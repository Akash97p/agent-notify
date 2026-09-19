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

/// <summary>
/// How the router authenticates to an upstream. An API key is the default and the only kind the
/// owner types in. The subscription kinds reuse a sign-in another tool on this computer already made,
/// so no key is stored for them at all; they are unofficial and opt-in.
/// </summary>
public static class RouterAuth
{
    public const string ApiKey = "api_key";

    /// <summary>A ChatGPT plan, through the sign-in Codex keeps in <c>~/.codex/auth.json</c>.</summary>
    public const string CodexChatGpt = "codex_chatgpt";

    /// <summary>A Muse Code plan, through the sign-in Muse Code keeps in <c>~/.config/muse/auth.json</c>.</summary>
    public const string MuseCode = "muse_code";

    public static bool IsValid(string? auth) => auth is ApiKey or CodexChatGpt or MuseCode;

    public static bool IsSubscription(string? auth) => auth is CodexChatGpt or MuseCode;

    public static string Normalize(string? auth)
    {
        if (string.IsNullOrWhiteSpace(auth)) return ApiKey;
        if (!IsValid(auth))
            throw new ArgumentException("Auth must be one of api_key, codex_chatgpt, muse_code.");
        return auth!;
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
    DateTimeOffset UpdatedAt)
{
    public string Auth { get; init; } = RouterAuth.ApiKey;
    public IReadOnlyDictionary<string, string> ModelWires { get; init; } = RouterUpstreamWires.None;
    public string? CredentialRef { get; init; }
}

/// <summary>
/// Where an upstream's credential lives when it is not a key stored with the upstream itself:
/// <c>api_account:&lt;id&gt;</c> is a key kept under Live quota's API accounts, and
/// <c>profile:&lt;directory&gt;</c> is the account directory whose sign-in a subscription uses (a
/// second Codex home, say). Referencing rather than copying keeps one source of truth.
/// </summary>
public static class RouterCredentialRef
{
    public const string ApiAccountPrefix = "api_account:";
    public const string ProfilePrefix = "profile:";

    public static string? ApiAccountId(string? reference) =>
        reference is not null && reference.StartsWith(ApiAccountPrefix, StringComparison.Ordinal) ? reference[ApiAccountPrefix.Length..] : null;

    public static string? ProfileDirectory(string? reference) =>
        reference is not null && reference.StartsWith(ProfilePrefix, StringComparison.Ordinal) ? reference[ProfilePrefix.Length..] : null;

    /// <summary>A valid reference, or null for none; throws for anything else.</summary>
    public static string? Normalize(string? reference, string auth)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        reference = reference.Trim();
        if (reference.Length > 1100 || reference.Any(char.IsControl))
            throw new ArgumentException("That credential reference is not valid.");
        if (ApiAccountId(reference) is { } id)
        {
            if (RouterAuth.IsSubscription(auth))
                throw new ArgumentException("A subscription signs in with its own account, not an API key.");
            if (id.Length is 0 or > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw new ArgumentException("That API account reference is not valid.");
            return reference;
        }
        if (ProfileDirectory(reference) is { } directory)
        {
            if (!RouterAuth.IsSubscription(auth))
                throw new ArgumentException("Only a subscription uses an account directory.");
            if (!Path.IsPathFullyQualified(directory))
                throw new ArgumentException("The account directory must be an absolute path.");
            return reference;
        }
        throw new ArgumentException("A credential reference is api_account:<id> or profile:<directory>.");
    }
}

/// <summary>Per-model wire overrides.</summary>
public static class RouterUpstreamWires
{
    public static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

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
    DateTimeOffset UpdatedAt)
{
    /// <summary>How the router authenticates to this upstream; see <see cref="RouterAuth"/>.</summary>
    public string Auth { get; init; } = RouterAuth.ApiKey;

    /// <summary>
    /// Models this upstream serves over a wire other than <see cref="Wire"/>. OpenCode Zen, for one,
    /// answers Claude on <c>/messages</c>, GPT on <c>/responses</c>, and the rest on
    /// <c>/chat/completions</c> under a single base URL and key.
    /// </summary>
    public IReadOnlyDictionary<string, string> ModelWires { get; init; } = RouterUpstreamWires.None;

    /// <summary>Where the credential comes from instead of <see cref="EncryptedKey"/>; see <see cref="RouterCredentialRef"/>.</summary>
    public string? CredentialRef { get; init; }

    public string WireFor(string model) =>
        ModelWires.TryGetValue(model, out var wire) && RouterWire.IsValid(wire) ? wire : Wire;
}

/// <summary>A named route with ordered targets.</summary>
public sealed record RouterRoute(
    string Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Targets,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class RouterSwitchStrategy
{
    public const string Off = "off";
    public const string Ordered = "ordered";
    public const string Sticky = "sticky";
    public const string RoundRobin = "round_robin";

    public static bool IsValid(string? strategy) =>
        strategy is Off or Ordered or Sticky or RoundRobin;

    public static string Normalize(string? strategy)
    {
        var value = string.IsNullOrWhiteSpace(strategy) ? Off : strategy.Trim().ToLowerInvariant();
        if (!IsValid(value))
            throw new ArgumentException("Switch strategy must be off, ordered, sticky, or round_robin.");
        return value;
    }
}

/// <summary>Router-level settings.</summary>
public sealed record RouterSettings(
    string? DefaultRoute,
    string SwitchStrategy = RouterSwitchStrategy.Off,
    string? ClaudeFallbackRoute = null)
{
    /// <summary>Whether any switching strategy is on; the strategy itself says which.</summary>
    public bool SmartRouting => SwitchStrategy != RouterSwitchStrategy.Off;
}

public sealed record RouterEffortMapping(
    string UpstreamId,
    string Model,
    IReadOnlyList<string> SupportedValues,
    IReadOnlyList<string> LevelMap,
    string? DefaultValue = null);

public sealed record RouterEffortCapability(
    string UpstreamId,
    string UpstreamSlug,
    string Model,
    string Wire,
    string Family,
    string Source,
    IReadOnlyList<string> SupportedValues,
    IReadOnlyList<string> LevelMap,
    string? DefaultValue);

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

    /// <summary>An Anthropic model the agent asked for itself, sent to Anthropic with its own sign-in.</summary>
    public const string Native = "native";
}

/// <summary>
/// Claude Code's own models, which the router hands to Anthropic unchanged, authenticated with the
/// credential Claude Code itself sent. Claude Code has one base URL for every model, so once it points
/// at the router its built-in Opus, Sonnet, and Haiku arrive here too; without this they would fall
/// through to the default route and run on some other provider under Claude's name.
/// </summary>
/// <remarks>
/// This only applies when the client authenticated to the router with <see cref="RouterKeyHeader"/>,
/// which leaves its <c>Authorization</c> or <c>x-api-key</c> free to carry its own Anthropic
/// credential. That credential is forwarded to Anthropic and nowhere else, never stored or logged.
/// </remarks>
public static class RouterNative
{
    /// <summary>The header a client sends the router key in when its usual auth headers are its own.</summary>
    public const string RouterKeyHeader = "x-agentnotify-router-key";

    /// <summary>The prefix of every Anthropic model ID; anything else is never sent to Anthropic natively.</summary>
    public const string ModelPrefix = "claude-";

    public static bool IsNativeModel(string? model) =>
        model is not null && model.StartsWith(ModelPrefix, StringComparison.OrdinalIgnoreCase) && !model.Contains('/');

    /// <summary>The destination: Anthropic's own API, on the Messages wire.</summary>
    public static readonly StoredRouterUpstream Upstream = new(
        "native-anthropic", "anthropic", "Anthropic (the agent's own sign-in)", RouterWire.AnthropicMessages,
        "https://api.anthropic.com/v1", null, [], true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    public static bool IsNative(StoredRouterUpstream upstream) => ReferenceEquals(upstream, Upstream);
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

/// <summary>A Codex account a ChatGPT-plan provider can sign in with.</summary>
public sealed record CodexPlanAccount(string Directory, string Label, bool IsDefault, bool SignedIn);
