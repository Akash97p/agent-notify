using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Logging;
using AgentNotify.Router.Translation;

namespace AgentNotify.Router;

/// <summary>Input as received from the client, with lightweight header context for auth.</summary>
public sealed record RouterInbound(
    string Wire,
    byte[] Body,
    string? AnthropicVersion = null,
    string? AnthropicBeta = null,
    bool IsCountTokens = false)
{
    /// <summary>
    /// Headers agents carry their conversation ID in, in order of preference: an explicit OpenCode
    /// session, Claude Code's, then Codex's.
    /// </summary>
    public static readonly string[] SessionHeaders =
        ["x-opencode-session", "x-claude-code-session-id", "session_id", "conversation_id"];

    /// <summary>The agent's conversation ID, when it sent one.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The client's own Anthropic credential (<c>Authorization</c> or <c>x-api-key</c>, name and value),
    /// present only when it authenticated to the router with <see cref="RouterNative.RouterKeyHeader"/>.
    /// It is forwarded to Anthropic for the client's own models and to nothing else.
    /// </summary>
    public KeyValuePair<string, string>? ClientCredential { get; init; }

    /// <summary>The client's own request headers that a native Anthropic request carries unchanged.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> NativeHeaders { get; init; } = [];

    /// <summary>The inbound query string (<c>?beta=true</c>), kept for a native Anthropic request.</summary>
    public string? Query { get; init; }

    /// <summary>
    /// Whether a request header is one Claude Code sends Anthropic that a native request keeps: the API
    /// version and betas, and how the client identifies itself. Credentials travel separately.
    /// </summary>
    public static bool IsNativeHeader(string name) =>
        name.StartsWith("anthropic-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("x-stainless-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("user-agent", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-app", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("x-claude-code-session-id", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Sink that writes the router response back to the client.</summary>
public interface IRouterClientSink
{
    bool HasStarted { get; }
    Task SetStatusAndHeadersAsync(int statusCode, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
}

/// <summary>Result of a router request for ledger and testing.</summary>
public sealed record RouterProxyResult(
    int Status,
    string? ErrorCode,
    string? Outcome,
    IReadOnlyList<RouterAttemptRecord> Attempts,
    RouterRequestRecord? RequestRecord);

/// <summary>Proxies Requests through configured upstreams with translation, failover and ledger recording.</summary>
public sealed partial class RouterProxy : IDisposable
{
    private readonly RouterConfigService _configService;
    private readonly RouterRepository _repository;
    private readonly RouterTranslator _translator = new();
    private readonly IAppLogger? _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly RouterCredentialSource _credentials;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _headersTimeout;
    private readonly TimeSpan _idleTimeout;

    /// <summary>Per target: until when it is skipped, and the status that started the cooldown.</summary>
    private readonly Dictionary<string, (DateTimeOffset Until, int Status)> _cooldowns = new();
    private readonly object _cooldownLock = new();
    private readonly Dictionary<string, string> _stickyTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _roundRobinOffsets = new(StringComparer.Ordinal);
    private readonly object _strategyLock = new();

    public const int MaxErrorBodyBytes = 64 * 1024;
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    public RouterProxy(
        RouterConfigService configService,
        RouterRepository repository,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null,
        TimeProvider? clock = null,
        TimeSpan? headersTimeout = null,
        TimeSpan? idleTimeout = null,
        Func<HttpClient, RouterCredentialSource>? credentials = null)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _headersTimeout = headersTimeout ?? TimeSpan.FromSeconds(300);
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(300);

        if (handler != null)
        {
            _httpClient = new HttpClient(handler, disposeHandler: false);
            _ownsClient = false;
        }
        else
        {
            var sockets = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(30),
                AutomaticDecompression = DecompressionMethods.All
            };
            _httpClient = new HttpClient(sockets, disposeHandler: true);
            _ownsClient = true;
        }

        _credentials = credentials?.Invoke(_httpClient) ?? new RouterCredentialSource(_configService, _httpClient, _clock);
        Discovery = new RouterModelDiscovery(_httpClient, _credentials);
    }

    /// <summary>Where each attempt's credential comes from, including subscription sign-ins.</summary>
    public RouterCredentialSource Credentials => _credentials;

    /// <summary>Lists a provider's models through the same client and destination rule as routed traffic.</summary>
    public RouterModelDiscovery Discovery { get; }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }

    private sealed record ModelEntry(string id, string @object, string owned_by);

    public async Task<byte[]> ListModelsAsync(CancellationToken ct = default)
    {
        var snapshot = await _configService.GetSnapshotAsync(ct).ConfigureAwait(false);
        var list = new List<ModelEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var up in snapshot.Upstreams.Where(u => u.Enabled))
        {
            foreach (var model in up.Models)
            {
                var id = up.Slug + "/" + model;
                if (seen.Add(id))
                    list.Add(new ModelEntry(id, "model", up.Slug));
            }
        }
        foreach (var route in snapshot.Routes.Where(r => r.Enabled))
        {
            var id = route.Name;
            if (seen.Add(id))
                list.Add(new ModelEntry(id, "model", "agentnotify"));
            if (route.Kind == RouterKind.Combo)
            {
                var combo = "combo/" + route.Name;
                if (seen.Add(combo))
                    list.Add(new ModelEntry(combo, "model", "agentnotify"));
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
        var payload = new { @object = "list", data = list };
        return JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }

    private sealed class Exchange
    {
        public readonly string RequestId = "rtr_" + Guid.NewGuid().ToString("N");
        public readonly DateTimeOffset StartedAt;
        public readonly RouterInbound Inbound;
        public string? RequestedModel;
        public bool Stream;
        public string? RouteKind;
        public string? RouteName;
        public string? UpstreamSlug;
        public string? NativeModel;
        public RouterUsage? Usage;
        public string UsageStatus = "unreported";

        /// <summary>The last upstream's own short error code (<c>usage_limit_reached</c>), for the message.</summary>
        public string? ProviderCode;
        public readonly List<RouterAttemptRecord> Attempts = new();

        private readonly RouterProxy _owner;

        public Exchange(RouterProxy owner, RouterInbound inbound, DateTimeOffset startedAt, string? requestedModel, bool stream)
        {
            _owner = owner;
            Inbound = inbound;
            StartedAt = startedAt;
            RequestedModel = requestedModel;
            Stream = stream;
        }

        public async Task<RouterProxyResult> FinishAsync(int? status, string? errorCode, string? outcome)
        {
            var record = new RouterRequestRecord(
                RequestId, StartedAt, _owner._clock.GetUtcNow(), Inbound.Wire, RequestedModel,
                RouteKind, RouteName, UpstreamSlug, NativeModel, Stream, status, outcome,
                Usage?.InputTokens, Usage?.CachedInputTokens, Usage?.OutputTokens, Usage?.ReasoningTokens,
                UsageStatus, errorCode);
            await _owner.InsertLedgerAsync(record, Attempts).ConfigureAwait(false);
            int resultStatus = status ?? 499;
            return new RouterProxyResult(resultStatus, errorCode, outcome, Attempts, record);
        }
    }

    private async Task<RouterProxyResult> FailAsync(IRouterClientSink sink, Exchange ex, int status, string code, string message, bool stream, int? retryAfterSeconds = null)
    {
        var body = _translator.WriteErrorBody(ex.Inbound.Wire, status, code, message);
        await WriteErrorToSink(sink, status, body, stream, CancellationToken.None, retryAfterSeconds).ConfigureAwait(false);
        string outcome = code == "all_targets_unavailable"
            ? RouterOutcome.FailedOverExhausted
            : status >= 500 ? RouterOutcome.UpstreamError
            : status >= 400 ? RouterOutcome.ClientError
            : RouterOutcome.UpstreamError;
        if (code == "all_targets_unavailable") outcome = RouterOutcome.FailedOverExhausted;
        ex.UpstreamSlug ??= null;
        return await ex.FinishAsync(status, code, outcome).ConfigureAwait(false);
    }

}
