using Type4Me.Win.ASR;
using Type4Me.Win.LLM;

namespace Type4Me.Win.Core;

public sealed record VolcanoASRConfig(
    string AppKey,
    string AccessKey,
    string ResourceId,
    string Uid) : IASRProviderConfig
{
    public ASRProvider Provider => ASRProvider.Volcano;
    public bool IsValid => !string.IsNullOrWhiteSpace(AppKey) && !string.IsNullOrWhiteSpace(AccessKey);
    public Dictionary<string, string> ToCredentials() => new()
    {
        ["appKey"] = AppKey,
        ["accessKey"] = AccessKey,
        ["resourceId"] = ResourceId,
        ["uid"] = Uid
    };
}

public sealed record DeepgramASRConfig(
    string ApiKey,
    string Model,
    string Language,
    bool Numerals) : IASRProviderConfig
{
    public ASRProvider Provider => ASRProvider.Deepgram;
    public bool IsValid => !string.IsNullOrWhiteSpace(ApiKey);
    public Dictionary<string, string> ToCredentials() => new()
    {
        ["apiKey"] = ApiKey,
        ["model"] = Model,
        ["language"] = Language,
        ["numerals"] = Numerals ? "true" : "false"
    };
}

public sealed record SonioxASRConfig(string ApiKey, string Model) : IASRProviderConfig
{
    public ASRProvider Provider => ASRProvider.Soniox;
    public bool IsValid => !string.IsNullOrWhiteSpace(ApiKey);
    public Dictionary<string, string> ToCredentials() => new()
    {
        ["apiKey"] = ApiKey,
        ["model"] = Model
    };
}

public sealed record OpenAIASRConfig(string ApiKey, string Model, string BaseUrl) : IASRProviderConfig
{
    public ASRProvider Provider => ASRProvider.OpenAI;
    public bool IsValid => !string.IsNullOrWhiteSpace(ApiKey);
    public Dictionary<string, string> ToCredentials() => new()
    {
        ["apiKey"] = ApiKey,
        ["model"] = Model,
        ["baseURL"] = BaseUrl
    };
}

public sealed record ASRProviderDescriptor(
    ASRProvider Provider,
    string DisplayName,
    IReadOnlyList<CredentialField> Fields,
    Func<Dictionary<string, string>, IASRProviderConfig?> CreateConfig,
    Func<ISpeechRecognizer> CreateClient,
    bool IsStreaming);

public static class ASRProviderRegistry
{
    public static IReadOnlyDictionary<ASRProvider, ASRProviderDescriptor> All { get; } =
        new Dictionary<ASRProvider, ASRProviderDescriptor>
        {
            [ASRProvider.Volcano] = new(
                ASRProvider.Volcano,
                "火山引擎 / Doubao",
                [
                    new("appKey", "应用 ID", "APPID", false, false, ""),
                    new("accessKey", "访问令牌", "访问令牌", true, false, ""),
                    new("resourceId", "模型", "volc.seedasr.sauc.duration", false, false, "volc.seedasr.sauc.duration")
                ],
                values => new VolcanoASRConfig(
                    Get(values, "appKey"),
                    Get(values, "accessKey"),
                    EmptyToDefault(Get(values, "resourceId"), "volc.seedasr.sauc.duration"),
                    EmptyToDefault(Get(values, "uid"), Environment.UserName)),
                () => new VolcanoASRClient(),
                true),
            [ASRProvider.Deepgram] = new(
                ASRProvider.Deepgram,
                "Deepgram",
                [
                    new("apiKey", "接口密钥", "粘贴你的接口密钥", true, false, ""),
                    new("model", "模型", "nova-3", false, false, "nova-3"),
                    new("language", "语言", "zh", false, false, "zh"),
                    new("numerals", "数字格式", "false", false, true, "false")
                ],
                values => new DeepgramASRConfig(
                    Get(values, "apiKey"),
                    EmptyToDefault(Get(values, "model"), "nova-3"),
                    EmptyToDefault(Get(values, "language"), "zh"),
                    string.Equals(Get(values, "numerals"), "true", StringComparison.OrdinalIgnoreCase)),
                () => new DeepgramASRClient(),
                true),
            [ASRProvider.Soniox] = new(
                ASRProvider.Soniox,
                "Soniox",
                [
                    new("apiKey", "接口密钥", "粘贴你的接口密钥", true, false, ""),
                    new("model", "模型", "stt-rt-v4", false, true, "stt-rt-v4")
                ],
                values => new SonioxASRConfig(
                    Get(values, "apiKey"),
                    EmptyToDefault(Get(values, "model"), "stt-rt-v4")),
                () => new SonioxASRClient(),
                true),
            [ASRProvider.OpenAI] = new(
                ASRProvider.OpenAI,
                "OpenAI",
                [
                    new("apiKey", "接口密钥", "sk-...", true, false, ""),
                    new("model", "模型", "gpt-4o-transcribe", false, true, "gpt-4o-transcribe"),
                    new("baseURL", "基础地址", "https://api.openai.com/v1", false, true, "https://api.openai.com/v1")
                ],
                values => new OpenAIASRConfig(
                    Get(values, "apiKey"),
                    EmptyToDefault(Get(values, "model"), "gpt-4o-transcribe"),
                    EmptyToDefault(Get(values, "baseURL"), "https://api.openai.com/v1")),
                () => new OpenAIASRClient(),
                false)
        };

    public static ASRProviderDescriptor Get(ASRProvider provider) => All[provider];

    private static string Get(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value.Trim() : "";

    private static string EmptyToDefault(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public sealed record LLMProviderDescriptor(
    LLMProvider Provider,
    string DisplayName,
    IReadOnlyList<CredentialField> Fields,
    Func<Dictionary<string, string>, LLMConfig?> CreateConfig,
    Func<ILLMClient> CreateClient);

public static class LLMProviderRegistry
{
    public static IReadOnlyDictionary<LLMProvider, LLMProviderDescriptor> All { get; } =
        new Dictionary<LLMProvider, LLMProviderDescriptor>
        {
            [LLMProvider.Doubao] = new(
                LLMProvider.Doubao,
                "Doubao / OpenAI 兼容",
                CommonOpenAIFields("https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-flash-250615"),
                values => OpenAICompatibleConfig(values, "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-flash-250615"),
                () => new OpenAICompatibleLLMClient()),
            [LLMProvider.OpenAICompatible] = new(
                LLMProvider.OpenAICompatible,
                "OpenAI 兼容",
                CommonOpenAIFields("https://api.openai.com/v1", "gpt-4o-mini"),
                values => OpenAICompatibleConfig(values, "https://api.openai.com/v1", "gpt-4o-mini"),
                () => new OpenAICompatibleLLMClient()),
            [LLMProvider.Claude] = new(
                LLMProvider.Claude,
                "Claude",
                [
                    new("apiKey", "接口密钥", "sk-ant-...", true, false, ""),
                    new("model", "模型", "claude-3-5-haiku-latest", false, true, "claude-3-5-haiku-latest"),
                    new("baseURL", "基础地址", "https://api.anthropic.com/v1", false, true, "https://api.anthropic.com/v1")
                ],
                values => OpenAICompatibleConfig(values, "https://api.anthropic.com/v1", "claude-3-5-haiku-latest"),
                () => new ClaudeLLMClient())
        };

    public static LLMProviderDescriptor Get(LLMProvider provider) => All[provider];

    private static IReadOnlyList<CredentialField> CommonOpenAIFields(string baseUrl, string model) =>
    [
        new("apiKey", "接口密钥", "粘贴你的接口密钥", true, false, ""),
        new("model", "模型", model, false, true, model),
        new("baseURL", "基础地址", baseUrl, false, true, baseUrl)
    ];

    private static LLMConfig? OpenAICompatibleConfig(Dictionary<string, string> values, string baseUrl, string model)
    {
        var key = values.TryGetValue("apiKey", out var apiKey) ? apiKey.Trim() : "";
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return new LLMConfig(
            key,
            values.TryGetValue("model", out var configuredModel) && !string.IsNullOrWhiteSpace(configuredModel) ? configuredModel.Trim() : model,
            values.TryGetValue("baseURL", out var configuredBaseUrl) && !string.IsNullOrWhiteSpace(configuredBaseUrl) ? configuredBaseUrl.Trim().TrimEnd('/') : baseUrl);
    }
}
