using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OfflineTranslator.Core;

/// <summary>
/// Hy-MT2 官方术语干预提示。只注入原文里真正出现的词条，提示保持短，推理才快。
/// </summary>
public static class HyMtPrompt
{
    public const int MaxTerms = 24;

    private static readonly Dictionary<string, string> TargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "中文",
        ["zht"] = "繁体中文",
        ["en"] = "英语",
        ["ja"] = "日语",
        ["ko"] = "韩语",
        ["de"] = "德语",
        ["fr"] = "法语",
        ["es"] = "西班牙语",
        ["pt"] = "葡萄牙语",
        ["ru"] = "俄语",
        ["ar"] = "阿拉伯语",
        ["th"] = "泰语",
        ["vi"] = "越南语",
        ["id"] = "印尼语",
        ["it"] = "意大利语",
        ["nl"] = "荷兰语",
        ["pl"] = "波兰语",
        ["tr"] = "土耳其语",
        ["uk"] = "乌克兰语",
        ["ms"] = "马来语",
        ["hi"] = "印地语",
    };

    /// <summary>目标语言用中文全称，和 Hy-MT2 中文提示的要求一致。</summary>
    public static string TargetLanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "中文";
        var key = code.Trim().ToLowerInvariant().Replace('_', '-');
        if (key is "auto") return "中文";
        if (key is "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" or "zht") return "繁体中文";
        if (key.StartsWith("zh", StringComparison.Ordinal)) return "中文";
        if (TargetNames.TryGetValue(key, out var name)) return name;
        return code.Trim();
    }

    /// <summary>
    /// 从术语库里挑出原文实际包含的词条。长词优先，短词若已被更长词覆盖则跳过。
    /// </summary>
    public static List<TermEntry> MatchTerms(
        string text,
        IReadOnlyList<TermEntry> entries,
        string? srcLang,
        string? tgtLang)
    {
        var hits = new List<TermEntry>();
        if (string.IsNullOrEmpty(text) || entries.Count == 0) return hits;

        // 词条按「原文里要找的那一侧」从长到短。英译中时用目标语去对原文，再译回源语。
        var candidates = new List<TermEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Source) || string.IsNullOrWhiteSpace(entry.Target)) continue;
            var forward = LangOk(entry.SourceLang, srcLang) && LangOk(entry.TargetLang, tgtLang);
            var reverse = LangOk(entry.TargetLang, srcLang) && LangOk(entry.SourceLang, tgtLang);
            if (forward)
                candidates.Add(entry);
            if (reverse && !string.Equals(entry.Source, entry.Target, StringComparison.OrdinalIgnoreCase))
                candidates.Add(new TermEntry(entry.Target, entry.Source, entry.TargetLang, entry.SourceLang, entry.CaseSensitive));
        }

        candidates.Sort(static (a, b) => b.Source.Length.CompareTo(a.Source.Length));
        foreach (var entry in candidates)
        {
            if (hits.Count >= MaxTerms) break;
            if (CoveredByLongerHit(entry.Source, hits)) continue;
            if (!ContainsTerm(text, entry)) continue;
            hits.Add(entry);
        }

        return hits;
    }

    public static string Build(string text, string? targetLanguage, IReadOnlyList<TermEntry> terms, string? userPrompt = null)
    {
        var sb = new StringBuilder();

        // 1. 如果用户显式传入了系统提示词/术语上下文，优先放在最前方
        var customPrompt = userPrompt?.Trim();
        if (!string.IsNullOrEmpty(customPrompt))
        {
            sb.Append(customPrompt);
            sb.Append("\n\n");
        }

        // 2. 匹配到的共享术语库
        if (terms.Count > 0)
        {
            sb.Append("参考下面的翻译：\n");
            foreach (var term in terms)
            {
                sb.Append(term.Source.Trim());
                sb.Append(" 翻译成 ");
                sb.Append(term.Target.Trim());
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        sb.Append("将以下文本翻译为");
        sb.Append(TargetLanguageName(targetLanguage));
        sb.Append("，注意只需要输出翻译后的结果，不要额外解释：\n\n");
        sb.Append(text.ReplaceLineEndings("\n").Trim());
        return sb.ToString();
    }

    private static bool CoveredByLongerHit(string source, List<TermEntry> hits)
    {
        foreach (var hit in hits)
        {
            if (hit.Source.Contains(source, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ContainsTerm(string text, TermEntry entry)
    {
        var source = entry.Source.Trim();
        if (source.Length == 0) return false;

        if (IsAsciiWord(source))
        {
            var flags = entry.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(source)}(?![\p{{L}}\p{{N}}])";
            return Regex.IsMatch(text, pattern, flags);
        }

        var comparison = entry.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return text.Contains(source, comparison);
    }

    private static bool IsAsciiWord(string source)
    {
        foreach (var c in source)
        {
            var ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or ' ' or '.' or '_' or '-';
            if (!ok) return false;
        }
        return true;
    }

    private static bool LangOk(string? entryLang, string? queryLang)
    {
        if (string.IsNullOrWhiteSpace(entryLang) || string.IsNullOrWhiteSpace(queryLang)) return true;
        if (queryLang.Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;
        return entryLang.Equals(queryLang.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
