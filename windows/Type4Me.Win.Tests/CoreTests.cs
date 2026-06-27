using System.IO;
using Type4Me.Win.Core;
using Type4Me.Win.LLM;
using Type4Me.Win.Services;
using System.Data.SQLite;

namespace Type4Me.Win.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "type4me-win-tests-" + Guid.NewGuid());

    public CoreTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void ProviderRegistry_BuildsDefaultConfigs()
    {
        var deepgram = ASRProviderRegistry.Get(ASRProvider.Deepgram).CreateConfig(new Dictionary<string, string>
        {
            ["apiKey"] = "dg-key"
        });

        Assert.NotNull(deepgram);
        var credentials = deepgram!.ToCredentials();
        Assert.Equal("nova-3", credentials["model"]);
        Assert.Equal("zh", credentials["language"]);
    }

    [Fact]
    public void ProcessingMode_RendersPromptVariables()
    {
        var mode = new ProcessingMode(
            Guid.NewGuid(),
            "custom",
            "{selected}|{text}|{clipboard}",
            false,
            "processing",
            NativeVirtualKeys.F9,
            NativeHotkeyModifiers.Control,
            HotkeyStyle.Toggle);

        var rendered = mode.RenderPrompt("recognized", new PromptContext("picked", "clip"));

        Assert.Equal("picked|recognized|clip", rendered);
    }

    [Fact]
    public void SettingsStore_SplitsSecureAndPlainCredentials()
    {
        var settingsPath = Path.Combine(_tempRoot, "credentials.json");
        var secureStore = new CredentialStore(Path.Combine(_tempRoot, "secure"));
        var store = new SettingsStore(settingsPath, secureStore);

        store.SaveASRCredentials(ASRProvider.OpenAI, new Dictionary<string, string>
        {
            ["apiKey"] = "secret",
            ["model"] = "whisper-1",
            ["baseURL"] = "https://example.test/v1"
        });

        var reloaded = new SettingsStore(settingsPath, secureStore).LoadASRCredentials(ASRProvider.OpenAI);

        Assert.Equal("secret", reloaded["apiKey"]);
        Assert.Equal("whisper-1", reloaded["model"]);
        Assert.DoesNotContain("secret", File.ReadAllText(settingsPath));
    }

    [Fact]
    public void SettingsStore_SavesPunctuationModeAndModes()
    {
        var settingsPath = Path.Combine(_tempRoot, "credentials.json");
        var store = new SettingsStore(settingsPath, new CredentialStore(Path.Combine(_tempRoot, "secure")));
        var modes = store.LoadModes();

        store.PunctuationMode = PunctuationMode.TrimFinalPeriod;
        store.SaveModes(modes.Select(mode => mode.Id == ProcessingMode.DirectId
            ? mode with { HotkeyVirtualKey = NativeVirtualKeys.RMenu, HotkeyModifiers = 0 }
            : mode));

        var reloaded = new SettingsStore(settingsPath, new CredentialStore(Path.Combine(_tempRoot, "secure")));

        Assert.Equal(PunctuationMode.TrimFinalPeriod, reloaded.PunctuationMode);
        var direct = reloaded.LoadModes().Single(mode => mode.Id == ProcessingMode.DirectId);
        Assert.Equal(NativeVirtualKeys.RMenu, direct.HotkeyVirtualKey);
        Assert.Equal(0u, direct.HotkeyModifiers);
    }

    [Fact]
    public void TextPostProcessor_AppliesPunctuationModes()
    {
        Assert.Equal("你好世界", TextPostProcessor.ApplyPunctuationMode("你好，世界。", PunctuationMode.None));
        Assert.Equal("你好，世界", TextPostProcessor.ApplyPunctuationMode("你好，世界。", PunctuationMode.TrimFinalPeriod));
        Assert.Equal("你好，世界！", TextPostProcessor.ApplyPunctuationMode("你好，世界！", PunctuationMode.TrimFinalPeriod));
        Assert.Equal("hello.", TextPostProcessor.ApplyPunctuationMode("hello.", PunctuationMode.Full));
    }

    [Fact]
    public void WavEncoder_WritesValidPcm16MonoHeader()
    {
        var pcm = new byte[3200];
        var wav = WavEncoder.FromPcm16Mono16k(pcm);

        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
    }

    [Fact]
    public void HistoryStore_InsertsFetchesAndExportsCsv()
    {
        var dbPath = Path.Combine(_tempRoot, "history.db");
        var store = new HistoryStore(dbPath);
        store.Insert(new HistoryRecord(
            "1",
            DateTimeOffset.Parse("2026-06-27T10:00:00+08:00"),
            1.25,
            "raw",
            "快速模式",
            null,
            "final",
            "completed",
            5,
            "OpenAI",
            "gpt-4o-transcribe"));

        var records = store.FetchPage();
        var csv = store.ExportCsv();

        Assert.Single(records);
        Assert.Equal("final", records[0].FinalText);
        Assert.Contains("\"final\"", csv);
    }

    [Fact]
    public void TextJoiner_DoesNotInsertSpacesAroundChinese()
    {
        Assert.Equal("世界", TextJoiner.AppendSegment("你好", "世界"));
        Assert.Equal(" world", TextJoiner.AppendSegment("hello", "world"));
    }

    [Fact]
    public void LlmEndpoint_DoesNotAppendDuplicatePath()
    {
        Assert.Equal(
            "https://example.test/v1/chat/completions",
            LLMHttpHelpers.BuildEndpoint("https://example.test/v1", "chat/completions"));
        Assert.Equal(
            "https://example.test/v1/chat/completions",
            LLMHttpHelpers.BuildEndpoint("https://example.test/v1/chat/completions", "chat/completions"));
        Assert.Equal(
            "https://example.test/v1/messages",
            LLMHttpHelpers.BuildEndpoint("https://example.test/v1/messages/", "messages"));
    }

    public void Dispose()
    {
        SQLiteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
