using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StardewAiAssistant.Models;

namespace StardewAiAssistant.Services;

public sealed class AiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ModConfig _config;

    public AiClient(ModConfig config)
    {
        _config = config;
    }

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.TimeoutMs))
        };

        if (_config.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            return await CompleteWithOllamaAsync(client, systemPrompt, messages, cancellationToken);

        return await CompleteWithOpenAiCompatibleAsync(client, systemPrompt, messages, cancellationToken);
    }

    private async Task<string> CompleteWithOpenAiCompatibleAsync(HttpClient client, string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            return "还没有配置 API Key。请在 config.json 里填写 ApiKey，或把 Provider 改成 Ollama 使用本地模型。";

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        var requestMessages = new List<object> { new { role = "system", content = systemPrompt } };
        requestMessages.AddRange(messages.Select(message => new { role = message.Role, content = message.Text }));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _config.Model,
            ["messages"] = requestMessages,
            ["temperature"] = 0.2,
            ["stream"] = false
        };

        AddProviderSpecificOptions(payload);

        var endpoint = $"{_config.BaseUrl.TrimEnd('/')}/chat/completions";
        using var response = await client.PostAsync(
            endpoint,
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            cancellationToken
        );

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return $"AI 请求失败：{(int)response.StatusCode} {response.ReasonPhrase}\n{Trim(body, 300)}";

        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?.Trim() ?? "AI 没有返回内容。";
    }

    private void AddProviderSpecificOptions(Dictionary<string, object?> payload)
    {
        if (!_config.BaseUrl.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase))
            return;

        if (string.IsNullOrWhiteSpace(_config.DeepSeekThinking))
            return;

        var thinking = _config.DeepSeekThinking.Trim().ToLowerInvariant();
        if (thinking is not ("enabled" or "disabled"))
            thinking = "disabled";

        var options = new Dictionary<string, object?>
        {
            ["type"] = thinking
        };

        if (thinking == "enabled" && !string.IsNullOrWhiteSpace(_config.DeepSeekReasoningEffort))
            options["reasoning_effort"] = _config.DeepSeekReasoningEffort;

        payload["thinking"] = options;
    }

    private async Task<string> CompleteWithOllamaAsync(HttpClient client, string systemPrompt, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var requestMessages = new List<object> { new { role = "system", content = systemPrompt } };
        requestMessages.AddRange(messages.Select(message => new { role = message.Role, content = message.Text }));

        var payload = new
        {
            model = _config.OllamaModel,
            messages = requestMessages,
            stream = false,
            options = new { temperature = 0.2 }
        };

        var endpoint = $"{_config.OllamaBaseUrl.TrimEnd('/')}/api/chat";
        using var response = await client.PostAsync(
            endpoint,
            new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            cancellationToken
        );

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            return $"Ollama 请求失败：{(int)response.StatusCode} {response.ReasonPhrase}\n{Trim(body, 300)}";

        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?.Trim() ?? "Ollama 没有返回内容。";
    }

    private static string Trim(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
