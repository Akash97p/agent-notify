using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Wsl;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Insights.Usage;

public sealed partial class LocalUsageService
{
    private static UsageEvent? ParseClaude(JsonElement row)
    {
        if (String(row, "type") != "assistant") return null;
        var message = Child(row, "message");
        var usage = Child(message, "usage");
        var model = String(message, "model");
        if (usage.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(model) || model.StartsWith('<') ||
            !DateTimeOffset.TryParse(String(row, "timestamp"), out var timestamp)) return null;

        var messageId = String(message, "id");
        var requestId = String(row, "requestId");
        var identity = messageId is not null && requestId is not null ? messageId + ":" + requestId
            : messageId ?? (requestId is null ? String(row, "uuid") : (String(row, "sessionId") ?? "") + ":" + requestId);
        var counts = new TokenCounts(Number(usage, "input_tokens"), Number(usage, "output_tokens"),
            Number(usage, "cache_read_input_tokens"), Number(usage, "cache_creation_input_tokens"), 0);
        if (counts.Total == 0) return null;
        var oneHour = Number(Child(usage, "cache_creation"), "ephemeral_1h_input_tokens");
        return new UsageEvent("claude_code", timestamp, model, "anthropic", String(row, "sessionId") ?? "",
            ProjectInfo.FromDirectory(String(row, "cwd")), identity, counts, Math.Min(counts.CacheWrite, oneHour));
    }

    private static TokenCounts CodexCounters(JsonElement value)
    {
        var input = Number(value, "input_tokens");
        var cached = Math.Min(input, Number(value, "cached_input_tokens"));
        var output = Number(value, "output_tokens");
        return new TokenCounts(input - cached, output, cached, 0, Math.Min(output, Number(value, "reasoning_output_tokens")));
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) ? child : default;

    private static string? String(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long Number(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) ? Math.Max(0, count) : 0;
    }

    private static TokenCounts Sum(IEnumerable<UsageEvent> rows)
    {
        var result = new TokenCounts(0, 0, 0, 0, 0);
        foreach (var row in rows) result = result.Add(row.Counts);
        return result;
    }

    private static UsageModel Model(IGrouping<(string Source, string Provider, string Model), UsageEvent> group) =>
        new(group.Key.Source, group.Key.Provider, group.Key.Model, Sum(group), Estimate(group),
            ApiPriceCatalog.Find(group.Key.Source, group.Key.Provider, group.Key.Model));

    private static ApiCostEstimate Estimate(IEnumerable<UsageEvent> rows)
    {
        decimal priced = 0;
        var unpricedEvents = 0;
        long unpricedTokens = 0;
        foreach (var row in rows)
        {
            var rate = ApiPriceCatalog.RateFor(row.Source, row.Provider, row.Model, row.Timestamp, row.Counts, row.ServiceTier);
            if (rate is null)
            {
                unpricedEvents++;
                unpricedTokens += row.Counts.Total;
            }
            else priced += rate.EstimateUsd(row.Counts, row.CacheWrite1h);
        }
        return new ApiCostEstimate(priced, unpricedEvents, unpricedTokens);
    }

    private sealed record ProjectInfo(string Id, string Name)
    {
        internal static readonly ProjectInfo Unknown = new("unknown", "Unknown project");

        internal static ProjectInfo FromDirectory(string? directory, ProjectInfo? fallback = null)
        {
            if (string.IsNullOrWhiteSpace(directory) || directory.Length > 4096) return fallback ?? Unknown;
            // WSL agents record Linux paths, which are not fully qualified to a Windows broker.
            if (directory[0] == '/' && OperatingSystem.IsWindows()) return FromPosix(directory, fallback);
            if (!Path.IsPathFullyQualified(directory)) return fallback ?? Unknown;
            try
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                var name = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(name)) return fallback ?? Unknown;
                var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
                return new ProjectInfo("p_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant(), name[..Math.Min(name.Length, 120)]);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return fallback ?? Unknown;
            }
        }

        private static ProjectInfo FromPosix(string directory, ProjectInfo? fallback)
        {
            if (directory.Any(char.IsControl)) return fallback ?? Unknown;
            var path = directory.TrimEnd('/');
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (string.IsNullOrWhiteSpace(name)) return fallback ?? Unknown;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
            return new ProjectInfo("p_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant(), name[..Math.Min(name.Length, 120)]);
        }
    }

    private sealed record CachedFile(long Length, DateTime Modified, UsageEvent[] Events);
    private sealed record UsageEvent(string Source, DateTimeOffset Timestamp, string Model, string Provider, string Session,
        ProjectInfo Project, string? Identity, TokenCounts Counts, long CacheWrite1h, string? ServiceTier = null);
}

public readonly record struct TokenCounts(long Input, long Output, long CacheRead, long CacheWrite, long Reasoning)
{
    public long Total => Input + Output + CacheRead + CacheWrite;
    public TokenCounts Add(TokenCounts other) => new(Input + other.Input, Output + other.Output,
        CacheRead + other.CacheRead, CacheWrite + other.CacheWrite, Reasoning + other.Reasoning);
    public TokenCounts Subtract(TokenCounts previous) => new(Math.Max(0, Input - previous.Input),
        Math.Max(0, Output - previous.Output), Math.Max(0, CacheRead - previous.CacheRead),
        Math.Max(0, CacheWrite - previous.CacheWrite), Math.Max(0, Reasoning - previous.Reasoning));
}

public sealed record UsageGroup(string Source, TokenCounts Counts, ApiCostEstimate Cost);
public sealed record UsageModel(string Source, string Provider, string Model, TokenCounts Counts, ApiCostEstimate Cost, ApiTokenRates? Rate);
public sealed record UsageProject(string Id, string Name, TokenCounts Counts, ApiCostEstimate Cost,
    IReadOnlyList<UsageModel> Models);
public sealed record UsageDay(string Date, TokenCounts Counts, ApiCostEstimate Cost);
public sealed record UsageSession(string Id, string Source, string ProjectId, string ProjectName,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt, int Events, TokenCounts Counts,
    ApiCostEstimate Cost, IReadOnlyList<UsageModel> Models);
public sealed record UsageReport(DateTimeOffset ScannedAt, int Days, int FilesScanned, int FilesSkipped,
    int Events, TokenCounts Totals, ApiCostEstimate Cost, IReadOnlyList<UsageGroup> Sources,
    IReadOnlyList<UsageModel> Models, IReadOnlyList<UsageProject> Projects, IReadOnlyList<UsageDay> Daily,
    string PricingAsOf, int SessionCount, IReadOnlyList<UsageSession> Sessions,
    IReadOnlyList<string> WslDistributions)
{
    public string ContractVersion => "4";
}
