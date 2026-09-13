namespace AgentNotify.Core.Usage;

/// <summary>
/// A dated snapshot of published text-token rates in USD per million tokens.
/// Standard API and OpenCode Go quota rates answer a counterfactual question; they are not
/// subscription spend or an invoice.
/// Keep mappings exact so a new model is visibly unpriced until its rate is verified.
/// </summary>
public static class ApiPriceCatalog
{
    public const string AsOf = "2026-09-13";
    public const string OpenAiSource = "https://developers.openai.com/api/docs/models";
    public const string AnthropicSource = "https://platform.claude.com/docs/en/about-claude/pricing";
    public const string OpenCodeGoSource = "https://opencode.ai/docs/go/";

    private static readonly IReadOnlyDictionary<string, ApiTokenRates> Rates =
        new Dictionary<string, ApiTokenRates>(StringComparer.OrdinalIgnoreCase)
        {
            // OpenAI model cards, standard text-token rates. Codex logs currently expose input,
            // cached input, and output, but no separately billed cache-write counter.
            ["codex:gpt-6-astra"] = new(10m, 1m, 0m, 0m, 50m),
            ["codex:gpt-5.6-sol"] = new(4m, 0.4m, 0m, 0m, 20m),
            ["codex:gpt-5.6"] = new(4m, 0.4m, 0m, 0m, 20m),
            ["codex:gpt-5.6-terra"] = new(2m, 0.2m, 0m, 0m, 12m),
            ["codex:gpt-5.6-luna"] = new(0.2m, 0.02m, 0m, 0m, 1.2m),

            // Anthropic's first-party Claude API table, standard global rates. One-hour cache
            // creation is priced separately from the five-minute bucket.
            ["claude_code:claude-opus-5"] = new(5m, 0.5m, 6.25m, 10m, 25m),
            ["claude_code:claude-sonnet-5"] = new(2m, 0.2m, 2.5m, 4m, 10m),
            ["claude_code:claude-opus-4-8"] = new(5m, 0.5m, 6.25m, 10m, 25m),
            ["claude_code:claude-haiku-4-5"] = new(1m, 0.1m, 1.25m, 2m, 5m)
        };

    private static readonly IReadOnlyDictionary<string, ApiTokenRates> OpenCodeGoRates =
        new Dictionary<string, ApiTokenRates>(StringComparer.OrdinalIgnoreCase)
        {
            // Published OpenCode Go token rates are quota-equivalent, not extra subscription spend.
            // Go does not publish a cache-write price for these models; such records stay unpriced.
            ["muse-spark-1.3-contributor"] = new(0.10m, 0.002m, 0m, 0m, 0.20m),
            ["muse-spark-1.2-contributor"] = new(0.10m, 0.002m, 0m, 0m, 0.20m),
            ["glm-5.3"] = new(1.40m, 0.26m, 0m, 0m, 4.40m)
        };

    public static ApiTokenRates? Find(string source, string provider, string model)
    {
        if (source == "opencode")
        {
            if (provider == "opencode-go")
                return OpenCodeGoRates.TryGetValue(model, out var goRates) ? goRates : null;
            if (provider != "openai") return null;
            source = "codex";
        }
        return Rates.TryGetValue(source + ":" + model, out var rates) ? rates : null;
    }
}

public sealed record ApiTokenRates(decimal Input, decimal CacheRead, decimal CacheWrite5m,
    decimal CacheWrite1h, decimal Output)
{
    public decimal EstimateUsd(TokenCounts counts, long cacheWrite1h)
    {
        var oneHour = Math.Min(counts.CacheWrite, Math.Max(0, cacheWrite1h));
        return (counts.Input * Input + counts.CacheRead * CacheRead +
                (counts.CacheWrite - oneHour) * CacheWrite5m + oneHour * CacheWrite1h +
                counts.Output * Output) / 1_000_000m;
    }
}

public sealed record ApiCostEstimate(decimal PricedUsd, int UnpricedEvents, long UnpricedTokens)
{
    public bool Complete => UnpricedEvents == 0;
}
