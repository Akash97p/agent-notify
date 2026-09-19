namespace AgentNotify.Insights.Usage;

/// <summary>
/// A dated snapshot of published text-token rates in USD per million tokens.
/// Standard API and OpenCode Go quota rates answer a counterfactual question; they are not
/// subscription spend or an invoice.
/// Keep mappings exact so a new model is visibly unpriced until its rate is verified.
/// </summary>
public static class ApiPriceCatalog
{
    public const string AsOf = "2026-09-17";
    public const string OpenCodeGoAsOf = "2026-09-17";
    public const string OpenAiSource = "https://developers.openai.com/api/docs/pricing";
    public const string AnthropicSource = "https://platform.claude.com/docs/en/about-claude/pricing";
    public const string MetaSource = "https://developer.meta.com/ai/products/meta-model-api/";
    public const string GoogleSource = "https://ai.google.dev/gemini-api/docs/pricing";
    public const string ZaiSource = "https://docs.z.ai/guides/overview/pricing";
    public const string XiaomiSource = "https://mimo.mi.com/docs/en-US/price/pay-as-you-go";
    public const string OpenCodeZenSource = "https://opencode.ai/docs/zen/";
    public const string OpenCodeGoSource = "https://opencode.ai/docs/go/";

    private const long OpenAiLongContextTokens = 272_000;
    private const long GoogleLongContextTokens = 200_000;

    /// <summary>Standard rates keyed by <c>provider:model</c>.</summary>
    private static readonly IReadOnlyDictionary<string, Price> Standard =
        new Dictionary<string, Price>(StringComparer.OrdinalIgnoreCase)
        {
            // OpenAI pricing page, Standard tier. Codex logs expose input, cached input, and output,
            // but no separately billed cache-write counter. GPT-5.5 and GPT-5.4 charge 2x input and
            // 1.5x output for a request with more than 272K input tokens.
            ["openai:gpt-6-astra"] = Flat(10m, 1m, 50m),
            ["openai:gpt-5.6-sol"] = Flat(4m, 0.4m, 20m),
            ["openai:gpt-5.6"] = Flat(4m, 0.4m, 20m),
            ["openai:gpt-5.6-terra"] = Flat(2m, 0.2m, 12m),
            ["openai:gpt-5.6-luna"] = Flat(0.2m, 0.02m, 1.2m),
            ["openai:gpt-5.5"] = new(Rates(5m, 0.5m, 30m), Rates(10m, 1m, 45m), OpenAiLongContextTokens),
            ["openai:gpt-5.4"] = new(Rates(2.5m, 0.25m, 15m), Rates(5m, 0.5m, 22.5m), OpenAiLongContextTokens),
            ["openai:gpt-5.4-mini"] = Flat(0.75m, 0.075m, 4.5m),
            ["openai:gpt-5.4-nano"] = Flat(0.2m, 0.02m, 1.25m),
            ["openai:gpt-5.3-codex"] = Flat(1.75m, 0.175m, 14m),
            ["openai:gpt-5.2"] = Flat(1.75m, 0.175m, 14m),
            ["openai:gpt-5.2-codex"] = Flat(1.75m, 0.175m, 14m),
            ["openai:gpt-5.1"] = Flat(1.25m, 0.125m, 10m),
            ["openai:gpt-5.1-codex"] = Flat(1.25m, 0.125m, 10m),
            ["openai:gpt-5.1-codex-max"] = Flat(1.25m, 0.125m, 10m),
            ["openai:gpt-5"] = Flat(1.25m, 0.125m, 10m),
            ["openai:gpt-5-codex"] = Flat(1.25m, 0.125m, 10m),
            ["openai:gpt-5-mini"] = Flat(0.25m, 0.025m, 2m),
            ["openai:gpt-5-nano"] = Flat(0.05m, 0.005m, 0.4m),

            // Anthropic's first-party Claude API table, standard global rates. One-hour cache
            // creation is priced separately from the five-minute bucket.
            ["anthropic:claude-opus-5"] = Flat(new ApiTokenRates(5m, 0.5m, 6.25m, 10m, 25m)),
            ["anthropic:claude-sonnet-5"] = Flat(new ApiTokenRates(2m, 0.2m, 2.5m, 4m, 10m)),
            ["anthropic:claude-opus-4-8"] = Flat(new ApiTokenRates(5m, 0.5m, 6.25m, 10m, 25m)),
            ["anthropic:claude-haiku-4-5"] = Flat(new ApiTokenRates(1m, 0.1m, 1.25m, 2m, 5m)),

            // Meta Model API. Contributor variants may be used to improve Meta's products.
            ["meta:muse-spark-1.3"] = Flat(1.25m, 0.15m, 4.25m),
            ["meta:muse-spark-1.3-contributor"] = Flat(0.10m, 0.002m, 0.20m),
            ["meta:muse-spark-1.2"] = Flat(1.25m, 0.15m, 4.25m),
            ["meta:muse-spark-1.2-contributor"] = Flat(0.10m, 0.002m, 0.20m),
            ["meta:muse-spark-1.1"] = Flat(1.25m, 0.15m, 4.25m),

            // Gemini Developer API, paid tier. Pro models charge more above 200K prompt tokens.
            // Gemini 3 Flash Preview publishes no caching rate, so cached records stay unpriced.
            ["google:gemini-3.1-pro-preview"] = new(Rates(2m, 0.2m, 12m), Rates(4m, 0.4m, 18m), GoogleLongContextTokens),
            ["google:gemini-3-flash-preview"] = new(Rates(0.5m, 0m, 3m), null, null, CacheReadPriced: false),
            ["google:gemini-3.5-flash-lite"] = Flat(0.3m, 0.03m, 2.5m),
            ["google:gemini-3.1-flash-lite"] = Flat(0.25m, 0.025m, 1.5m),
            ["google:gemini-2.5-pro"] = new(Rates(1.25m, 0.125m, 10m), Rates(2.5m, 0.25m, 15m), GoogleLongContextTokens),
            ["google:gemini-2.5-flash"] = Flat(0.3m, 0.03m, 2.5m),

            // Z.ai. Cached input storage is free for a limited time and is not a token counter here.
            ["zai:glm-5.2"] = Flat(1.4m, 0.26m, 4.4m),
            ["zai:glm-5.1"] = Flat(1.4m, 0.26m, 4.4m),
            ["zai:glm-4.7"] = Flat(0.6m, 0.11m, 2.2m),
            ["zai:glm-4.7-flash"] = Flat(0m, 0m, 0m),

            // Xiaomi MiMo pay-as-you-go (overseas). Cache writes are free for a limited time.
            ["xiaomi:mimo-v2.5-pro"] = Flat(0.435m, 0.0036m, 0.87m),
            ["xiaomi:mimo-v2.5"] = Flat(0.14m, 0.0028m, 0.28m)
        };

    /// <summary>OpenAI's fast (priority) tier. A model missing here stays unpriced in fast mode.</summary>
    private static readonly IReadOnlyDictionary<string, ApiTokenRates> OpenAiFast =
        new Dictionary<string, ApiTokenRates>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = Rates(8m, 0.8m, 40m),
            ["gpt-5.6"] = Rates(8m, 0.8m, 40m),
            ["gpt-5.6-terra"] = Rates(4m, 0.4m, 24m),
            ["gpt-5.6-luna"] = Rates(0.4m, 0.04m, 2.4m),
            ["gpt-5.5"] = Rates(12.5m, 1.25m, 75m),
            ["gpt-5.4"] = Rates(5m, 0.5m, 30m),
            ["gpt-5.4-mini"] = Rates(1.5m, 0.15m, 9m),
            ["gpt-5.3-codex"] = Rates(3.5m, 0.35m, 28m),
            ["gpt-5.2"] = Rates(3.5m, 0.35m, 28m),
            ["gpt-5.1"] = Rates(2.5m, 0.25m, 20m),
            ["gpt-5"] = Rates(2.5m, 0.25m, 20m),
            ["gpt-5-mini"] = Rates(0.45m, 0.045m, 3.6m)
        };

    /// <summary>Provider IDs agents write for the same first-party API.</summary>
    private static readonly IReadOnlyDictionary<string, string> ProviderAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["z-ai"] = "zai",
            ["zhipuai"] = "zai",
            ["mi"] = "xiaomi",
            ["gemini"] = "google"
        };

    private static readonly IReadOnlyDictionary<string, OpenCodeGoPrice> OpenCodeGoPrices =
        new Dictionary<string, OpenCodeGoPrice>(StringComparer.OrdinalIgnoreCase)
        {
            // Published OpenCode Go token rates are quota-equivalent, not extra subscription spend.
            // Only exact IDs with published rates are included. Context-tiered models remain unknown
            // because the local ledger cannot prove which rate applied; peak-priced models use the
            // request time against the published UTC schedule.
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
            ["hy3"] = Go(0.14m, 0.035m, null, 0.58m, 60m),
            ["deepseek-v4-pro"] = Go(0.66m, 0.022m, null, 1.98m, 15m) with { Peak = Rates(1.32m, 0.044m, 3.96m) },
            ["deepseek-v4-flash"] = Go(0.15m, 0.003m, null, 0.60m, 30m) with { Peak = Rates(0.30m, 0.006m, 1.20m) }
        };

    private static ApiTokenRates Rates(decimal input, decimal cacheRead, decimal output) =>
        new(input, cacheRead, 0m, 0m, output);

    private static Price Flat(decimal input, decimal cacheRead, decimal output) => Flat(Rates(input, cacheRead, output));

    private static Price Flat(ApiTokenRates rates) => new(rates, null, null);

    private static OpenCodeGoPrice Go(decimal input, decimal cacheRead, decimal? cacheWrite,
        decimal output, decimal monthlyLimit) =>
        new(new ApiTokenRates(input, cacheRead, cacheWrite ?? 0m, cacheWrite ?? 0m, output),
            monthlyLimit, cacheWrite.HasValue);

    public static decimal? OpenCodeGoMonthlyLimit(string model) =>
        OpenCodeGoPrices.TryGetValue(model, out var price) ? price.MonthlyLimit : null;

    /// <summary>OpenCode Go's peak hours: 01:00–04:00 and 06:00–10:00 UTC on weekdays.</summary>
    public static bool IsOpenCodeGoPeak(DateTimeOffset time)
    {
        var utc = time.UtcDateTime;
        if (utc.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        return utc.Hour is >= 1 and < 4 or >= 6 and < 10;
    }

    /// <summary>The OpenCode Go rate for one request, or null when it cannot be priced.</summary>
    public static ApiTokenRates? OpenCodeGoRate(string model, DateTimeOffset time, TokenCounts counts)
    {
        if (!OpenCodeGoPrices.TryGetValue(model, out var price) || counts.CacheWrite > 0 && !price.HasCacheWriteRate)
            return null;
        return price.Peak is not null && IsOpenCodeGoPeak(time) ? price.Peak : price.Rates;
    }

    /// <summary>
    /// The published base rate for a model as an agent reported it, for display. Individual
    /// records may use a different tier; <see cref="RateFor"/> decides that.
    /// </summary>
    public static ApiTokenRates? Find(string source, string provider, string model)
    {
        var resolved = Resolve(source, provider, model);
        return resolved switch
        {
            { Kind: PriceKind.Free } => new ApiTokenRates(0m, 0m, 0m, 0m, 0m),
            { Kind: PriceKind.OpenCodeGo } => OpenCodeGoPrices.TryGetValue(resolved.Model, out var go) ? go.Rates : null,
            { Kind: PriceKind.Standard } => Standard.TryGetValue(resolved.Provider + ":" + resolved.Model, out var price) ? price.Base : null,
            _ => null
        };
    }

    /// <summary>
    /// The rate one usage record was charged at: the long-context tier when its prompt exceeds the
    /// threshold, OpenAI's fast tier for priority requests, and OpenCode Go's peak rate by time.
    /// Null when no published rate covers the record.
    /// </summary>
    public static ApiTokenRates? RateFor(string source, string provider, string model, DateTimeOffset time,
        TokenCounts counts, string? serviceTier = null)
    {
        var resolved = Resolve(source, provider, model);
        switch (resolved.Kind)
        {
            case PriceKind.Free:
                return new ApiTokenRates(0m, 0m, 0m, 0m, 0m);
            case PriceKind.OpenCodeGo:
                return OpenCodeGoRate(resolved.Model, time, counts);
            case PriceKind.Standard:
                break;
            default:
                return null;
        }
        if (!Standard.TryGetValue(resolved.Provider + ":" + resolved.Model, out var price)) return null;
        if (counts.CacheRead > 0 && !price.CacheReadPriced) return null;
        var prompt = counts.Input + counts.CacheRead + counts.CacheWrite;
        var longContext = price.LongContextTokens is { } threshold && prompt > threshold;

        var fast = resolved.Fast || serviceTier is "priority" or "fast";
        if (fast)
        {
            // Fast-tier long-context rates are not published.
            if (resolved.Provider != "openai" || longContext || !OpenAiFast.TryGetValue(resolved.Model, out var fastRates))
                return null;
            return fastRates;
        }
        if (!longContext) return price.Base;
        return price.LongContext;
    }

    /// <summary>
    /// Maps an agent's source and provider ID onto a price table. Codex, Claude Code, Muse Code, and
    /// the Gemini CLI each talk to one provider; OpenCode and Kilo record the provider per message.
    /// </summary>
    private static Resolved Resolve(string source, string provider, string model)
    {
        switch (source)
        {
            case "codex": provider = "openai"; break;
            case "claude_code": provider = "anthropic"; break;
            case "muse": provider = "meta"; break;
            case "gemini_cli": provider = "google"; break;
            case "opencode" or "kilo":
                if (source == "opencode" && provider == "opencode-go") return new(PriceKind.OpenCodeGo, provider, model, false);
                // OpenCode Zen's "-free" and Kilo's ":free" models are published at no charge.
                if (source == "opencode" && provider == "opencode" && model.EndsWith("-free", StringComparison.OrdinalIgnoreCase) ||
                    source == "kilo" && provider == "kilo" &&
                    (model.EndsWith(":free", StringComparison.OrdinalIgnoreCase) || model.EndsWith("/free", StringComparison.OrdinalIgnoreCase)))
                    return new(PriceKind.Free, provider, model, false);
                provider = ProviderAliases.GetValueOrDefault(provider, provider);
                break;
            default:
                return new(PriceKind.None, provider, model, false);
        }
        // A "-fast" suffix is OpenCode's name for the same model on OpenAI's fast tier.
        if (provider == "openai" && model.EndsWith("-fast", StringComparison.OrdinalIgnoreCase))
            return new(PriceKind.Standard, provider, model[..^5], true);
        return new(PriceKind.Standard, provider, model, false);
    }

    private enum PriceKind { None, Standard, Free, OpenCodeGo }

    private sealed record Resolved(PriceKind Kind, string Provider, string Model, bool Fast);

    private sealed record Price(ApiTokenRates Base, ApiTokenRates? LongContext, long? LongContextTokens,
        bool CacheReadPriced = true);

    private sealed record OpenCodeGoPrice(ApiTokenRates Rates, decimal MonthlyLimit, bool HasCacheWriteRate)
    {
        public ApiTokenRates? Peak { get; init; }
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
