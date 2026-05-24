using StardewModdingAPI;

namespace StardewAiAssistant.Models;

public sealed class ModConfig
{
    public SButton OpenMenuButton { get; set; } = SButton.L;
    public string Provider { get; set; } = "OpenAICompatible";
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "qwen2.5:7b";
    public string DeepSeekThinking { get; set; } = "disabled";
    public string DeepSeekReasoningEffort { get; set; } = "high";
    public string Language { get; set; } = "zh-CN";
    public int TimeoutMs { get; set; } = 30000;
    public int MaxKnowledgeEntries { get; set; } = 6;
    public bool PreferLocalAnswer { get; set; } = true;
    public bool EnableDebugLogging { get; set; }
}
