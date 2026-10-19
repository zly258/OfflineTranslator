using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineTranslator.Core.Testing;

/// <summary>
/// 假翻译引擎：不触碰 LLamaSharp 原生库，按规则返回固定分片。
/// 供自检（--autotest）与单元测试使用，保证"免模型、免点击"的全链路自动化。
/// </summary>
public sealed class FakeTranslationEngine : ITranslationEngine
{
    /// <summary>全局记录最近一次收到的请求，供测试断言参数覆盖是否生效。</summary>
    public static TranslationRequest? LastRequest;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>自定义译文生成器；未设置时默认返回 "【FAKE 译文】" + 原文。</summary>
    public Func<TranslationRequest, string>? ResponseFactory { get; set; }

    /// <summary>每个分片的字符数（模拟流式输出）。</summary>
    public int ChunkSize { get; set; } = 6;

    /// <summary>设为 true 时翻译抛异常（测试 500 错误路径）。</summary>
    public bool ThrowOnTranslate { get; set; }

    public List<TranslationRequest> Requests { get; } = new();

    public string ModelName { get; private set; } = "";
    public string? SourcePath { get; private set; }
    public int Threads { get; private set; }
    public string? RuntimeSummary => "FAKE 引擎 (测试模式)";

    public Task LoadAsync(EngineModelSpec spec, CancellationToken token = default)
    {
        ModelName = spec.DisplayModelName;
        SourcePath = spec.ModelPath;
        Threads = spec.Threads ?? 0;
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> TranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(token);
        try
        {
            Requests.Add(request);
            LastRequest = request;
        }
        finally
        {
            _gate.Release();
        }

        if (ThrowOnTranslate) throw new InvalidOperationException("FAKE 引擎故障注入");

        var text = ResponseFactory?.Invoke(request) ?? $"【FAKE 译文】{request.Text}";
        for (var i = 0; i < text.Length; i += ChunkSize)
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return text.Substring(i, Math.Min(ChunkSize, text.Length - i));
        }
    }

    public Task UnloadAsync(CancellationToken token = default)
    {
        ModelName = "";
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
