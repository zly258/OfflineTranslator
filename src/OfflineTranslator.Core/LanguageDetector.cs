using System;
using System.Text.RegularExpressions;

namespace OfflineTranslator.Core;

/// <summary>
/// 轻量级快速语种启发式检测器（< 0.1ms），用于当源语言设置为 'auto' 时的自动探测。
/// </summary>
public static class LanguageDetector
{
    private static readonly Regex HanziRegex = new(@"\p{IsCJKUnifiedIdeographs}", RegexOptions.Compiled);
    private static readonly Regex HiraganaKatakanaRegex = new(@"[\p{IsHiragana}\p{IsKatakana}]", RegexOptions.Compiled);
    private static readonly Regex HangulRegex = new(@"[\p{IsHangulSyllables}\p{IsHangulJamo}]", RegexOptions.Compiled);
    private static readonly Regex CyrillicRegex = new(@"\p{IsCyrillic}", RegexOptions.Compiled);
    private static readonly Regex ArabicRegex = new(@"\p{IsArabic}", RegexOptions.Compiled);

    /// <summary>
    /// 检测待翻译文本的语言代码（例如 "zh", "ja", "ko", "ru", "ar", "en" 等）。
    /// </summary>
    public static string DetectLanguage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "en";

        // 日语优先于汉字判断（日文中混杂大量汉字）
        if (HiraganaKatakanaRegex.IsMatch(text))
            return "ja";

        // 韩文字符
        if (HangulRegex.IsMatch(text))
            return "ko";

        // 中文汉字
        if (HanziRegex.IsMatch(text))
            return "zh";

        // 俄语 / 西里尔字母
        if (CyrillicRegex.IsMatch(text))
            return "ru";

        // 阿拉伯语
        if (ArabicRegex.IsMatch(text))
            return "ar";

        // 拉丁字母系粗分（法、德、西等常见特征字符，默认退化为 en）
        if (Regex.IsMatch(text, @"[äöüßÄÖÜ]")) return "de";
        if (Regex.IsMatch(text, @"[éàèùâêîôûçëïüœÉÀÈÙÂÊÎÔÛÇËÏÜŒ]")) return "fr";
        if (Regex.IsMatch(text, @"[áéíóúüñ¿¡ÁÉÍÓÚÜÑ]")) return "es";

        return "en";
    }
}
