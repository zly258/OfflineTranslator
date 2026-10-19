using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OfflineTranslator.Core;

/// <summary>
/// 持久化的句段翻译记忆。贪心解码是确定性的，相同原文/方向/提示词可以直接复用译文。
/// JSONL 追加写，启动时载入；条目数或体积超限时丢弃最旧记录并压缩文件。
/// 模型与术语库通过 scope 隔离，scope 变化后旧条目不会再命中。
/// </summary>
public sealed class TranslationMemory : IDisposable
{
    private const int MaxEntries = 20_000;
    private const int MaxBytes = 24 * 1024 * 1024;
    private const int FlushEvery = 16;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private StreamWriter? _writer;
    private int _pending;
    private long _bytes;
    private bool _disposed;

    public TranslationMemory(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        Load();
    }

    /// <summary>默认位置：%LocalAppData%\WISDRI\OfflineTranslator\translation-memory.jsonl。</summary>
    public static string DefaultPath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("WISDRI_TM_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath.Trim();
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WISDRI", "OfflineTranslator", "translation-memory.jsonl");
        }
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>把 scope、语言、提示词、token 上限和原文压成一个稳定哈希。</summary>
    public static string ComputeKey(
        string scope,
        string sourceLang,
        string targetLang,
        string? systemPrompt,
        int maxTokens,
        string text)
    {
        var payload = string.Join('\u0001',
            scope,
            sourceLang,
            targetLang,
            maxTokens.ToString(CultureInfo.InvariantCulture),
            systemPrompt ?? string.Empty,
            text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public bool TryGet(string key, out string value)
    {
        lock (_lock) return _entries.TryGetValue(key, out value!);
    }

    public void Put(string key, string value)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) return;
        lock (_lock)
        {
            if (_disposed || _entries.ContainsKey(key)) return;
            _entries[key] = value;
            _order.Enqueue(key);
            _bytes += value.Length + 80;
            _pending++;

            if (_entries.Count > MaxEntries || _bytes > MaxBytes)
            {
                CompactLocked();
                return;
            }

            EnsureWriterLocked();
            try { _writer?.WriteLine(JsonSerializer.Serialize(new TmRecord(key, value))); }
            catch { }

            if (_pending >= FlushEvery) FlushLocked();
        }
    }

    private void Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!File.Exists(_path)) return;

            foreach (var line in File.ReadLines(_path))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var key = doc.RootElement.TryGetProperty("k", out var k) ? k.GetString() : null;
                    var value = doc.RootElement.TryGetProperty("v", out var v) ? v.GetString() : null;
                    if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) continue;
                    if (!_entries.TryAdd(key, value)) continue;
                    _order.Enqueue(key);
                    _bytes += value.Length + 80;
                }
                catch
                {
                    // 单行损坏不影响其余记忆
                }
            }

            while ((_entries.Count > MaxEntries || _bytes > MaxBytes) && _order.Count > 0)
                RemoveOldestLocked();
        }
        catch
        {
            // 记忆文件不可读时按空记忆运行，不影响翻译
        }
    }

    private void EnsureWriterLocked()
    {
        if (_writer is not null || _disposed) return;
        try
        {
            _writer = new StreamWriter(_path, append: true, new UTF8Encoding(false));
        }
        catch
        {
            _writer = null;
        }
    }

    private void FlushLocked()
    {
        try { _writer?.Flush(); } catch { }
        _pending = 0;
    }

    private void RemoveOldestLocked()
    {
        var oldest = _order.Dequeue();
        if (_entries.Remove(oldest, out var removed))
            _bytes -= removed.Length + 80;
    }

    private void CompactLocked()
    {
        while ((_entries.Count > MaxEntries || _bytes > MaxBytes) && _order.Count > 0)
            RemoveOldestLocked();

        try
        {
            _writer?.Dispose();
            _writer = null;
            var temp = _path + ".tmp";
            using (var writer = new StreamWriter(temp, append: false, new UTF8Encoding(false)))
            {
                foreach (var key in _order)
                {
                    if (!_entries.TryGetValue(key, out var value)) continue;
                    writer.WriteLine(JsonSerializer.Serialize(new TmRecord(key, value)));
                }
            }
            File.Move(temp, _path, overwrite: true);
            _pending = 0;
        }
        catch
        {
            // 压缩失败时保持内存记忆可用，下次启动重新载入
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            FlushLocked();
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _disposed = true;
        }
    }

    private sealed record TmRecord(string k, string v);
}
