namespace AgentNotify.Router;

/// <summary>
/// A provider the owner can add in one step. Everything but the key comes from here: the slug and
/// label an upstream starts with, the wire and base URL, how it authenticates, and — for a provider
/// that serves different models over different wires — which wire each model family uses.
/// </summary>
/// <param name="Kind"><c>api</c> (pay per token with a key), <c>subscription</c> (a monthly plan), or <c>local</c>.</param>
/// <param name="Auth">One of <see cref="RouterAuth"/>.</param>
/// <param name="WireRules">
/// Model-ID prefixes and the wire those models use, checked in order; a model matching none uses
/// <paramref name="Wire"/>. Applied when an upstream's model list is saved.
/// </param>
/// <param name="ExcludePrefixes">Model families listed by the provider that the router cannot speak to.</param>
/// <param name="OpenCodeAuthId">The provider ID OpenCode stores a key under, so a key it already has can be reused.</param>
/// <param name="Unofficial">
/// Reuses a sign-in made for another tool rather than a documented API. Opt-in, and labelled so.
/// </param>
public sealed record RouterPreset(
    string Id,
    string DisplayName,
    string Wire,
    string BaseUrl,
    bool NeedsKey,
    string? DocsUrl,
    string Kind = "api",
    string Auth = RouterAuth.ApiKey,
    string? Blurb = null,
    string? KeyUrl = null,
    IReadOnlyList<(string Prefix, string Wire)>? WireRules = null,
    IReadOnlyList<string>? ExcludePrefixes = null,
    string? OpenCodeAuthId = null,
    bool Unofficial = false)
{
    /// <summary>The wire this preset's provider uses for <paramref name="model"/>.</summary>
    public string WireFor(string model)
    {
        foreach (var (prefix, wire) in WireRules ?? [])
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return wire;
        return Wire;
    }

    /// <summary>Whether the router can serve this model at all.</summary>
    public bool Supports(string model) =>
        !(ExcludePrefixes ?? []).Any(prefix => model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The per-model overrides for a list of models: only those that differ from <see cref="Wire"/>.</summary>
    public Dictionary<string, string> ModelWires(IEnumerable<string> models)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            var wire = WireFor(model);
            if (wire != Wire) map[model] = wire;
        }
        return map;
    }
}

/// <summary>Catalog of upstream presets.</summary>
public static class RouterPresetCatalog
{
    private const string Responses = RouterWire.OpenAiResponses;
    private const string Chat = RouterWire.OpenAiChat;
    private const string Messages = RouterWire.AnthropicMessages;

    public const string CodexChatGptBaseUrl = "https://chatgpt.com/backend-api/codex";
    public const string MetaBaseUrl = "https://api.meta.ai/v1";

    public static readonly IReadOnlyList<RouterPreset> Presets = new List<RouterPreset>
    {
        // Subscriptions: a monthly plan rather than a per-token key.
        new("chatgpt", "ChatGPT plan (Codex)", Responses, CodexChatGptBaseUrl, false,
            "https://developers.openai.com/codex/auth", Kind: "subscription", Auth: RouterAuth.CodexChatGpt,
            Blurb: "Your Plus or Pro plan, through the sign-in Codex already has on this computer. No key.",
            Unofficial: true),
        new("opencode-go", "OpenCode Go", Chat, "https://opencode.ai/zen/go/v1", true,
            "https://opencode.ai/docs/go/", Kind: "subscription",
            Blurb: "The $10 OpenCode plan: Kimi, GLM, DeepSeek, Qwen, MiniMax, Grok, and Muse Spark.",
            KeyUrl: "https://opencode.ai/auth",
            WireRules: [("minimax-", Messages), ("qwen", Messages), ("union-", Messages),
                        ("gpt-", Responses), ("grok-", Responses), ("muse-", Responses)],
            OpenCodeAuthId: "opencode-go"),
        new("muse-code", "Muse Code plan", Responses, MetaBaseUrl, false,
            "https://developer.meta.com/ai/products/muse-code/", Kind: "subscription", Auth: RouterAuth.MuseCode,
            Blurb: "Your Muse Code plan, through the sign-in Muse Code already has on this computer. No key.",
            Unofficial: true),

        // Pay per token.
        new("opencode", "OpenCode Zen", Chat, "https://opencode.ai/zen/v1", true,
            "https://opencode.ai/docs/zen/",
            Blurb: "One key for Claude, GPT, Kimi, GLM, DeepSeek, and more, billed per token.",
            KeyUrl: "https://opencode.ai/auth",
            WireRules: [("claude-", Messages), ("qwen", Messages), ("union-", Messages),
                        ("gpt-", Responses), ("grok-", Responses), ("muse-", Responses)],
            // Zen serves Gemini on Google's own wire, which the router does not speak.
            ExcludePrefixes: ["gemini-"],
            OpenCodeAuthId: "opencode"),
        new("meta", "Meta Model API (Muse Spark)", Responses, MetaBaseUrl, true,
            "https://dev.meta.ai/docs", Blurb: "Muse Spark with a Meta Model API key.",
            KeyUrl: "https://developer.meta.com/ai/products/meta-model-api/",
            // Muse Image and Voice are not chat models.
            ExcludePrefixes: ["muse-image", "muse-voice", "muse-glimmer"]),
        new("openai", "OpenAI", Responses, "https://api.openai.com/v1", true, "https://platform.openai.com/docs",
            KeyUrl: "https://platform.openai.com/api-keys", OpenCodeAuthId: "openai"),
        new("anthropic", "Anthropic", Messages, "https://api.anthropic.com/v1", true, "https://docs.anthropic.com/",
            KeyUrl: "https://console.anthropic.com/settings/keys", OpenCodeAuthId: "anthropic"),
        new("openrouter", "OpenRouter", Chat, "https://openrouter.ai/api/v1", true, "https://openrouter.ai/docs",
            KeyUrl: "https://openrouter.ai/keys", OpenCodeAuthId: "openrouter"),
        new("deepseek", "DeepSeek", Chat, "https://api.deepseek.com/v1", true, "https://api-docs.deepseek.com/",
            KeyUrl: "https://platform.deepseek.com/api_keys", OpenCodeAuthId: "deepseek"),
        new("moonshot", "Moonshot (Kimi)", Chat, "https://api.moonshot.ai/v1", true, "https://platform.moonshot.ai/docs",
            OpenCodeAuthId: "moonshotai"),
        new("zai", "Z.ai", Chat, "https://api.z.ai/api/paas/v4", true, "https://docs.z.ai/", OpenCodeAuthId: "zai"),
        new("siliconflow", "SiliconFlow", Chat, "https://api.siliconflow.com/v1", true, "https://docs.siliconflow.com/"),
        new("groq", "Groq", Chat, "https://api.groq.com/openai/v1", true, "https://console.groq.com/docs",
            OpenCodeAuthId: "groq"),

        // This computer.
        new("ollama", "Ollama", Chat, "http://127.0.0.1:11434/v1", false, "https://ollama.com/", Kind: "local",
            Blurb: "Models running in Ollama on this computer."),
        new("lmstudio", "LM Studio", Chat, "http://127.0.0.1:1234/v1", false, "https://lmstudio.ai/docs", Kind: "local",
            Blurb: "Models loaded in LM Studio on this computer."),
    };

    public static RouterPreset? FindById(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
