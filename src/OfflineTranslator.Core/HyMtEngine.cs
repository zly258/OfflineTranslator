using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Sdcb.HyMT2Sharp.Backends;
using Sdcb.HyMT2Sharp.Model;

namespace OfflineTranslator.Core;

/// <summary>
/// 从 models 目录里的 Hy-MT2 GGUF 做进程内翻译。
/// 有显卡时走 Vulkan，否则走 CPU。整句生成完再返回，术语按官方提示注入。
/// </summary>
public sealed class HyMtEngine : ITranslationEngine, IGlossaryAwareEngine, ITranslationMemoryAwareEngine
{
    private readonly HunyuanDenseModel _model;
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

    private HyMtEngine(HunyuanDenseModel model, string modelName, string sourcePath, int threads, string backendName, string? backendWarning)
    {
        _model = model;
        ModelName = modelName;
        SourcePath = sourcePath;
        Threads = threads;
        RuntimeSummary = $"后端 {backendName}，线程 {model.ThreadCount}（{model.ThreadAutoHint}）";
        if (!string.IsNullOrEmpty(backendWarning))
            RuntimeSummary += $"；GPU 不可用，已回退 CPU：{backendWarning}";
    }

    public static Task<ITranslationEngine> CreateAsync(EngineModelSpec spec, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Task.Run(() => (ITranslationEngine)Load(spec, token), token);
    }

    private static HyMtEngine Load(EngineModelSpec spec, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var threads = spec.Threads ?? 0;
        var backendName = "cpu";
        IComputeBackend? backend = null;
        try
        {
            backend = BackendFactory.Create();
            if (backend is not null) backendName = backend.GetType().Name.Replace("Backend", "", StringComparison.Ordinal).ToLowerInvariant();
        }
        catch
        {
            backend = null;
            backendName = "cpu";
        }

        HunyuanDenseModel model;
        string? backendWarning = null;
        try
        {
            model = Open(spec, threads, backend);
        }
        catch (Exception ex) when (backend is not null)
        {
            // 显卡后端打不开时退回 CPU。失败的构造函数会自己释放 backend。
            backendWarning = Summarize(ex.Message);
            backendName = "cpu";
            model = Open(spec, threads, null);
        }

        try
        {
            Warmup(model);
        }
        catch
        {
            model.Dispose();
            throw;
        }

        return new HyMtEngine(model, spec.DisplayModelName, spec.ModelPath, threads, backendName, backendWarning);
    }

    private static string Summarize(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    private static HunyuanDenseModel Open(EngineModelSpec spec, int threads, IComputeBackend? backend)
    {
        var file = ModelScanner.ResolveModelFile(spec.ModelPath);
        // Memory 策略保留 KV 缓冲，跨请求才能复用提示词前缀（不再每次请求都重新 prefill）。
        return new HunyuanDenseModel(file, threads, KvCacheConfig.Memory, backend);
    }

    /// <summary>跑一小段 prefill，把内核校准和首次编译留在加载阶段。</summary>
    private static void Warmup(HunyuanDenseModel model)
    {
        var prompt = model.Tokenizer.Encode(ChatTemplate.RenderHunyuanDense(
            [new ChatMessage("user", "将以下文本翻译为英语，注意只需要输出翻译后的结果，不要额外解释：\n\n测试")]));
        _ = model.Forward(model.AlignPrompt(prompt).Suffix);
        model.EndRequest();
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

        // 消费方提前停止（如 API 客户端断开）时，通过 linked 取消生产端，避免继续空转推理。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var run = Task.Run(() => Produce(channel.Writer, request, linked.Token));
        // Produce 自己吞掉异常并写进 channel。这里只观察 Task 本身的失败，避免 yield 落进 catch。
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

    private void Produce(ChannelWriter<string> writer, TranslationRequest request, CancellationToken token)
    {
        try
        {
            _gate.Wait(token);
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
                        var prompt = HyMtPrompt.Build(chunk, tgt, terms, request.SystemPrompt);
                        var translated = new System.Text.StringBuilder();
                        foreach (var piece in Generate(prompt, TokenBudget(chunk, request), token))
                        {
                            translated.Append(piece);
                            if (!writer.TryWrite(piece)) return;
                        }

                        if (memoryKey is not null) memory!.Put(memoryKey, translated.ToString());
                    }
                }
            }
            finally
            {
                // 不再调用 EndRequest：保留 KV 前缀，下一次请求可直接复用提示词部分。
                _gate.Release();
            }

            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    /// <summary>逐 token 产出可见文本，调用方边生成边写 channel。</summary>
    private IEnumerable<string> Generate(string userPrompt, int maxTokens, CancellationToken token)
    {
        var ids = _model.Tokenizer.Encode(ChatTemplate.RenderHunyuanDense(
            [new ChatMessage("user", userPrompt.ReplaceLineEndings("\n"))]));
        var logits = _model.Forward(_model.AlignPrompt(ids).Suffix);

        var generated = new List<int>(Math.Min(maxTokens, 256));
        var visible = string.Empty;
        // 行首空白直接丢弃；行尾空白先挂起，等下一个 token 确认后再决定是否输出。
        var pending = string.Empty;
        var started = false;

        for (var i = 0; i < maxTokens; i++)
        {
            token.ThrowIfCancellationRequested();
            var tokenId = Sampler.ArgMax(logits);
            if (_model.Tokenizer.IsStop(tokenId)) break;

            generated.Add(tokenId);
            var next = _model.Tokenizer.DecodeVisible(generated).TrimEnd('\uFFFD');
            if (next.Length > visible.Length && next.StartsWith(visible, StringComparison.Ordinal))
            {
                pending += next[visible.Length..];
                visible = next;

                var lastNonSpace = pending.Length - 1;
                while (lastNonSpace >= 0 && char.IsWhiteSpace(pending[lastNonSpace]))
                    lastNonSpace--;

                if (lastNonSpace >= 0)
                {
                    var emit = pending[..(lastNonSpace + 1)];
                    pending = pending[(lastNonSpace + 1)..];
                    if (!started)
                    {
                        emit = emit.TrimStart();
                        started = emit.Length > 0;
                    }
                    if (emit.Length > 0) yield return emit;
                }
            }

            logits = _model.Forward([tokenId]);
        }
    }

    private static int TokenBudget(string segment, TranslationRequest request)
    {
        var floor = request.MaxTokens > 0 ? request.MaxTokens : 256;
        return Math.Clamp(Math.Max(floor, segment.Length), 48, 1024);
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
        try { _model.Dispose(); } catch { }
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
