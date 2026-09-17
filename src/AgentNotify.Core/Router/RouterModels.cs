namespace AgentNotify.Core.Router;

/// <summary>Wire identifiers for the provider router.</summary>
public static class RouterWire
{
    public const string OpenAiResponses = "openai_responses";
    public const string OpenAiChat = "openai_chat";
    public const string AnthropicMessages = "anthropic_messages";

    public static bool IsValid(string? wire) =>
        wire is OpenAiResponses or OpenAiChat or AnthropicMessages;

    public static string Normalize(string? wire)
    {
        if (!IsValid(wire))
            throw new ArgumentException("Wire must be one of openai_responses, openai_chat, anthropic_messages.");
        return wire!;
    }
}

/// <summary>Public view of a configured upstream.</summary>
public sealed record RouterUpstream(
    string Id,
    string Slug,
    string Label,
    string Wire,
    string BaseUrl,
    bool HasKey,
    IReadOnlyList<string> Models,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Stored upstream including encrypted key.</summary>
public sealed record StoredRouterUpstream(
    string Id,
    string Slug,
    string Label,
    string Wire,
    string BaseUrl,
    string? EncryptedKey,
    IReadOnlyList<string> Models,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A named route with ordered targets.</summary>
public sealed record RouterRoute(
    string Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Targets,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Router-level settings.</summary>
public sealed record RouterSettings(string? DefaultRoute);

/// <summary>Ledger row for a logical router request.</summary>
public sealed record RouterRequestRecord(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string InboundWire,
    string? RequestedModel,
    string? RouteKind,
    string? RouteName,
    string? UpstreamSlug,
    string? Model,
    bool Stream,
    int? Status,
    string? Outcome,
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? ReasoningTokens,
    string? UsageStatus,
    string? ErrorCode);

/// <summary>Ledger row for a single upstream attempt.</summary>
public sealed record RouterAttemptRecord(
    string RequestId,
    int Ordinal,
    string UpstreamSlug,
    string Model,
    string UpstreamWire,
    int? Status,
    string? ErrorCode,
    long? DurationMs,
    bool BytesStreamed,
    DateTimeOffset StartedAt);

/// <summary>A request with its attempts.</summary>
public sealed record RouterLedgerEntry(
    RouterRequestRecord Request,
    IReadOnlyList<RouterAttemptRecord> Attempts);

/// <summary>Aggregated usage for a provider model.</summary>
public sealed record RouterUsageSummary(
    string UpstreamSlug,
    string Model,
    long Count,
    long OkCount,
    long InputTokensSum,
    long CachedInputTokensSum,
    long OutputTokensSum,
    long ReasoningTokensSum);

/// <summary>Route kind constants.</summary>
public static class RouterKind
{
    public const string Alias = "alias";
    public const string Combo = "combo";

    public static bool IsValid(string? kind) => kind is Alias or Combo;
}

/// <summary>Resolved route kind constants.</summary>
public static class RouterRouteKind
{
    public const string Explicit = "explicit";
    public const string Alias = "alias";
    public const string Combo = "combo";
    public const string ModelList = "model_list";
    public const string Default = "default";
}

/// <summary>Outcome constants for ledger rows.</summary>
public static class RouterOutcome
{
    public const string Ok = "ok";
    public const string UpstreamError = "upstream_error";
    public const string ClientError = "client_error";
    public const string Canceled = "canceled";
    public const string FailedOverExhausted = "failed_over_exhausted";
}
