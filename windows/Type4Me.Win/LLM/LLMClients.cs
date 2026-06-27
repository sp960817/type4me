using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Type4Me.Win.Core;

namespace Type4Me.Win.LLM;

public interface ILLMClient
{
    Task<string> ProcessAsync(string text, string prompt, LLMConfig config, PromptContext context, CancellationToken cancellationToken);
}

public sealed class OpenAICompatibleLLMClient : ILLMClient
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };

    public async Task<string> ProcessAsync(string text, string prompt, LLMConfig config, PromptContext context, CancellationToken cancellationToken)
    {
        var finalPrompt = prompt
            .Replace("{text}", text, StringComparison.Ordinal)
            .Replace("{selected}", context.SelectedText, StringComparison.Ordinal)
            .Replace("{clipboard}", context.ClipboardText, StringComparison.Ordinal);

        var requestBody = JsonSerializer.Serialize(new
        {
            model = config.Model,
            messages = new[] { new { role = "user", content = finalPrompt } },
            stream = false,
            temperature = 0.2
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, LLMHttpHelpers.BuildEndpoint(config.BaseUrl, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LLMHttpHelpers.EnsureSuccess(response, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?.Trim() ?? text;
    }
}

public sealed class ClaudeLLMClient : ILLMClient
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };

    public async Task<string> ProcessAsync(string text, string prompt, LLMConfig config, PromptContext context, CancellationToken cancellationToken)
    {
        var finalPrompt = prompt
            .Replace("{text}", text, StringComparison.Ordinal)
            .Replace("{selected}", context.SelectedText, StringComparison.Ordinal)
            .Replace("{clipboard}", context.ClipboardText, StringComparison.Ordinal);

        var requestBody = JsonSerializer.Serialize(new
        {
            model = config.Model,
            max_tokens = 4096,
            messages = new[] { new { role = "user", content = finalPrompt } },
            stream = false
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, LLMHttpHelpers.BuildEndpoint(config.BaseUrl, "messages"));
        request.Headers.Add("x-api-key", config.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        LLMHttpHelpers.EnsureSuccess(response, body);
        using var json = JsonDocument.Parse(body);
        var pieces = json.RootElement.GetProperty("content")
            .EnumerateArray()
            .Where(e => e.TryGetProperty("text", out _))
            .Select(e => e.GetProperty("text").GetString());
        return string.Concat(pieces).Trim();
    }
}

public static class LLMHttpHelpers
{
    public static string BuildEndpoint(string baseUrl, string endpoint)
    {
        var trimmedBaseUrl = baseUrl.Trim().TrimEnd('/');
        var trimmedEndpoint = endpoint.Trim('/');
        return trimmedBaseUrl.EndsWith("/" + trimmedEndpoint, StringComparison.OrdinalIgnoreCase)
            ? trimmedBaseUrl
            : trimmedBaseUrl + "/" + trimmedEndpoint;
    }

    public static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = ExtractErrorMessage(body);
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"文本模型请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            : $"文本模型请求失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}；{detail}";
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return Limit(error.GetString() ?? "");
                }

                if (error.ValueKind == JsonValueKind.Object)
                {
                    var parts = new List<string>();
                    if (error.TryGetProperty("message", out var message)) parts.Add(message.GetString() ?? "");
                    if (error.TryGetProperty("type", out var type)) parts.Add(type.GetString() ?? "");
                    if (error.TryGetProperty("code", out var code)) parts.Add(code.ToString());
                    return Limit(string.Join("；", parts.Where(p => !string.IsNullOrWhiteSpace(p))));
                }
            }

            if (root.TryGetProperty("message", out var rootMessage))
            {
                return Limit(rootMessage.GetString() ?? "");
            }
        }
        catch (JsonException)
        {
        }

        return Limit(body.ReplaceLineEndings(" ").Trim());
    }

    private static string Limit(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}
