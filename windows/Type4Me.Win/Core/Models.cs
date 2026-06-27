using System.Text;
using System.IO;

namespace Type4Me.Win.Core;

public enum ASRProvider
{
    Volcano,
    Deepgram,
    Soniox,
    OpenAI
}

public enum LLMProvider
{
    Doubao,
    Claude,
    OpenAICompatible
}

public enum HotkeyStyle
{
    Toggle,
    Hold
}

public enum PunctuationMode
{
    Full,
    None,
    TrimFinalPeriod
}

public enum SessionState
{
    Idle,
    Starting,
    Recording,
    Finishing,
    PostProcessing,
    Injecting
}

public enum InjectionOutcome
{
    Inserted,
    CopiedToClipboard
}

public sealed record FieldOption(string Value, string Label);

public sealed record CredentialField(
    string Key,
    string Label,
    string Placeholder,
    bool IsSecure,
    bool IsOptional,
    string DefaultValue,
    IReadOnlyList<FieldOption>? Options = null);

public interface IASRProviderConfig
{
    ASRProvider Provider { get; }
    bool IsValid { get; }
    Dictionary<string, string> ToCredentials();
}

public sealed record ASRRequestOptions(
    PunctuationMode PunctuationMode = PunctuationMode.Full,
    IReadOnlyList<string>? Hotwords = null,
    string? BoostingTableId = null,
    int ContextHistoryLength = 20)
{
    public bool EnablePunctuation => PunctuationMode != PunctuationMode.None;
}

public sealed record RecognitionTranscript(
    IReadOnlyList<string> ConfirmedSegments,
    string PartialText,
    string AuthoritativeText,
    bool IsFinal)
{
    public static RecognitionTranscript Empty { get; } = new([], "", "", false);

    public string DisplayText =>
        !string.IsNullOrWhiteSpace(AuthoritativeText)
            ? AuthoritativeText
            : string.Concat(ConfirmedSegments) + PartialText;
}

public abstract record RecognitionEvent
{
    public sealed record Ready : RecognitionEvent;
    public sealed record Transcript(RecognitionTranscript Value) : RecognitionEvent;
    public sealed record Completed : RecognitionEvent;
    public sealed record ProcessingResult(string Text) : RecognitionEvent;
    public sealed record Finalized(string Text, InjectionOutcome Injection) : RecognitionEvent;
    public sealed record Error(Exception Exception) : RecognitionEvent;
}

public sealed record LLMConfig(string ApiKey, string Model, string BaseUrl);

public sealed record ProcessingMode(
    Guid Id,
    string Name,
    string Prompt,
    bool IsBuiltin,
    string ProcessingLabel,
    int HotkeyVirtualKey,
    uint HotkeyModifiers,
    HotkeyStyle HotkeyStyle)
{
    public static Guid DirectId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static Guid SmartDirectId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000006");
    public static Guid TranslateId { get; } = Guid.Parse("00000000-0000-0000-0000-000000000003");
    public static Guid PromptOptimizeId { get; } = Guid.Parse("5D0A24D4-ECE9-4C13-9FC5-F9C81BD6B1C3");

    public static ProcessingMode Direct { get; } = new(
        DirectId,
        "快速模式",
        "",
        true,
        "识别中",
        NativeVirtualKeys.Space,
        NativeHotkeyModifiers.Control | NativeHotkeyModifiers.Alt,
        HotkeyStyle.Toggle);

    public static ProcessingMode SmartDirect { get; } = new(
        SmartDirectId,
        "智能润色",
        """
        你是一个语音转写纠错助手。请修正以下语音识别文本中的错别字和标点符号。
        只修正明显错误，不改变原意，直接返回修正后的文本。

        {text}
        """,
        false,
        "润色中",
        NativeVirtualKeys.Space,
        NativeHotkeyModifiers.Control | NativeHotkeyModifiers.Shift,
        HotkeyStyle.Toggle);

    public static ProcessingMode Translate { get; } = new(
        TranslateId,
        "英文翻译",
        """
        你是一个语音转写文本的英文翻译工具。请将下面的中文口语文本翻译为自然流畅的英文。
        直接返回译文，不要添加解释。

        {text}
        """,
        false,
        "翻译中",
        NativeVirtualKeys.Space,
        NativeHotkeyModifiers.Control | NativeHotkeyModifiers.Alt | NativeHotkeyModifiers.Shift,
        HotkeyStyle.Toggle);

    public static ProcessingMode PromptOptimize { get; } = new(
        PromptOptimizeId,
        "提示词优化",
        """
        你是提示词优化工具。请将下面的口语化原始提示词改写为结构清晰、指令精准的高质量提示词。
        直接返回优化后的提示词，不要添加解释。

        {text}
        """,
        false,
        "优化中",
        NativeVirtualKeys.F9,
        NativeHotkeyModifiers.Control | NativeHotkeyModifiers.Alt,
        HotkeyStyle.Toggle);

    public static IReadOnlyList<ProcessingMode> Defaults { get; } =
        [Direct, SmartDirect, Translate, PromptOptimize];

    public string RenderPrompt(string text, PromptContext context)
    {
        return Prompt
            .Replace("{text}", text, StringComparison.Ordinal)
            .Replace("{selected}", context.SelectedText, StringComparison.Ordinal)
            .Replace("{clipboard}", context.ClipboardText, StringComparison.Ordinal);
    }
}

public sealed record PromptContext(string SelectedText, string ClipboardText)
{
    public static PromptContext Empty { get; } = new("", "");
}

public static class NativeHotkeyModifiers
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Win = 0x0008;
}

public static class NativeVirtualKeys
{
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int F9 = 0x78;
    public const int V = 0x56;
    public const int Control = 0x11;
    public const int Shift = 0x10;
    public const int Menu = 0x12;
    public const int LWin = 0x5B;
    public const int RWin = 0x5C;
    public const int LShift = 0xA0;
    public const int RShift = 0xA1;
    public const int LControl = 0xA2;
    public const int RControl = 0xA3;
    public const int LMenu = 0xA4;
    public const int RMenu = 0xA5;
}

public static class TextPostProcessor
{
    private const string PunctuationChars = "，。！？；：、,.!?;:\"'“”‘’（）()【】[]《》<>-—…";

    public static string ApplyPunctuationMode(string text, PunctuationMode mode)
    {
        return mode switch
        {
            PunctuationMode.None => RemovePunctuation(text),
            PunctuationMode.TrimFinalPeriod => TrimFinalPeriod(text),
            _ => text
        };
    }

    private static string RemovePunctuation(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!PunctuationChars.Contains(ch))
            {
                builder.Append(ch);
            }
        }
        return builder.ToString();
    }

    private static string TrimFinalPeriod(string text)
    {
        var trimmedEnd = text.TrimEnd();
        if (trimmedEnd.EndsWith('。') || trimmedEnd.EndsWith('.'))
        {
            return trimmedEnd[..^1];
        }
        return text;
    }
}

public static class TextJoiner
{
    public static string AppendSegment(string existingText, string segment)
    {
        if (string.IsNullOrEmpty(existingText) || string.IsNullOrEmpty(segment))
        {
            return segment;
        }

        var last = existingText[^1];
        var first = segment[0];
        if (char.IsWhiteSpace(last) || char.IsWhiteSpace(first) ||
            IsCjk(last) || IsCjk(first) ||
            IsOpeningPunctuation(last) || IsClosingPunctuation(first))
        {
            return segment;
        }

        return " " + segment;
    }

    private static bool IsCjk(char c) => c >= '\u4e00' && c <= '\u9fff';
    private static bool IsOpeningPunctuation(char c) => "([{（【《“‘".Contains(c);
    private static bool IsClosingPunctuation(char c) => ".,!?;:)]}，。！？；：）】》”’".Contains(c);
}

public static class WavEncoder
{
    public static byte[] FromPcm16Mono16k(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return ms.ToArray();
    }
}
