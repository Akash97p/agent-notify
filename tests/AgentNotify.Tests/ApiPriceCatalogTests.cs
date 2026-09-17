using AgentNotify.Core.Usage;

namespace AgentNotify.Tests;

public sealed class ApiPriceCatalogTests
{
    private static readonly DateTimeOffset Weekday = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TokenCounts Million = new(1_000_000, 0, 0, 0, 0);

    private static decimal? Usd(string source, string provider, string model, TokenCounts counts,
        DateTimeOffset? time = null, string? tier = null) =>
        ApiPriceCatalog.RateFor(source, provider, model, time ?? Weekday, counts, tier)?.EstimateUsd(counts, 0);

    [Theory]
    [InlineData("codex", "", "gpt-5.3-codex", 1.75)]
    [InlineData("codex", "", "gpt-5.5", 5)]
    [InlineData("codex", "", "gpt-5.1-codex-max", 1.25)]
    [InlineData("codex", "", "gpt-5-codex", 1.25)]
    [InlineData("opencode", "openai", "gpt-5.4-mini", 0.75)]
    [InlineData("muse", "", "muse-spark-1.2-contributor", 0.10)]
    [InlineData("opencode", "meta", "muse-spark-1.2", 1.25)]
    [InlineData("gemini_cli", "", "gemini-2.5-pro", 1.25)]
    [InlineData("kilo", "zai", "glm-5.1", 1.4)]
    [InlineData("kilo", "mi", "mimo-v2.5-pro", 0.435)]
    [InlineData("kilo", "xiaomi", "mimo-v2.5-pro", 0.435)]
    public void PublishedInputRatesApplyPerProvider(string source, string provider, string model, decimal usd) =>
        // 100K prompt tokens stay below every long-context threshold.
        Assert.Equal(usd / 10m, Usd(source, provider, model, new TokenCounts(100_000, 0, 0, 0, 0)));

    [Theory]
    [InlineData("codex", "", "Unknown model")]
    [InlineData("opencode", "rabit", "deepseek-pro")]
    [InlineData("kilo", "poolside", "poolside/laguna-m.1")]
    [InlineData("gemini_cli", "", "gemini-3-pro-preview")]
    [InlineData("kilo", "kilo", "anthropic/claude-opus-5")]
    public void ModelsWithoutAVerifiedRateStayUnpriced(string source, string provider, string model) =>
        Assert.Null(ApiPriceCatalog.RateFor(source, provider, model, Weekday, Million));

    [Theory]
    [InlineData("opencode", "opencode", "muse-spark-1.3-contributor-free")]
    [InlineData("opencode", "opencode", "deepseek-v4-flash-free")]
    [InlineData("kilo", "kilo", "nvidia/nemotron-3-ultra-550b-a55b:free")]
    [InlineData("kilo", "kilo", "kilo-auto/free")]
    public void FreeGatewayModelsCostNothing(string source, string provider, string model) =>
        Assert.Equal(0m, Usd(source, provider, model, Million));

    [Fact]
    public void LongPromptsUseTheLongContextTier()
    {
        var shortPrompt = new TokenCounts(200_000, 1_000_000, 72_000, 0, 0);
        var longPrompt = new TokenCounts(200_001, 1_000_000, 72_000, 0, 0);
        Assert.Equal(1m + 0.036m + 30m, Usd("codex", "", "gpt-5.5", shortPrompt));
        Assert.Equal(2.00001m + 0.072m + 45m, Usd("codex", "", "gpt-5.5", longPrompt));
        Assert.Equal(1m + 15m + 0.0144m, Usd("gemini_cli", "", "gemini-2.5-pro", new TokenCounts(400_000, 1_000_000, 57_600, 0, 0)));
    }

    [Fact]
    public void FastModeUsesPriorityRatesAndLeavesUnpublishedFastRatesUnpriced()
    {
        Assert.Equal(8m, Usd("codex", "", "gpt-5.6-sol", Million, tier: "priority"));
        Assert.Equal(0.4m, Usd("opencode", "openai", "gpt-5.6-luna-fast", Million));
        Assert.Equal(1.5m, Usd("opencode", "openai", "gpt-5.4-mini-fast", Million));
        Assert.Null(Usd("codex", "", "gpt-5.1-codex-max", Million, tier: "priority"));
        Assert.Equal(4m, Usd("codex", "", "gpt-5.6-sol", Million, tier: "default"));
    }

    [Fact]
    public void GeminiFlashPreviewWithCachedInputStaysUnpriced()
    {
        Assert.Equal(0.5m, Usd("gemini_cli", "", "gemini-3-flash-preview", Million));
        Assert.Null(Usd("gemini_cli", "", "gemini-3-flash-preview", new TokenCounts(1_000_000, 0, 10, 0, 0)));
    }

    [Theory]
    [InlineData("2026-09-16T01:00:00Z", true)]
    [InlineData("2026-09-16T03:59:59Z", true)]
    [InlineData("2026-09-16T04:00:00Z", false)]
    [InlineData("2026-09-16T06:30:00Z", true)]
    [InlineData("2026-09-16T10:00:00Z", false)]
    [InlineData("2026-09-19T07:00:00Z", false)]
    public void OpenCodeGoPeakHoursFollowTheUtcWeekdaySchedule(string time, bool peak)
    {
        var at = DateTimeOffset.Parse(time, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(peak, ApiPriceCatalog.IsOpenCodeGoPeak(at));
        Assert.Equal(peak ? 1.32m : 0.66m, Usd("opencode", "opencode-go", "deepseek-v4-pro", Million, at));
    }
}
