namespace AgentNotify.Insights.Billing;

/// <summary>The fixed set of providers with an official balance or spend endpoint.</summary>
public sealed record BillingProviderInfo(
    string Id,
    string DisplayName,
    string Host,
    string Shows,
    string KeyType,
    string DocsUrl);

public static class BillingCatalog
{
    public static readonly IReadOnlyList<BillingProviderInfo> All =
    [
        new("deepseek", "DeepSeek", "api.deepseek.com", "balance", "API key",
            "https://platform.deepseek.com/api_keys"),
        new("moonshot", "Moonshot AI (Kimi)", "api.moonshot.ai", "balance", "API key",
            "https://platform.moonshot.ai/console/api-keys"),
        new("siliconflow", "SiliconFlow", "api.siliconflow.com", "balance", "API key",
            "https://docs.siliconflow.com/"),
        new("openrouter", "OpenRouter", "openrouter.ai", "spend_history", "API key",
            "https://openrouter.ai/keys"),
        new("openai_admin", "OpenAI", "api.openai.com", "spend_history", "Admin key",
            "https://platform.openai.com/docs/guides/admin-api"),
        new("anthropic_admin", "Anthropic", "api.anthropic.com", "spend_history", "Admin key",
            "https://console.anthropic.com/settings/admin-keys"),
    ];

    public static bool IsSupported(string? provider) =>
        All.Any(p => string.Equals(p.Id, provider, StringComparison.Ordinal));

    public static BillingProviderInfo? Find(string? provider) =>
        All.FirstOrDefault(p => string.Equals(p.Id, provider, StringComparison.Ordinal));

    /// <summary>Providers with no usable official endpoint under a normal key; documented, never probed.</summary>
    public static readonly IReadOnlyList<string> Unsupported =
    [
        "Meta (Muse Spark)",
        "Z.ai",
        "Xiaomi MiMo",
        "Google Gemini",
        "xAI",
    ];
}
