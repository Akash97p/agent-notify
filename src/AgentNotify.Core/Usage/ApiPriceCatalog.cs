namespace AgentNotify.Core.Usage;

/// <summary>
/// A dated snapshot of published text-token rates in USD per million tokens.
/// Standard API and OpenCode Go quota rates answer a counterfactual question; they are not
/// subscription spend or an invoice.
/// Keep mappings exact so a new model is visibly unpriced until its rate is verified.
/// </summary>
public static class ApiPriceCatalog
{
    public const string AsOf = "2026-09-14";
    public const string OpenCodeGoAsOf = "2026-09-14";
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

    private static readonly IReadOnlyDictionary<string, OpenCodeGoPrice> OpenCodeGoPrices =
        new Dictionary<string, OpenCodeGoPrice>(StringComparer.OrdinalIgnoreCase)
        {
            // Published OpenCode Go token rates are quota-equivalent, not extra subscription spend.
            // Only exact IDs with one published rate are included. Context-tiered and peak/off-peak
            // models remain unknown because the local ledger cannot prove which rate applied.
            // A null cache-write rate means that a record containing cache writes stays unpriced.
            ["glm-5.3-flash"] = Go(0.15m, 0.03m, null, 0.50m, 60m),
            ["glm-5.3"] = Go(1.40m, 0.26m, null, 4.40m, 15m),
            ["glm-5.2"] = Go(1.40m, 0.26m, null, 4.40m, 60m),
            ["glm-5.1"] = Go(1.40m, 0.26m, null, 4.40m, 60m),
            ["kimi-k3"] = Go(3m, 0.30m, null, 15m, 15m),
            ["kimi-k2.7-code"] = Go(0.95m, 0.19m, null, 4m, 60m),
            ["kimi-k2.6"] = Go(0.95m, 0.16m, null, 4m, 60m),
            ["longcat-2.0"] = Go(0.30m, 0.006m, null, 1.20m, 60m),
            ["mimo-v2.5"] = Go(0.14m, 0.0028m, null, 0.28m, 60m),
            ["mimo-v2.5-pro"] = Go(0.435m, 0.003625m, null, 0.87m, 15m),
            ["minimax-m3"] = Go(0.30m, 0.06m, null, 1.20m, 60m),
            ["minimax-m2.7"] = Go(0.30m, 0.06m, 0.375m, 1.20m, 60m),
            ["minimax-m2.5"] = Go(0.30m, 0.06m, 0.375m, 1.20m, 60m),
            ["muse-spark-1.3-contributor"] = Go(0.10m, 0.002m, null, 0.20m, 60m),
            ["muse-spark-1.2-contributor"] = Go(0.10m, 0.002m, null, 0.20m, 60m),
            ["qwen3.8-max"] = Go(2m, 0.25m, 2.50m, 6m, 15m),
            ["qwen3.8-flash"] = Go(0.15m, 0.016m, 0.20m, 0.47m, 30m),
            ["qwen3.7-max"] = Go(2.50m, 0.50m, 3.125m, 7.50m, 30m),
            ["hy4-preview"] = Go(0.834m, 0.042m, null, 2.501m, 30m),
            ["hy3"] = Go(0.14m, 0.035m, null, 0.58m, 60m)
        };

    private static OpenCodeGoPrice Go(decimal input, decimal cacheRead, decimal? cacheWrite,
        decimal output, decimal monthlyLimit) =>
        new(new ApiTokenRates(input, cacheRead, cacheWrite ?? 0m, cacheWrite ?? 0m, output),
            monthlyLimit, cacheWrite.HasValue);

    public static decimal? OpenCodeGoMonthlyLimit(string model) =>
        OpenCodeGoPrices.TryGetValue(model, out var price) ? price.MonthlyLimit : null;

    public static bool CanPriceOpenCodeGo(string model, TokenCounts counts) =>
        OpenCodeGoPrices.TryGetValue(model, out var price) &&
        (counts.CacheWrite == 0 || price.HasCacheWriteRate);

    public static ApiTokenRates? Find(string source, string provider, string model)
    {
        if (source == "opencode")
        {
            if (provider == "opencode-go")
                return OpenCodeGoPrices.TryGetValue(model, out var goPrice) ? goPrice.Rates : null;
            if (provider != "openai") return null;
            source = "codex";
        }
        return Rates.TryGetValue(source + ":" + model, out var rates) ? rates : null;
    }

    private sealed record OpenCodeGoPrice(ApiTokenRates Rates, decimal MonthlyLimit, bool HasCacheWriteRate);
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
