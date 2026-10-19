using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OfflineTranslator.Core;

/// <summary>
/// 术语项。
/// </summary>
public sealed record TermEntry(
    string Source,
    string Target,
    string? SourceLang = null,
    string? TargetLang = null,
    bool CaseSensitive = false);

/// <summary>
/// 只读术语库：程序不负责管理（增删/落盘/迁移），仅从三端共享规范文件读取词条参与翻译。
///
/// 读取顺序：规范文件（%ProgramData%\WISDRI\Glossary\glossary.csv，可用环境变量
/// WISDRI_GLOSSARY_DIR 覆盖）→ 每用户覆盖（%AppData%\WISDRI\Glossary\glossary.csv，后者优先）；
/// 两者都不存在时在内存中使用内置出厂默认（assets\glossary-defaults.csv），不落盘。
/// 格式：CSV / UTF-8，表头 Source,Target,SourceLang,TargetLang,CaseSensitive，
///   '#' 开头为注释，字段支持双引号转义（"" 表示一个 "），CaseSensitive 取 0/1。
/// 策略：L1 = 送翻前精准查库直出（<see cref="TryTranslate"/>）。
/// </summary>
public sealed class GlossaryService
{
    /// <summary>出厂内置默认术语库的内嵌资源名（见 OfflineTranslator.Core.csproj）。</summary>
    public const string EmbeddedDefaultsName = "WISDRI.Glossary.glossary-defaults.csv";

    /// <summary>三端共享规范文件名。</summary>
    public const string CsvFileName = "glossary.csv";

    private static string? _canonicalOverride;   // 自检/测试用：把规范路径指到临时目录
    private static string? _userOverride;        // 自检/测试用：把每用户覆盖路径指到临时目录

    private readonly List<TermEntry> _entries = new();
    private readonly object _lock = new();

    /// <summary>每次 Load 递增，供翻译缓存判断术语库是否已变化。</summary>
    public int Version { get; private set; }

    /// <summary>术语库内容的稳定哈希，供持久化翻译记忆跨会话隔离。</summary>
    public string ContentHash { get; private set; } = "0";

    // ============================ 路径解析（三端统一规范） ============================

    /// <summary>共享目录：%ProgramData%\WISDRI\Glossary（可用环境变量 WISDRI_GLOSSARY_DIR 覆盖）。</summary>
    public static string SharedDirectory
    {
        get
        {
            string? env = null;
            try { env = Environment.GetEnvironmentVariable("WISDRI_GLOSSARY_DIR"); }
            catch { /* 环境变量不可读时退回默认目录 */ }
            if (!string.IsNullOrWhiteSpace(env)) return env!.Trim();
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WISDRI", GlossaryDirName);
        }
    }

    private const string GlossaryDirName = "Glossary";

    /// <summary>规范文件（三端唯一事实来源）。</summary>
    public static string CanonicalPath => _canonicalOverride ?? Path.Combine(SharedDirectory, CsvFileName);

    /// <summary>每用户覆盖文件：%AppData%\WISDRI\Glossary\glossary.csv。</summary>
    public static string UserPath => _userOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WISDRI", GlossaryDirName, CsvFileName);

    /// <summary>
    /// 自检/测试用：把规范路径与每用户覆盖路径临时改到指定文件（传 null 恢复默认）。
    /// 用于在临时目录验证读取行为，不碰真实配置。
    /// </summary>
    public static void UsePathsForTest(string? canonicalPath, string? userPath)
    {
        _canonicalOverride = canonicalPath;
        _userOverride = userPath;
    }

    public IReadOnlyList<TermEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    // ============================ 读取 ============================

    /// <summary>
    /// 按三端规范加载术语库：规范文件 → 每用户覆盖（后者优先）→ 都没有则内置默认（仅内存，不落盘）。
    /// 返回加载成功数量与校验错误列表。
    /// </summary>
    public (int loadedCount, List<string> errors) Load()
    {
        var errors = new List<string>();
        var collected = new List<TermEntry>();

        var canonical = CanonicalPath;
        var user = UserPath;
        var hasCanonical = File.Exists(canonical);
        var hasUser = File.Exists(user);

        if (hasCanonical)
        {
            var (entries, errs) = ReadEntries(canonical);
            collected.AddRange(entries);
            errors.AddRange(errs);
        }

        if (hasUser)
        {
            var (entries, errs) = ReadEntries(user);
            collected.AddRange(entries);   // 每用户覆盖后加载 → 冲突时优先
            errors.AddRange(errs);
        }

        if (!hasCanonical && !hasUser)
        {
            // 两个文件都不存在：内存中使用内置出厂默认
            collected.AddRange(EmbeddedDefaults(out var embeddedErrors));
            errors.AddRange(embeddedErrors);
        }

        var merged = MergeLastWins(collected);
        lock (_lock)
        {
            _entries.Clear();
            _entries.AddRange(merged);
            Version++;
            ContentHash = ComputeContentHash(merged);
        }

        return (merged.Count, errors);
    }

    // ============================ 内置默认与解析 ============================

    /// <summary>读取内嵌出厂默认术语库原文（assets\glossary-defaults.csv）。</summary>
    public static string? ReadEmbeddedDefaultsRaw()
    {
        try
        {
            using var stream = typeof(GlossaryService).Assembly.GetManifestResourceStream(EmbeddedDefaultsName);
            if (stream is null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>解析内嵌出厂默认术语库词条。</summary>
    public static List<TermEntry> EmbeddedDefaults(out List<string> errors)
    {
        var raw = ReadEmbeddedDefaultsRaw();
        if (string.IsNullOrEmpty(raw))
        {
            errors = new List<string> { $"未找到内嵌默认术语库资源: {EmbeddedDefaultsName}" };
            return new List<TermEntry>();
        }

        var (entries, errs) = ParseLines(raw.Split('\n'));
        errors = errs;
        return entries;
    }

    private static (List<TermEntry> entries, List<string> errors) ReadEntries(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            return ParseLines(lines);
        }
        catch (Exception ex)
        {
            return (new List<TermEntry>(), new List<string> { $"读取术语库失败 {path}: {ex.Message}" });
        }
    }

    private static (List<TermEntry> entries, List<string> errors) ParseLines(IEnumerable<string> lines)
    {
        var errors = new List<string>();
        var newEntries = new List<TermEntry>();
        var lineNum = 0;
        var dataLineSeen = false;

        foreach (var rawLine in lines)
        {
            lineNum++;
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            // 跳过表头行（首个数据行且首列是 source/原文/源术语）
            if (!dataLineSeen)
            {
                dataLineSeen = true;
                if (IsHeaderLine(line))
                    continue;
            }

            var parts = ParseCsvLine(line);
            if (parts.Count < 2)
            {
                errors.Add($"第 {lineNum} 行列数不足: 至少需要'源术语,目标术语'");
                continue;
            }

            var src = parts[0].Trim();
            var tgt = parts[1].Trim();

            if (string.IsNullOrWhiteSpace(src))
            {
                errors.Add($"第 {lineNum} 行源术语为空");
                continue;
            }
            if (string.IsNullOrWhiteSpace(tgt))
            {
                errors.Add($"第 {lineNum} 行目标术语为空");
                continue;
            }
            if (src.Length > 200 || tgt.Length > 200)
            {
                errors.Add($"第 {lineNum} 行术语过长（>200字符），疑似整段非术语");
                continue;
            }

            var srcLang = parts.Count > 2 ? NormalizeLang(parts[2]) : null;
            var tgtLang = parts.Count > 3 ? NormalizeLang(parts[3]) : null;
            var caseSensitive = parts.Count > 4 && (parts[4].Trim() == "1" || parts[4].Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

            newEntries.Add(new TermEntry(src, tgt, srcLang, tgtLang, caseSensitive));
        }

        return (newEntries, errors);
    }

    private static bool IsHeaderLine(string line)
    {
        var first = ParseCsvLine(line)[0].Trim();
        return first.Equals("source", StringComparison.OrdinalIgnoreCase)
            || first.Equals("原文", StringComparison.Ordinal)
            || first.Equals("源术语", StringComparison.Ordinal);
    }

    // ============================ L1 送翻前精准查库 ============================

    /// <summary>
    /// 送翻前精准查库。按 Source 精确匹配（忽略首尾空白，CaseSensitive 语义同规范），
    /// 命中则直接给出规范译名，调用方无需再送引擎（零延迟、无幻觉）。
    /// 语言均以「空或 auto 视为不限」处理；同名词条以后加载者优先（每用户覆盖 > 规范文件 > 内置默认）。
    /// </summary>
    public bool TryTranslate(string source, string? srcLang, string? tgtLang, out string target)
    {
        target = string.Empty;
        if (string.IsNullOrWhiteSpace(source)) return false;

        var key = source.Trim();
        TermEntry? hit = null;
        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                var comparison = entry.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (!string.Equals(entry.Source.Trim(), key, comparison)) continue;
                if (!LangMatches(entry.SourceLang, srcLang) || !LangMatches(entry.TargetLang, tgtLang)) continue;
                hit = entry;
            }
        }

        if (hit is null || string.IsNullOrWhiteSpace(hit.Target)) return false;
        target = hit.Target;
        return true;
    }

    // ============================ 内部工具 ============================

    private static bool LangMatches(string? entryLang, string? queryLang)
    {
        if (string.IsNullOrWhiteSpace(entryLang) || string.IsNullOrWhiteSpace(queryLang)) return true;
        if (queryLang.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;
        return entryLang.Equals(queryLang.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>对排序后的词条做 FNV-1a，内容不变则哈希不变（跨进程稳定）。</summary>
    private static string ComputeContentHash(List<TermEntry> entries)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var entry in entries
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Target, StringComparer.Ordinal)
            .ThenBy(e => e.SourceLang ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(e => e.TargetLang ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(e => e.CaseSensitive))
        {
            Append(entry.Source);
            Append(entry.Target);
            Append(entry.SourceLang ?? string.Empty);
            Append(entry.TargetLang ?? string.Empty);
            hash ^= entry.CaseSensitive ? 1UL : 0UL;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16");

        void Append(string text)
        {
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            hash ^= 0x1F;
            hash *= 1099511628211UL;
        }
    }

    /// <summary>同 Source 去重，后加载者覆盖先加载者（保证 AppData 覆盖优先、规范文件覆盖内置默认）。</summary>
    private static List<TermEntry> MergeLastWins(IEnumerable<TermEntry> entries)
    {
        var result = new List<TermEntry>();
        foreach (var entry in entries)
        {
            var index = result.FindIndex(existing => SameKey(existing, entry));
            if (index >= 0) result[index] = entry;
            else result.Add(entry);
        }
        return result;
    }

    private static bool SameKey(TermEntry a, TermEntry b) =>
        a.CaseSensitive || b.CaseSensitive
            ? string.Equals(a.Source, b.Source, StringComparison.Ordinal)
            : string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeLang(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        return lang.Trim().ToLowerInvariant();
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
    }
}
