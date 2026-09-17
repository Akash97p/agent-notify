namespace AgentNotify.Core.Router;

/// <summary>A preset for creating an upstream.</summary>
public sealed record RouterPreset(
    string Id,
    string DisplayName,
    string Wire,
    string BaseUrl,
    bool NeedsKey,
    string? DocsUrl);

/// <summary>Catalog of upstream presets.</summary>
public static class RouterPresetCatalog
{
    public static readonly IReadOnlyList<RouterPreset> Presets = new List<RouterPreset>
    {
        new("openai", "OpenAI", RouterWire.OpenAiResponses, "https://api.openai.com/v1", true, "https://platform.openai.com/docs"),
        new("anthropic", "Anthropic", RouterWire.AnthropicMessages, "https://api.anthropic.com/v1", true, "https://docs.anthropic.com/"),
        new("openrouter", "OpenRouter", RouterWire.OpenAiChat, "https://openrouter.ai/api/v1", true, "https://openrouter.ai/docs"),
        new("deepseek", "DeepSeek", RouterWire.OpenAiChat, "https://api.deepseek.com/v1", true, "https://api-docs.deepseek.com/"),
        new("moonshot", "Moonshot (Kimi)", RouterWire.OpenAiChat, "https://api.moonshot.ai/v1", true, "https://platform.moonshot.ai/docs"),
        new("zai", "Z.ai", RouterWire.OpenAiChat, "https://api.z.ai/api/paas/v4", true, "https://docs.z.ai/"),
        new("siliconflow", "SiliconFlow", RouterWire.OpenAiChat, "https://api.siliconflow.com/v1", true, "https://docs.siliconflow.com/"),
        new("groq", "Groq", RouterWire.OpenAiChat, "https://api.groq.com/openai/v1", true, "https://console.groq.com/docs"),
        new("ollama", "Ollama (this computer)", RouterWire.OpenAiChat, "http://127.0.0.1:11434/v1", false, "https://ollama.com/"),
        new("lmstudio", "LM Studio (this computer)", RouterWire.OpenAiChat, "http://127.0.0.1:1234/v1", false, "https://lmstudio.ai/docs"),
    };

    public static RouterPreset? FindById(string id) =>
        Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
