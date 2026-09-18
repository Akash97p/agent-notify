namespace AgentNotify.Core.Router.Connect;

/// <summary>
/// An agent whose own configuration can be pointed at the router. <c>ConfigPath</c> is the file the
/// connector edits, <c>Detected</c> means the agent looks installed, <c>Connected</c> means that file
/// currently carries AgentNotify's managed lines, <c>ModelSlots</c> holds the selectors mapped onto a
/// host's fixed picker entries, and <c>Blocked</c> explains why connecting is impossible right now.
/// </summary>
public sealed record RouterAgentInfo(
    string Id,
    string DisplayName,
    string ConfigPath,
    bool Detected,
    bool Connected,
    DateTimeOffset? ConnectedAt,
    string? SelectedModel,
    IReadOnlyDictionary<string, string> ModelSlots,
    string? CatalogPath,
    int CatalogModelCount,
    IReadOnlyList<RouterAgentBackup> Backups,
    string? Blocked)
{
    /// <summary>The host's other model settings and their current values.</summary>
    public IReadOnlyList<RouterAgentOption> Options { get; init; } = [];
    public IReadOnlyDictionary<string, string> OptionValues { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>A copy of an agent's configuration file taken before AgentNotify changed it.</summary>
public sealed record RouterAgentBackup(string Id, string Path, DateTimeOffset CreatedAt, string Reason);

/// <summary>
/// What the owner chose when connecting an agent: the selector it should start on (a route name,
/// <c>combo/&lt;name&gt;</c>, or <c>slug/model</c>); for a host whose picker has fixed entries, what
/// each of those entries should point at; and the host's other model settings, such as the model its
/// subagents use. Anything omitted keeps the host's own value.
/// </summary>
public sealed record RouterConnectRequest(
    string? Model = null,
    IReadOnlyDictionary<string, string>? ModelSlots = null,
    IReadOnlyDictionary<string, string>? Options = null);

/// <summary>
/// One setting a connector can write beyond the main model: which key it maps to in the host's own
/// configuration, whether its value is a router selector (rather than free text), and the values the
/// interface should offer.
/// </summary>
public sealed record RouterAgentOption(
    string Id,
    string DisplayName,
    string Description,
    bool IsModelSelector,
    IReadOnlyList<string> Choices);

/// <summary>The outcome of connecting, disconnecting, or restoring one agent.</summary>
public sealed record RouterConnectResult(RouterAgentInfo Agent, string Message);

/// <summary>Persisted per-agent state: what AgentNotify changed, and what it looked like before.</summary>
public sealed record RouterAgentState
{
    public string Id { get; init; } = "";
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? SelectedModel { get; init; }
    public Dictionary<string, string> ModelSlots { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Options { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The values the agent's own configuration held before the first connect, so disconnecting puts
    /// them back rather than merely deleting AgentNotify's lines. A key stored with a null value was
    /// absent before and is removed again on disconnect.
    /// </summary>
    public Dictionary<string, string?> Previous { get; init; } = new(StringComparer.Ordinal);

    public List<RouterAgentBackup> Backups { get; init; } = [];
}
