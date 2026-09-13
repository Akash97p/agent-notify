namespace AgentNotify.Core.Usage;

/// <summary>
/// A dated snapshot of published standard, text-token API prices in USD per million tokens.
/// These rates answer a counterfactual question; they are not subscription spend or an invoice.
/// Keep mappings exact so a new model is visibly unpriced until its rate is verified.
/// </summary>
public static class ApiPriceCatalog
{
    public const string AsOf = "2026-09-13";
    public const string OpenAiSource = "https://developers.openai.com/api/docs/models";
    public const string AnthropicSource = "https://platform.claude.com/docs/en/about-claude/pricing";

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

    public static ApiTokenRates? Find(string source, string model) =>
        Rates.TryGetValue(source + ":" + model, out var rates) ? rates : null;
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
