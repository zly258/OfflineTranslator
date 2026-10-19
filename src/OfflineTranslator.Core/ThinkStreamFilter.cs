using System;
using System.Text;

namespace OfflineTranslator.Core;

/// <summary>
/// 思考标签（如 &lt;think&gt;...&lt;/think&gt; 或 &lt;thought&gt;...&lt;/thought&gt;）流式过滤器。
/// 确保无论模型是否具有推理链偏好，流式输出对外吐出的分片 100% 纯净为译文正文，
/// 绝不夹带任何内部思考过程。
/// </summary>
public sealed class ThinkStreamFilter
{
    private bool _inThinking;
    private bool _finishedThinking;
    private readonly StringBuilder _buffer = new();

    /// <summary>送入模型新吐出的 token 片段，返回应安全对外输出的纯净译文字符串。</summary>
    public string Feed(string piece)
    {
        if (string.IsNullOrEmpty(piece) && !_inThinking) return string.Empty;
        if (!string.IsNullOrEmpty(piece)) _buffer.Append(piece);
        var current = _buffer.ToString();

        // 1. 如果还在等待判断或处于思考块内部
        if (!_finishedThinking)
        {
            if (_inThinking)
            {
                var closeIdx = FindCloseTag(current, out var tagLen);
                if (closeIdx >= 0)
                {
                    _inThinking = false;
                    _finishedThinking = true;
                    var remaining = current[(closeIdx + tagLen)..].TrimStart('\r', '\n');
                    _buffer.Clear();
                    return remaining;
                }

                // 仍处于思维链中：保留末尾 32 字符以防闭合标签被跨 token 拆断
                if (_buffer.Length > 64)
                {
                    var tail = _buffer.ToString(_buffer.Length - 32, 32);
                    _buffer.Clear();
                    _buffer.Append(tail);
                }
                return string.Empty;
            }

            var trimmed = current.TrimStart();
            if (trimmed.StartsWith("<think", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<thought", StringComparison.OrdinalIgnoreCase))
            {
                var gt = trimmed.IndexOf('>');
                if (gt >= 0)
                {
                    // 确认进入思考状态
                    _inThinking = true;
                    var afterTag = trimmed[(gt + 1)..];
                    _buffer.Clear();
                    _buffer.Append(afterTag);
                    return Feed(string.Empty);
                }

                // 标签未接收完整（如只到了 "<thin"），保留在 buffer 中继续等待
                return string.Empty;
            }

            // 如果已经接收到非空白字符且不是以 '<' 开头，或者长度超过 16 仍不是思考标签，判定本轮生成无思考标签
            if (!trimmed.StartsWith('<') || trimmed.Length >= 16)
            {
                _finishedThinking = true;
            }
            else
            {
                // 以 '<' 开头但不足以判断标签，等待下一个 token
                return string.Empty;
            }
        }

        // 2. 正常输出正文（安全剔除任何可能残留的闭合标签）
        _buffer.Clear();
        if (current.Contains("</think>", StringComparison.OrdinalIgnoreCase))
            current = current.Replace("</think>", "", StringComparison.OrdinalIgnoreCase);
        if (current.Contains("</thought>", StringComparison.OrdinalIgnoreCase))
            current = current.Replace("</thought>", "", StringComparison.OrdinalIgnoreCase);
        return current;
    }

    /// <summary>流生成结束时的清理。</summary>
    public string Flush()
    {
        if (_inThinking)
        {
            _buffer.Clear();
            return string.Empty;
        }
        var remaining = _buffer.ToString();
        _buffer.Clear();
        return remaining;
    }

    private static int FindCloseTag(string text, out int tagLen)
    {
        tagLen = 0;
        var idx = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) { tagLen = 8; return idx; }
        idx = text.IndexOf("</thought>", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) { tagLen = 10; return idx; }
        return -1;
    }
}
