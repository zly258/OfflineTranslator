using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineTranslator.Core.Models;

namespace OfflineTranslator.Core;

/// <summary>
/// 服务门面：模型扫描 / 加载 / 卸载 / 翻译 / OpenAI 兼容 API 启停 / 日志事件。
/// </summary>
public sealed class TranslationService : IAsyncDisposable
{
    private readonly TranslationEngineHost _host = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private OpenAiApiService? _api;

    // 贪心解码是确定性的，同一原文/方向/提示词的结果可直接复用。
    // Inventor 里大量重复的界面字符串会因此瞬间返回。
    private const int MaxCacheEntries = 512;
    private const int MaxCacheChars = 2_000_000;
    private readonly Dictionary<string, string> _translationCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _translationCacheOrder = new();
    private readonly object _translationCacheLock = new();
    private int _translationCacheChars;

    // 跨会话的句段级翻译记忆（重复的界面字符串、重复文档）。
    private readonly TranslationMemory _memory = new();
    private EngineModelSpec? _currentSpec;

    public GlossaryService Glossary { get; } = new();

    public event Action<string>? Log;

    public EngineFactory EngineFactory { get; init; } = CompositeEngineFactory.CreateAsync;

    public bool IsModelLoaded => _host.IsModelLoaded;
    public string? ModelName => _host.Engine?.ModelName;
    public string? EngineRuntimeSummary => _host.Engine?.RuntimeSummary;
    public bool IsApiRunning => _api is not null;
    public int ApiPort => _api?.Port ?? 0;
    public string? ApiUrl => _api?.BoundUrl;

    public IReadOnlyList<ModelInfo> ScanModels(string modelsDirectory)
    {
        var models = ModelScanner.Scan(modelsDirectory);
        EmitLog(models.Count == 0
            ? $"models 目录（{modelsDirectory}）未发现模型"
            : $"扫描到 {models.Count} 个模型：{string.Join("、", models.Select(m => m.FileName))}");
        return models;
    }

    public async Task LoadModelAsync(EngineModelSpec spec, CancellationToken token = default)
    {
        await _lifecycleGate.WaitAsync(token);
        try
        {
            if (_host.IsModelLoaded && _host.Engine is { } current
                && string.Equals(current.SourcePath, spec.ModelPath, StringComparison.OrdinalIgnoreCase)
                && current.Threads == (spec.Threads ?? 0))
            {
                EmitLog($"模型 {ModelName} 已处于加载状态，直接复用");
                _currentSpec = spec;
                ApplyMemory();
                await SyncGlossaryAsync(token);
                return;
            }

            if (_host.IsModelLoaded)
            {
                EmitLog($"切换模型：先卸载当前模型 {ModelName}");
                await StopApiIfRunningAsync();
                await _host.UnloadAsync(token);
            }

            EmitLog($"开始加载模型 {spec.DisplayModelName} …");
            var sw = Stopwatch.StartNew();
            await _host.LoadAsync(spec, EngineFactory, token);
            ClearCache();
            _currentSpec = spec;
            ApplyMemory();
            await SyncGlossaryAsync(token);
            EmitLog($"模型 {ModelName} 加载完成，耗时 {sw.ElapsedMilliseconds} ms");
            if (_host.Engine?.RuntimeSummary is { } summary)
                EmitLog(summary);
        }
        catch (OperationCanceledException)
        {
            EmitLog("模型加载已取消");
            throw;
        }
        catch (Exception ex)
        {
            EmitLog("模型加载失败：" + ex.Message);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task UnloadModelAsync(CancellationToken token = default)
    {
        await _lifecycleGate.WaitAsync(token);
        try
        {
            if (!_host.IsModelLoaded)
            {
                EmitLog("没有已加载的模型，无需卸载");
                return;
            }
            await StopApiIfRunningAsync();
            EmitLog($"正在卸载模型 {ModelName} …");
            await _host.UnloadAsync(token);
            ClearCache();
            _currentSpec = null;
            EmitLog("模型已卸载，内存与引擎资源已释放");
        }
        catch (Exception ex)
        {
            EmitLog("模型卸载异常：" + ex.Message);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var engine = _host.RequireEngine();
        var cacheKey = BuildCacheKey(request);
        if (TryGetCached(cacheKey, out var cached))
        {
            EmitLog($"缓存命中：{request.Text.Length} 字符，直接返回");
            yield return cached;
            yield break;
        }

        await SyncGlossaryAsync(token).ConfigureAwait(false);

        // 整句与术语库完全一致时直接给出规范译名，不再送模型。
        if (Glossary.TryTranslate(request.Text, request.SourceLanguage, request.TargetLanguage, out var direct))
        {
            EmitLog($"术语库命中（整句直出）：{request.Text.Trim()} → {direct}");
            yield return direct;
            yield break;
        }

        EmitLog($"开始翻译：{request.Text.Length} 字符，方向 {request.SourceLanguage ?? "auto"} → {request.TargetLanguage ?? "zh"}");
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        await foreach (var chunk in engine.TranslateAsync(request, token).ConfigureAwait(false))
        {
            sb.Append(chunk);
            yield return chunk;
        }
        StoreCached(cacheKey, sb.ToString());
        EmitLog($"翻译完成：耗时 {sw.ElapsedMilliseconds} ms");
    }

    private string BuildCacheKey(TranslationRequest request) => string.Join('\u0001',
        ModelName ?? string.Empty,
        Glossary.Version.ToString(CultureInfo.InvariantCulture),
        request.SourceLanguage ?? string.Empty,
        request.TargetLanguage ?? string.Empty,
        request.MaxTokens.ToString(CultureInfo.InvariantCulture),
        request.SystemPrompt ?? string.Empty,
        request.Text);

    private bool TryGetCached(string key, out string value)
    {
        lock (_translationCacheLock)
            return _translationCache.TryGetValue(key, out value!);
    }

    private void StoreCached(string key, string value)
    {
        if (value.Length == 0 || value.Length > MaxCacheChars) return;
        lock (_translationCacheLock)
        {
            if (_translationCache.ContainsKey(key)) return;
            _translationCache[key] = value;
            _translationCacheOrder.Enqueue(key);
            _translationCacheChars += value.Length;

            while (_translationCacheOrder.Count > MaxCacheEntries || _translationCacheChars > MaxCacheChars)
            {
                var oldest = _translationCacheOrder.Dequeue();
                if (_translationCache.Remove(oldest, out var removed))
                    _translationCacheChars -= removed.Length;
            }
        }
    }

    private void ClearCache()
    {
        lock (_translationCacheLock)
        {
            _translationCache.Clear();
            _translationCacheOrder.Clear();
            _translationCacheChars = 0;
        }
    }

    /// <summary>把当前术语库同步给推理后端（引擎不支持术语时跳过）。</summary>
    private async Task SyncGlossaryAsync(CancellationToken token)
    {
        if (_host.Engine is IGlossaryAwareEngine aware)
        {
            var entries = Glossary.Entries;
            await aware.SetGlossaryAsync(entries, token).ConfigureAwait(false);
        }
        ApplyMemory();
    }

    /// <summary>把翻译记忆和当前 scope 交给引擎（引擎不支持时跳过）。</summary>
    private void ApplyMemory()
    {
        if (_host.Engine is not ITranslationMemoryAwareEngine aware) return;
        aware.SetTranslationMemory(_memory, BuildMemoryScope());
    }

    /// <summary>scope 包含模型文件身份与术语库内容哈希，两者变化后旧记忆自动失效。</summary>
    private string BuildMemoryScope()
    {
        var spec = _currentSpec;
        if (spec is null) return string.Empty;
        try
        {
            var path = spec.ModelPath;
            if (Directory.Exists(path)) path = ModelScanner.ResolveModelFile(path);
            var info = new FileInfo(path);
            return $"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Glossary.ContentHash}";
        }
        catch
        {
            return $"{spec.DisplayModelName}|{Glossary.ContentHash}";
        }
    }

    public async Task StartApiAsync(int port, CancellationToken token = default)
    {
        if (!_host.IsModelLoaded)
            throw new ModelNotLoadedException("必须先加载模型才能启动 API 服务");
        if (_api is not null)
            throw new InvalidOperationException("API 服务已在运行");

        await _lifecycleGate.WaitAsync(token);
        try
        {
            var api = new OpenAiApiService();
            api.Log += EmitLog;
            await api.StartAsync(() => _host.Engine, () => ModelName, DefaultApiBaseline, port, token);
            _api = api;
        }
        catch (Exception ex)
        {
            EmitLog("API 启动失败：" + ex.Message);
            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private TranslationRequest DefaultApiBaseline() => TranslationRequest.DefaultsFor(string.Empty);

    public async Task StopApiAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopApiCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopApiIfRunningAsync()
    {
        if (_api is not null) await StopApiCoreAsync();
    }

    private async Task StopApiCoreAsync()
    {
        var api = _api;
        if (api is null) return;
        _api = null;
        await api.DisposeAsync();
    }

    private void EmitLog(string message) => Log?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _lifecycleGate.WaitAsync();
            try
            {
                await StopApiCoreAsync();
            }
            finally
            {
                _lifecycleGate.Release();
            }
            await _host.DisposeAsync();
        }
        catch (Exception ex)
        {
            EmitLog("释放资源异常：" + ex.Message);
        }
        finally
        {
            _memory.Dispose();
        }
    }
}
