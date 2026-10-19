using System;
using System.Collections.Generic;
using System.Text;

namespace OfflineTranslator.Core;

/// <summary>
/// 不同大模型家族的提示词模板策略接口。
/// </summary>
public interface IPromptFormatter
{
    string ModelFamily { get; }

    /// <summary>拼装最终发给模型的 prompt 字符串（包含 chat template 特殊标记）。</summary>
    string BuildPrompt(string text, string targetLanguage, IReadOnlyList<TermEntry> terms, string? systemPrompt);

    /// <summary>模型的截断停用词标记。</summary>
    IReadOnlyList<string> AntiPrompts { get; }
}

/// <summary>
/// 腾讯混元 Hy-MT2 专用模板（使用 Hy-MT 独有特殊标记）。
/// </summary>
public sealed class HunyuanPromptFormatter : IPromptFormatter
{
    public static HunyuanPromptFormatter Instance { get; } = new();

    public string ModelFamily => "Hunyuan";

    public IReadOnlyList<string> AntiPrompts { get; } =
    [
        "<｜hy_place▁holder▁no▁2｜>",
        "<｜hy_end▁of▁sentence｜>",
        "<｜hy_Assistant｜>",
        "<｜hy_User｜>"
    ];

    public string BuildPrompt(string text, string targetLanguage, IReadOnlyList<TermEntry> terms, string? systemPrompt)
    {
        var rawPrompt = HyMtPrompt.Build(text, targetLanguage, terms, systemPrompt);
        return $"<｜hy_begin▁of▁sentence｜><｜hy_User｜>{rawPrompt.ReplaceLineEndings("\n")}<｜hy_Assistant｜>";
    }
}

/// <summary>
/// 阿里通义千问 Qwen 系列专用模板（采用标准 ChatML 格式，支持 qwen2.5-0.5b/1.5b 等）。
/// </summary>
public sealed class QwenPromptFormatter : IPromptFormatter
{
    public static QwenPromptFormatter Instance { get; } = new();

    public string ModelFamily => "Qwen";

    public IReadOnlyList<string> AntiPrompts { get; } =
    [
        "<|im_end|>",
        "<|endoftext|>",
        "<|im_start|>",
        "\nuser\n",
        "\nassistant\n"
    ];

    public string BuildPrompt(string text, string targetLanguage, IReadOnlyList<TermEntry> terms, string? systemPrompt)
    {
        var tgtName = HyMtPrompt.TargetLanguageName(targetLanguage);
        var sb = new StringBuilder();

        // 1. System Prompt（默认强力关闭思考与多余回答）
        sb.Append("<|im_start|>system\n");
        sb.Append($"你是专业翻译引擎。请将用户输入的文本准确、忠实地翻译为{tgtName}。请务必直接输出翻译结果，严禁输出任何思考过程（如禁止输出 <think> 标签或思维链），严禁添加解释、注解或多余回答。");

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            sb.Append("\n\n");
            sb.Append(systemPrompt.Trim());
        }

        if (terms.Count > 0)
        {
            sb.Append("\n\n请严格参考并采用以下专业术语翻译：\n");
            foreach (var term in terms)
            {
                sb.Append("- ");
                sb.Append(term.Source.Trim());
                sb.Append(" => ");
                sb.Append(term.Target.Trim());
                sb.Append('\n');
            }
        }
        sb.Append("<|im_end|>\n");

        // 2. User Input
        sb.Append("<|im_start|>user\n");
        sb.Append(text.ReplaceLineEndings("\n").Trim());
        sb.Append("<|im_end|>\n");

        // 3. Assistant Generation Trigger（直接闭合思考块，默认关闭任何思维链展开，直接开始译文生成）
        sb.Append("<|im_start|>assistant\n<think>\n</think>\n");

        return sb.ToString();
    }
}

/// <summary>
/// 通用指令大模型默认模板（通用 ChatML 格式）。
/// </summary>
public sealed class DefaultPromptFormatter : IPromptFormatter
{
    public static DefaultPromptFormatter Instance { get; } = new();

    public string ModelFamily => "Default";

    public IReadOnlyList<string> AntiPrompts { get; } =
    [
        "<|im_end|>",
        "</s>",
        "<|endoftext|>",
        "\n\nHuman:",
        "\n\nUser:"
    ];

    public string BuildPrompt(string text, string targetLanguage, IReadOnlyList<TermEntry> terms, string? systemPrompt)
    {
        return QwenPromptFormatter.Instance.BuildPrompt(text, targetLanguage, terms, systemPrompt);
    }
}

/// <summary>
/// 根据模型文件名或路径自动路由最佳提示词策略。
/// </summary>
public static class PromptFormatterFactory
{
    public static IPromptFormatter ForModel(string modelPathOrName)
    {
        if (string.IsNullOrWhiteSpace(modelPathOrName)) return DefaultPromptFormatter.Instance;
        var name = modelPathOrName.ToLowerInvariant();

        if (name.Contains("hy-mt") || name.Contains("hunyuan"))
            return HunyuanPromptFormatter.Instance;

        if (name.Contains("qwen"))
            return QwenPromptFormatter.Instance;

        return DefaultPromptFormatter.Instance;
    }
}
