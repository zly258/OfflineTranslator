using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineTranslator.Core;

public sealed class ChatCompletionRequestBody
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("messages")] public List<ChatMessageDto>? Messages { get; set; }
    [JsonPropertyName("prompt")] public string? Prompt { get; set; }
    [JsonPropertyName("system_prompt")] public string? SystemPrompt { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
    [JsonPropertyName("temperature")] public float? Temperature { get; set; }
    [JsonPropertyName("top_p")] public float? TopP { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("source_language")] public string? SourceLanguage { get; set; }
    [JsonPropertyName("target_language")] public string? TargetLanguage { get; set; }
}

public sealed class ChatMessageDto
{
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
}

/// <summary>
/// 基于标准 .NET TcpListener 的超轻量 OpenAI 兼容服务。
/// 完全使用底层 TCP Socket 解析 HTTP/1.1，避开 Windows http.sys 驱动在特定权限与 .NET 10 下出现的「句柄无效」问题，
/// 天然跨平台、免管理员权限、零外部依赖。
/// </summary>
public sealed class OpenAiApiService : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private static readonly byte[] HeaderEnd = "\r\n\r\n"u8.ToArray();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private Func<ITranslationEngine?> _engineProvider = () => null;
    private Func<string?> _modelNameProvider = () => null;
    private Func<TranslationRequest> _baselineProvider = () => throw new InvalidOperationException("baseline provider not set");

    public bool IsRunning => _listener is not null;
    public int RequestedPort { get; private set; }
    public string? BoundUrl { get; private set; }
    public int Port => RequestedPort;

    public event Action<string>? Log;

    public Task StartAsync(
        Func<ITranslationEngine?> engineProvider,
        Func<string?> modelNameProvider,
        Func<TranslationRequest> baselineProvider,
        int port,
        CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_listener is not null, this);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 0-65535 之间");

        _engineProvider = engineProvider;
        _modelNameProvider = modelNameProvider;
        _baselineProvider = baselineProvider;

        var actualPort = port == 0 ? FindFreePort() : port;
        RequestedPort = actualPort;

        _listener = new TcpListener(IPAddress.Loopback, actualPort);
        _listener.Start();

        BoundUrl = $"http://127.0.0.1:{actualPort}";
        _cts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenLoopAsync(_listener, _cts.Token));

        EmitLog($"OpenAI API 服务已在 {BoundUrl} 启动（TCP 监听 127.0.0.1）");
        return Task.CompletedTask;
    }

    private static int FindFreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var free = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return free;
    }

    private async Task ListenLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                    EmitLog("API 连接接入异常: " + ex.Message);
            }
        }
    }

    private sealed class HttpRequestInfo
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "/";
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = [];
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                var req = await ParseRequestAsync(stream, token).ConfigureAwait(false);
                if (req is null) return;

                if (req.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 204, "No Content", "text/plain", [], token).ConfigureAwait(false);
                    return;
                }

                if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    (req.Path.Equals("/health", StringComparison.OrdinalIgnoreCase) || req.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)))
                {
                    var healthJson = JsonSerializer.SerializeToUtf8Bytes(new { status = "ok", model = _modelNameProvider() }, JsonOpts);
                    await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", healthJson, token).ConfigureAwait(false);
                    return;
                }

                if (req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    req.Path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase))
                {
                    var model = _modelNameProvider() ?? "hy-mt2";
                    var modelsJson = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        @object = "list",
                        data = new[]
                        {
                            new
                            {
                                id = model,
                                @object = "model",
                                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                owned_by = "offline-translator",
                            }
                        }
                    }, JsonOpts);
                    await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", modelsJson, token).ConfigureAwait(false);
                    return;
                }

                if (req.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    req.Path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleChatCompletionsAsync(stream, req, token).ConfigureAwait(false);
                    return;
                }

                // 404
                var notFound = JsonSerializer.SerializeToUtf8Bytes(new { error = new { message = $"路径不存在: {req.Path}" } }, JsonOpts);
                await WriteResponseAsync(stream, 404, "Not Found", "application/json; charset=utf-8", notFound, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    EmitLog($"处理请求出错: {ex.Message}");
                    try
                    {
                        var err = JsonSerializer.SerializeToUtf8Bytes(new { error = new { message = ex.Message } }, JsonOpts);
                        await WriteResponseAsync(stream, 500, "Internal Server Error", "application/json; charset=utf-8", err, token).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
        }
    }

    private static async Task<HttpRequestInfo?> ParseRequestAsync(NetworkStream stream, CancellationToken token)
    {
        using var ms = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var headerEndPos = -1;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read <= 0) return null;
                ms.Write(buffer, 0, read);

                headerEndPos = FindHeaderEnd(ms.GetBuffer(), (int)ms.Length);
                if (headerEndPos >= 0) break;
                if (ms.Length > 65536) throw new InvalidOperationException("HTTP 请求头过大");
            }

            var rawBytes = ms.ToArray();
            var headerStr = Encoding.UTF8.GetString(rawBytes, 0, headerEndPos);
            var lines = headerStr.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return null;

            var firstLine = lines[0].Split(' ');
            if (firstLine.Length < 2) return null;

            var req = new HttpRequestInfo
            {
                Method = firstLine[0].Trim(),
                Path = firstLine[1].Trim()
            };

            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    var k = lines[i][..colon].Trim();
                    var v = lines[i][(colon + 1)..].Trim();
                    req.Headers[k] = v;
                }
            }

            var bodyStart = headerEndPos + 4; // 跳过 \r\n\r\n
            var reader = new RequestBodyReader(stream, rawBytes, bodyStart);

            if (req.Headers.TryGetValue("Transfer-Encoding", out var te)
                && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                req.Body = await ReadChunkedBodyAsync(reader, token).ConfigureAwait(false);
            }
            else if (req.Headers.TryGetValue("Content-Length", out var clStr)
                     && int.TryParse(clStr, out var contentLength) && contentLength > 0)
            {
                var bodyBytes = new byte[contentLength];
                await reader.ReadExactAsync(bodyBytes, token).ConfigureAwait(false);
                req.Body = bodyBytes;
            }

            return req;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    /// <summary>请求体读取器：先消费与请求头一起到达的字节，再从网络流继续读。</summary>
    private sealed class RequestBodyReader(Stream stream, byte[] buffered, int offset)
    {
        private int _offset = offset;

        public async Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken token)
        {
            if (_offset < buffered.Length)
            {
                var take = Math.Min(count, buffered.Length - _offset);
                Buffer.BlockCopy(buffered, _offset, buffer, start, take);
                _offset += take;
                return take;
            }
            return await stream.ReadAsync(buffer.AsMemory(start, count), token).ConfigureAwait(false);
        }

        public async Task ReadExactAsync(byte[] buffer, CancellationToken token)
        {
            var done = 0;
            while (done < buffer.Length)
            {
                var read = await ReadAsync(buffer, done, buffer.Length - done, token).ConfigureAwait(false);
                if (read <= 0) throw new IOException("请求体提前结束");
                done += read;
            }
        }

        /// <summary>读一行（以 CRLF 结束），返回不含 CRLF 的内容。</summary>
        public async Task<string> ReadLineAsync(CancellationToken token)
        {
            var bytes = new List<byte>(64);
            var one = new byte[1];
            while (true)
            {
                var read = await ReadAsync(one, 0, 1, token).ConfigureAwait(false);
                if (read <= 0) throw new IOException("请求体提前结束");
                if (one[0] == (byte)'\n')
                {
                    if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }
                bytes.Add(one[0]);
            }
        }
    }

    /// <summary>按 HTTP/1.1 分块传输（Transfer-Encoding: chunked）解码请求体。</summary>
    private static async Task<byte[]> ReadChunkedBodyAsync(RequestBodyReader reader, CancellationToken token)
    {
        using var output = new MemoryStream();
        while (true)
        {
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line.Length == 0) continue;

            var semi = line.IndexOf(';');
            var sizeText = (semi >= 0 ? line[..semi] : line).Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new InvalidOperationException("非法的 chunk 长度: " + line);

            if (size == 0)
            {
                // 丢弃 trailer 直到空行
                while ((await reader.ReadLineAsync(token).ConfigureAwait(false)).Length > 0) { }
                break;
            }

            var chunk = new byte[size];
            await reader.ReadExactAsync(chunk, token).ConfigureAwait(false);
            output.Write(chunk, 0, size);
            await reader.ReadLineAsync(token).ConfigureAwait(false); // chunk 结尾 CRLF
        }
        return output.ToArray();
    }

    private async Task HandleChatCompletionsAsync(NetworkStream stream, HttpRequestInfo req, CancellationToken token)
    {
        var engine = _engineProvider();
        if (engine is null)
        {
            var err = JsonSerializer.SerializeToUtf8Bytes(new { error = new { message = "未加载模型，无法处理翻译请求" } }, JsonOpts);
            await WriteResponseAsync(stream, 503, "Service Unavailable", "application/json; charset=utf-8", err, token).ConfigureAwait(false);
            return;
        }

        ChatCompletionRequestBody? body;
        try
        {
            body = JsonSerializer.Deserialize<ChatCompletionRequestBody>(req.Body, JsonOpts);
        }
        catch (Exception ex)
        {
            var err = JsonSerializer.SerializeToUtf8Bytes(new { error = new { message = "请求体 JSON 格式不合法: " + ex.Message } }, JsonOpts);
            await WriteResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", err, token).ConfigureAwait(false);
            return;
        }

        if (body is null || ((body.Messages is null || body.Messages.Count == 0) && string.IsNullOrWhiteSpace(body.Prompt)))
        {
            var err = JsonSerializer.SerializeToUtf8Bytes(new { error = new { message = "messages 或 prompt 字段不能为空" } }, JsonOpts);
            await WriteResponseAsync(stream, 400, "Bad Request", "application/json; charset=utf-8", err, token).ConfigureAwait(false);
            return;
        }

        // 提取待翻译文本与系统提示词（使用者可能把术语放在 system 提示词中）
        string rawText = string.Empty;
        var systemPrompts = new List<string>();

        if (!string.IsNullOrWhiteSpace(body.SystemPrompt))
            systemPrompts.Add(body.SystemPrompt.Trim());

        if (body.Messages is not null)
        {
            foreach (var m in body.Messages)
            {
                if (m.Role?.Equals("system", StringComparison.OrdinalIgnoreCase) == true && !string.IsNullOrWhiteSpace(m.Content))
                {
                    systemPrompts.Add(m.Content.Trim());
                }
            }

            var userMessage = body.Messages.LastOrDefault(m => m.Role?.Equals("user", StringComparison.OrdinalIgnoreCase) == true)
                              ?? body.Messages.LastOrDefault(m => !m.Role?.Equals("system", StringComparison.OrdinalIgnoreCase) == true);
            if (userMessage is not null)
                rawText = userMessage.Content ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(rawText) && !string.IsNullOrWhiteSpace(body.Prompt))
        {
            rawText = body.Prompt.Trim();
        }

        var combinedSystemPrompt = systemPrompts.Count > 0 ? string.Join("\n\n", systemPrompts) : null;

        var request = new TranslationRequest
        {
            Text = rawText,
            SystemPrompt = combinedSystemPrompt,
            SourceLanguage = body.SourceLanguage,
            TargetLanguage = body.TargetLanguage ?? "zh",
            MaxTokens = body.MaxTokens ?? 512,
        };

        var modelName = _modelNameProvider() ?? "hy-mt2";
        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (body.Stream)
        {
            // 流式响应 (SSE)
            var sseHeader = "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: text/event-stream; charset=utf-8\r\n" +
                            "Cache-Control: no-cache\r\n" +
                            "Connection: close\r\n" +
                            "Access-Control-Allow-Origin: *\r\n" +
                            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                            "Access-Control-Allow-Headers: Content-Type, Authorization\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(sseHeader), token).ConfigureAwait(false);

            await foreach (var chunk in engine.TranslateAsync(request, token).ConfigureAwait(false))
            {
                var sseChunk = new
                {
                    id,
                    @object = "chat.completion.chunk",
                    created,
                    model = modelName,
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new { content = chunk },
                            finish_reason = (string?)null
                        }
                    }
                };
                var pieceStr = $"data: {JsonSerializer.Serialize(sseChunk, JsonOpts)}\n\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(pieceStr), token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }

            var endChunk = new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model = modelName,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { },
                        finish_reason = "stop"
                    }
                }
            };
            var endStr = $"data: {JsonSerializer.Serialize(endChunk, JsonOpts)}\n\ndata: [DONE]\n\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(endStr), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        else
        {
            // 非流式响应
            var sb = new StringBuilder();
            await foreach (var chunk in engine.TranslateAsync(request, token).ConfigureAwait(false))
            {
                sb.Append(chunk);
            }

            var respData = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id,
                @object = "chat.completion",
                created,
                model = modelName,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new
                        {
                            role = "assistant",
                            content = sb.ToString()
                        },
                        finish_reason = "stop"
                    }
                }
            }, JsonOpts);

            await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", respData, token).ConfigureAwait(false);
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string statusReason, string contentType, byte[] body, CancellationToken token)
    {
        var header = $"HTTP/1.1 {statusCode} {statusReason}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                     "Access-Control-Allow-Headers: Content-Type, Authorization\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);
        await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, token).ConfigureAwait(false);
        }
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private void EmitLog(string message) => Log?.Invoke(message);

    public ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch { }
        finally
        {
            _cts?.Dispose();
            _listener = null;
        }
        return ValueTask.CompletedTask;
    }
}
