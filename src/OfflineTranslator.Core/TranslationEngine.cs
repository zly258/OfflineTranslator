using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineTranslator.Core;

public static class InferenceDefaults
{
    public const int MaxTokens = 512;
    public const float Temperature = 0.0f;
    public const float TopP = 1.0f;
    public const int TopK = 1;
    public const float RepeatPenalty = 1.0f;
    public const float PresencePenalty = 0.0f;
    public const float FrequencyPenalty = 0.0f;
    public const int ContextSize = 1024;
    public const int GpuLayerCount = 0;
}

/// <summary>一次模型加载的规格（模型路径、线程数等）。</summary>
public sealed record EngineModelSpec(
    string ModelPath,
    int ContextSize = 1024,
    int GpuLayerCount = 0,
    string? ModelName = null,
    int? Threads = null)
{
    public string DisplayModelName => ModelName ?? (Directory.Exists(ModelPath)
        ? new DirectoryInfo(ModelPath).Name
        : Path.GetFileNameWithoutExtension(ModelPath));
}

/// <summary>一次翻译请求的全部参数，UI 与 OpenAI API 都向这个结构收敛。</summary>
public sealed record TranslationRequest
{
    /// <summary>原文（待翻译文本）。</summary>
    public required string Text { get; init; }

    /// <summary>用户传入的系统提示词或提示词上下文（如额外的术语对应、背景说明、角色设定等）。</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>源语言描述（如 "auto", "en", "zh", "ja" 等）。</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>目标语言描述（如 "zh", "en", "ja" 等）。</summary>
    public string? TargetLanguage { get; init; }

    /// <summary>快速模式（默认开启）。</summary>
    public bool FastMode { get; init; } = true;

    public float Temperature { get; init; }
    public float TopP { get; init; }
    public int MaxTokens { get; init; }
    public float RepeatPenalty { get; init; }
    public float PresencePenalty { get; init; }
    public float FrequencyPenalty { get; init; }

    public static TranslationRequest DefaultsFor(string text,
        string? source = null, string? target = null, bool fast = true) => new()
    {
        Text = text,
        SourceLanguage = source,
        TargetLanguage = target,
        FastMode = fast,
        Temperature = InferenceDefaults.Temperature,
        TopP = InferenceDefaults.TopP,
        RepeatPenalty = InferenceDefaults.RepeatPenalty,
        PresencePenalty = InferenceDefaults.PresencePenalty,
        FrequencyPenalty = InferenceDefaults.FrequencyPenalty,
        MaxTokens = InferenceDefaults.MaxTokens,
    };
}

/// <summary>
/// 支持把术语库交给推理侧的引擎。命中发生在生成之前，译文才能按词表落词。
/// </summary>
public interface IGlossaryAwareEngine
{
    /// <summary>把术语表同步给推理后端。</summary>
    Task SetGlossaryAsync(IReadOnlyList<TermEntry> entries, CancellationToken token = default);
}

/// <summary>
/// 支持持久化句段翻译记忆的引擎：相同原文/方向/提示词直接复用历史译文。
/// </summary>
public interface ITranslationMemoryAwareEngine
{
    void SetTranslationMemory(TranslationMemory? memory, string scope);
}

/// <summary>
/// 翻译引擎抽象：隔离具体翻译模型实现。
/// </summary>
public interface ITranslationEngine : IAsyncDisposable
{
    /// <summary>当前加载模型的名字。</summary>
    string ModelName { get; }

    /// <summary>模型文件源路径。</summary>
    string? SourcePath => null;

    /// <summary>推理使用的线程数。</summary>
    int Threads => 0;

    /// <summary>当前引擎与硬件运行概况（如后端、GPU offload 等）。</summary>
    string? RuntimeSummary => null;

    /// <summary>执行翻译并返回译文分片。</summary>
    IAsyncEnumerable<string> TranslateAsync(TranslationRequest request, CancellationToken token);

    /// <summary>卸载模型并释放资源。</summary>
    Task UnloadAsync(CancellationToken token = default);
}

/// <summary>
/// 智能分发引擎工厂：根据模型规格（2Bit 专有格式或标准通用 GGUF）自动路由到 HyMtEngine 或 LLamaEngine。
/// </summary>
public static class CompositeEngineFactory
{
    public static Task<ITranslationEngine> CreateAsync(EngineModelSpec spec, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var path = spec.ModelPath;
        var fileName = Path.GetFileName(path).ToLowerInvariant();

        // 腾讯 Hy-MT2 专有 2Bit 格式（Q2_0C 等），走内置纯 C# 推理引擎
        if (fileName.Contains("2bit") || fileName.Contains("stq1_0") || fileName.Contains("q2_0c"))
        {
            return HyMtEngine.CreateAsync(spec, token);
        }

        // 其它所有标准通用 GGUF 模型（Qwen、Hy-MT2 Q4/Q8、Llama 等），走 llama.cpp 原生引擎（支持 Vulkan GPU / CPU）
        return LLamaEngine.CreateAsync(spec, token);
    }
}

/// <summary>引擎工厂：由加载规格创建一个"已加载"的引擎实例。</summary>
public delegate Task<ITranslationEngine> EngineFactory(EngineModelSpec spec, CancellationToken token);

/// <summary>
/// 引擎宿主：负责创建、持有、串行化对引擎的访问。
/// </summary>
public sealed class TranslationEngineHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITranslationEngine? _engine;
    private bool _disposed;

    public ITranslationEngine? Engine => _engine;
    public bool IsModelLoaded => _engine is not null && !string.IsNullOrEmpty(_engine.ModelName);

    public async Task<ITranslationEngine> LoadAsync(EngineModelSpec spec, EngineFactory factory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(factory);
        if (!File.Exists(spec.ModelPath) && !Directory.Exists(spec.ModelPath))
            throw new FileNotFoundException("模型文件或目录不存在", spec.ModelPath);

        await _gate.WaitAsync(token);
        try
        {
            var previous = _engine;
            if (previous is not null)
            {
                try { await previous.UnloadAsync(token); }
                finally { await previous.DisposeAsync(); }
                _engine = null;
            }
            try
            {
                var engine = await factory(spec, token);
                _engine = engine;
                return engine;
            }
            catch
            {
                _engine = null;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ITranslationEngine RequireEngine()
        => _engine ?? throw new ModelNotLoadedException("尚未加载模型：请先在界面加载翻译模型，再执行翻译或启动 API。");

    public async Task UnloadAsync(CancellationToken token = default)
    {
        ITranslationEngine? engine;
        await _gate.WaitAsync(token);
        try
        {
            engine = _engine;
            _engine = null;
        }
        finally
        {
            _gate.Release();
        }

        if (engine is null) return;
        try
        {
            await engine.UnloadAsync(token);
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await UnloadAsync();
        _gate.Dispose();
    }
}

public sealed class ModelNotLoadedException : Exception
{
    public ModelNotLoadedException(string message) : base(message) { }
}
