using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Data.SQLite;
using Type4Me.Win.Core;

namespace Type4Me.Win.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Type4Me");

    public static string CredentialsPath => Path.Combine(Root, "credentials.json");
    public static string HistoryPath => Path.Combine(Root, "history.db");
    public static string LogsPath => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsPath);
    }
}

public sealed class CredentialStore
{
    private readonly string _root;

    public CredentialStore(string? root = null)
    {
        _root = root ?? Path.Combine(AppPaths.Root, "secure");
        Directory.CreateDirectory(_root);
    }

    public void Save(string account, IReadOnlyDictionary<string, string> values)
    {
        var json = JsonSerializer.Serialize(values);
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(json),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(account), protectedBytes);
    }

    public Dictionary<string, string> Load(string account)
    {
        var path = PathFor(account);
        if (!File.Exists(path))
        {
            return [];
        }

        var bytes = ProtectedData.Unprotect(
            File.ReadAllBytes(path),
            optionalEntropy: null,
            scope: DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(bytes) ?? [];
    }

    public void Delete(string account)
    {
        var path = PathFor(account);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string account)
    {
        var safe = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)));
        return Path.Combine(_root, safe + ".bin");
    }
}

public sealed class SettingsStore
{
    private readonly CredentialStore _secureStore;
    private readonly string _path;
    private readonly object _lock = new();
    private SettingsDocument _document;

    public SettingsStore(string? path = null, CredentialStore? secureStore = null)
    {
        AppPaths.EnsureCreated();
        _path = path ?? AppPaths.CredentialsPath;
        _secureStore = secureStore ?? new CredentialStore();
        _document = LoadDocument();
    }

    public ASRProvider SelectedASRProvider
    {
        get => Enum.TryParse<ASRProvider>(_document.SelectedASRProvider, out var provider) ? provider : ASRProvider.Volcano;
        set
        {
            _document.SelectedASRProvider = value.ToString();
            SaveDocument();
        }
    }

    public LLMProvider SelectedLLMProvider
    {
        get => Enum.TryParse<LLMProvider>(_document.SelectedLLMProvider, out var provider) ? provider : LLMProvider.Doubao;
        set
        {
            _document.SelectedLLMProvider = value.ToString();
            SaveDocument();
        }
    }

    public PunctuationMode PunctuationMode
    {
        get => Enum.TryParse<PunctuationMode>(_document.PunctuationMode, out var mode) ? mode : PunctuationMode.Full;
        set
        {
            _document.PunctuationMode = value.ToString();
            SaveDocument();
        }
    }

    public List<ProcessingMode> LoadModes() =>
        _document.Modes.Count == 0 ? ProcessingMode.Defaults.ToList() : _document.Modes;

    public void SaveModes(IEnumerable<ProcessingMode> modes)
    {
        _document.Modes = modes.ToList();
        SaveDocument();
    }

    public void SaveASRCredentials(ASRProvider provider, Dictionary<string, string> values)
    {
        SaveCredentials("asr_" + provider, ASRProviderRegistry.Get(provider).Fields, values);
    }

    public Dictionary<string, string> LoadASRCredentials(ASRProvider provider)
    {
        var descriptor = ASRProviderRegistry.Get(provider);
        return LoadCredentials("asr_" + provider, descriptor.Fields);
    }

    public IASRProviderConfig? LoadSelectedASRConfig()
    {
        var provider = SelectedASRProvider;
        var descriptor = ASRProviderRegistry.Get(provider);
        return descriptor.CreateConfig(LoadASRCredentials(provider));
    }

    public void SaveLLMCredentials(LLMProvider provider, Dictionary<string, string> values)
    {
        SaveCredentials("llm_" + provider, LLMProviderRegistry.Get(provider).Fields, values);
    }

    public Dictionary<string, string> LoadLLMCredentials(LLMProvider provider)
    {
        var descriptor = LLMProviderRegistry.Get(provider);
        return LoadCredentials("llm_" + provider, descriptor.Fields);
    }

    public LLMConfig? LoadSelectedLLMConfig()
    {
        var provider = SelectedLLMProvider;
        var descriptor = LLMProviderRegistry.Get(provider);
        return descriptor.CreateConfig(LoadLLMCredentials(provider));
    }

    private void SaveCredentials(string key, IReadOnlyList<CredentialField> fields, Dictionary<string, string> values)
    {
        lock (_lock)
        {
            var secureKeys = fields.Where(f => f.IsSecure).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            var secure = values.Where(kv => secureKeys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var plain = values.Where(kv => !secureKeys.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            if (secure.Count > 0) _secureStore.Save(key, secure);
            else _secureStore.Delete(key);

            if (plain.Count > 0) _document.PlainCredentials[key] = plain;
            else _document.PlainCredentials.Remove(key);

            SaveDocument();
        }
    }

    private Dictionary<string, string> LoadCredentials(string key, IReadOnlyList<CredentialField> fields)
    {
        lock (_lock)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                if (!string.IsNullOrWhiteSpace(field.DefaultValue))
                {
                    result[field.Key] = field.DefaultValue;
                }
            }

            if (_document.PlainCredentials.TryGetValue(key, out var plain))
            {
                foreach (var pair in plain) result[pair.Key] = pair.Value;
            }

            foreach (var pair in _secureStore.Load(key))
            {
                result[pair.Key] = pair.Value;
            }

            return result;
        }
    }

    private SettingsDocument LoadDocument()
    {
        if (!File.Exists(_path))
        {
            return new SettingsDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(_path)) ?? new SettingsDocument();
        }
        catch
        {
            return new SettingsDocument();
        }
    }

    private void SaveDocument()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.Serialize(_document, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }

    private sealed class SettingsDocument
    {
        public string SelectedASRProvider { get; set; } = ASRProvider.Volcano.ToString();
        public string SelectedLLMProvider { get; set; } = LLMProvider.Doubao.ToString();
        public string PunctuationMode { get; set; } = Type4Me.Win.Core.PunctuationMode.Full.ToString();
        public Dictionary<string, Dictionary<string, string>> PlainCredentials { get; set; } = [];
        public List<ProcessingMode> Modes { get; set; } = [];
    }
}

public sealed record HistoryRecord(
    string Id,
    DateTimeOffset CreatedAt,
    double DurationSeconds,
    string RawText,
    string? ProcessingMode,
    string? ProcessedText,
    string FinalText,
    string Status,
    int CharacterCount,
    string? ASRProvider,
    string? ASRModel);

public sealed class HistoryStore
{
    private readonly string _connectionString;

    public HistoryStore(string? dbPath = null)
    {
        AppPaths.EnsureCreated();
        _connectionString = new SQLiteConnectionStringBuilder
        {
            DataSource = dbPath ?? AppPaths.HistoryPath
        }.ToString();
        Initialize();
    }

    public void Insert(HistoryRecord record)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO recognition_history
            (id, created_at, duration_seconds, raw_text, processing_mode, processed_text, final_text, status, character_count, asr_provider, asr_model)
            VALUES ($id, $created_at, $duration_seconds, $raw_text, $processing_mode, $processed_text, $final_text, $status, $character_count, $asr_provider, $asr_model);
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$created_at", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$duration_seconds", record.DurationSeconds);
        command.Parameters.AddWithValue("$raw_text", record.RawText);
        command.Parameters.AddWithValue("$processing_mode", (object?)record.ProcessingMode ?? DBNull.Value);
        command.Parameters.AddWithValue("$processed_text", (object?)record.ProcessedText ?? DBNull.Value);
        command.Parameters.AddWithValue("$final_text", record.FinalText);
        command.Parameters.AddWithValue("$status", record.Status);
        command.Parameters.AddWithValue("$character_count", record.CharacterCount);
        command.Parameters.AddWithValue("$asr_provider", (object?)record.ASRProvider ?? DBNull.Value);
        command.Parameters.AddWithValue("$asr_model", (object?)record.ASRModel ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<HistoryRecord> FetchPage(int limit = 100, int offset = 0)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM recognition_history ORDER BY created_at DESC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var records = new List<HistoryRecord>();
        while (reader.Read())
        {
            records.Add(ReadRecord(reader));
        }
        return records;
    }

    public string ExportCsv()
    {
        var builder = new StringBuilder();
        builder.AppendLine("记录ID,创建时间,持续秒数,原始识别,处理模式,处理后文本,最终文本,状态,字符数,语音服务,语音模型");
        foreach (var record in FetchPage(10_000))
        {
            builder.AppendLine(string.Join(',', [
                Csv(record.Id),
                Csv(record.CreatedAt.ToString("O")),
                Csv(record.DurationSeconds.ToString("0.###")),
                Csv(record.RawText),
                Csv(LocalizeModeName(record.ProcessingMode)),
                Csv(record.ProcessedText ?? ""),
                Csv(record.FinalText),
                Csv(LocalizeHistoryStatus(record.Status)),
                Csv(record.CharacterCount.ToString()),
                Csv(LocalizeProvider(record.ASRProvider)),
                Csv(record.ASRModel ?? "")
            ]));
        }
        return builder.ToString();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS recognition_history (
                id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                duration_seconds REAL,
                raw_text TEXT NOT NULL,
                processing_mode TEXT,
                processed_text TEXT,
                final_text TEXT NOT NULL,
                status TEXT NOT NULL,
                character_count INTEGER,
                asr_provider TEXT,
                asr_model TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_history_created_at ON recognition_history(created_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    private SQLiteConnection Open()
    {
        var connection = new SQLiteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static HistoryRecord ReadRecord(SQLiteDataReader reader) => new(
        reader.GetString(0),
        DateTimeOffset.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10));

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string LocalizeModeName(string? modeName) => modeName switch
    {
        null or "" => "",
        "Prompt 优化" => "提示词优化",
        _ => modeName
    };

    private static string LocalizeHistoryStatus(string status) => status switch
    {
        "completed" => "已完成",
        "timeout" => "超时",
        "failed" => "失败",
        "stopped" => "已停止",
        "canceled" or "cancelled" => "已取消",
        _ => status
    };

    private static string LocalizeProvider(string? provider) => provider switch
    {
        null or "" => "",
        "Volcano" => "火山引擎",
        "OpenAICompatible" => "OpenAI 兼容",
        _ => provider
    };
}
