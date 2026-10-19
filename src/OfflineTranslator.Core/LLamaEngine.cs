using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;

namespace OfflineTranslator.Core;

/// <summary>
/// 基于 llama.cpp (LLamaSharp) 的通用 GGUF 翻译引擎。
/// 支持 Qwen、Hy-MT2 (标准 GGUF)、Llama 等多种开源模型，支持 Vulkan / CUDA 硬件加速并自动 fallback 至 CPU。
/// </summary>
public sealed class LLamaEngine : ITranslationEngine, IGlossaryAwareEngine, ITranslationMemoryAwareEngine
{
    private readonly LLamaWeights _weights;
    private readonly ModelParams _modelParams;
    private readonly StatelessExecutor _executor;
    private readonly IPromptFormatter _formatter;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _glossaryLock = new();
    private readonly object _memoryLock = new();

    private List<TermEntry> _glossary = [];
    private TranslationMemory? _memory;
    private string _memoryScope = string.Empty;
    private bool _disposed;

    public string ModelName { get; }
    public string SourcePath { get; }
    public int Threads { get; }
    public string RuntimeSummary { get; }

    private LLamaEngine(
        LLamaWeights weights,
        ModelParams modelParams,
        StatelessExecutor executor,
        IPromptFormatter formatter,
        string modelName,
        string sourcePath,
        int threads,
        string runtimeSummary)
    {
        _weights = weights;
        _modelParams = modelParams;
        _executor = executor;
        _formatter = formatter;
        ModelName = modelName;
        SourcePath = sourcePath;
        Threads = threads;
        RuntimeSummary = runtimeSummary;
    }

    public static Task<ITranslationEngine> CreateAsync(EngineModelSpec spec, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Task.Run(() => (ITranslationEngine)Load(spec, token), token);
    }

    private static bool _libraryConfigured;
    private static readonly object _configLock = new();

    private static void EnsureNativeConfig()
    {
        if (_libraryConfigured) return;
        lock (_configLock)
        {
            if (_libraryConfigured) return;
            try
            {
                NativeLibraryConfig.All.WithVulkan(true);
            }
            catch { }
            _libraryConfigured = true;
        }
    }

    private static LLamaEngine Load(EngineModelSpec spec, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureNativeConfig();

        var modelFile = ModelScanner.ResolveModelFile(spec.ModelPath);
        var formatter = PromptFormatterFactory.ForModel(modelFile);
        var threads = spec.Threads ?? 0;
        var threadCount = threads > 0 ? (uint)threads : (uint)Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

        LLamaWeights weights;
        ModelParams modelParams;
        string runtimeSummary;

        try
        {
            var gpuParams = new ModelParams(modelFile)
            {
                ContextSize = 2048,
                GpuLayerCount = 99,
                BatchSize = 512,
                Threads = (int)threadCount,
            };
            weights = LLamaWeights.LoadFromFile(gpuParams);
            modelParams = gpuParams;
            runtimeSummary = $"引擎 llama.cpp，后端 Vulkan (GPU 加速)，线程 {threadCount}";
        }
        catch (Exception exGpu)
        {
            try
            {
                var cpuParams = new ModelParams(modelFile)
                {
                    ContextSize = 2048,
                    GpuLayerCount = 0,
                    BatchSize = 512,
                    Threads = (int)threadCount,
                };
                weights = LLamaWeights.LoadFromFile(cpuParams);
                modelParams = cpuParams;
                var reason = exGpu.Message.Split('\n', 2)[0].Trim();
                if (reason.Length > 100) reason = reason[..100] + "…";
                runtimeSummary = $"引擎 llama.cpp，后端 CPU，线程 {threadCount}（GPU 失败回退: {reason}）";
            }
            catch
            {
                throw;
            }
        }

        var executor = new StatelessExecutor(weights, modelParams);
        return new LLamaEngine(
            weights,
            modelParams,
            executor,
            formatter,
            spec.DisplayModelName,
            spec.ModelPath,
            (int)threadCount,
            runtimeSummary);
    }

    public Task SetGlossaryAsync(IReadOnlyList<TermEntry> entries, CancellationToken token = default)
    {
        var copy = new List<TermEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Source) && !string.IsNullOrWhiteSpace(entry.Target))
                copy.Add(entry);
        }

        lock (_glossaryLock)
        {
            _glossary = copy;
        }
        return Task.CompletedTask;
    }

    public void SetTranslationMemory(TranslationMemory? memory, string scope)
    {
        lock (_memoryLock)
        {
            _memory = memory;
            _memoryScope = scope ?? string.Empty;
        }
    }

    public async IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            yield break;

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var run = Task.Run(() => Produce(channel.Writer, request, linked.Token));
        _ = run.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await foreach (var piece in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                yield return piece;
        }
        finally
        {
            linked.Cancel();
        }
    }

    private async Task Produce(ChannelWriter<string> writer, TranslationRequest request, CancellationToken token)
    {
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var src = NormalizeLang(request.SourceLanguage);
                var tgt = NormalizeLang(request.TargetLanguage);
                if (src == "auto") src = LanguageDetector.DetectLanguage(request.Text);
                if (tgt is "auto" or "") tgt = "zh";

                if (src == tgt)
                {
                    writer.TryWrite(request.Text);
                    return;
                }

                List<TermEntry> glossary;
                lock (_glossaryLock) { glossary = _glossary; }

                TranslationMemory? memory;
                string memoryScope;
                lock (_memoryLock)
                {
                    memory = _memory;
                    memoryScope = _memoryScope;
                }

                var lines = request.Text.ReplaceLineEndings("\n").Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (i > 0 && !writer.TryWrite("\n")) return;

                    var line = lines[i];
                    if (line.Length == 0) continue;

                    foreach (var chunk in SplitLong(line, 600))
                    {
                        token.ThrowIfCancellationRequested();
                        if (chunk.Length == 0) continue;

                        var memoryKey = memory is null || memoryScope.Length == 0
                            ? null
                            : TranslationMemory.ComputeKey(memoryScope, src, tgt, request.SystemPrompt, request.MaxTokens, chunk);

                        if (memoryKey is not null && memory!.TryGet(memoryKey, out var cached))
                        {
                            if (!writer.TryWrite(cached)) return;
                            continue;
                        }

                        var terms = HyMtPrompt.MatchTerms(chunk, glossary, src, tgt);
                        var prompt = _formatter.BuildPrompt(chunk, tgt, terms, request.SystemPrompt);
                        var maxTokens = request.MaxTokens > 0 ? request.MaxTokens : 512;

                        var inferenceParams = new InferenceParams
                        {
                            MaxTokens = maxTokens,
                            AntiPrompts = _formatter.AntiPrompts,
                            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
                            {
                                Temperature = 0.0f,
                                TopP = 1.0f,
                            }
                        };

                        var translated = new StringBuilder();
                        var started = false;
                        var thinkFilter = new ThinkStreamFilter();

                        await foreach (var piece in _executor.InferAsync(prompt, inferenceParams, token).ConfigureAwait(false))
                        {
                            token.ThrowIfCancellationRequested();
                            var filtered = thinkFilter.Feed(piece);
                            if (string.IsNullOrEmpty(filtered)) continue;

                            var emit = filtered;
                            if (!started)
                            {
                                emit = emit.TrimStart();
                                started = emit.Length > 0;
                            }
                            if (emit.Length > 0)
                            {
                                translated.Append(emit);
                                if (!writer.TryWrite(emit)) return;
                            }
                        }

                        var tail = thinkFilter.Flush();
                        if (!string.IsNullOrEmpty(tail))
                        {
                            if (!started) tail = tail.TrimStart();
                            if (tail.Length > 0)
                            {
                                translated.Append(tail);
                                writer.TryWrite(tail);
                            }
                        }

                        if (memoryKey is not null)
                        {
                            var finalResult = translated.ToString().Trim();
                            if (finalResult.Length > 0) memory!.Put(memoryKey, finalResult);
                        }
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    private static List<string> SplitLong(string line, int maxChars)
    {
        if (line.Length <= maxChars) return [line];

        var parts = new List<string>();
        var start = 0;
        while (start < line.Length)
        {
            var take = Math.Min(maxChars, line.Length - start);
            if (start + take < line.Length)
            {
                var window = line.AsSpan(start, take);
                var cut = window.LastIndexOfAny("。！？；;.!?\n");
                if (cut > maxChars / 3) take = cut + 1;
            }
            parts.Add(line.Substring(start, take));
            start += take;
        }
        return parts;
    }

    private static string NormalizeLang(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "auto";
        var key = code.Trim().ToLowerInvariant().Replace('_', '-');
        if (key is "zh-cn" or "zh-hans" or "zh-sg") return "zh";
        if (key is "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" or "zht") return "zht";
        return key;
    }

    public Task UnloadAsync(CancellationToken token = default) => DisposeAsync().AsTask();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try { _weights.Dispose(); } catch { }
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
