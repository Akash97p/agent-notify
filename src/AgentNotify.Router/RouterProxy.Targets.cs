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

public sealed partial class RouterProxy : IDisposable
{
    private static string StrategyKey(RouteResolution resolution, string? requestedModel) =>
        (resolution.RouteName ?? requestedModel ?? "default") + "|" +
        string.Join(",", resolution.Targets.Where(target => !RouterNative.IsNative(target.Upstream)).Select(TargetKey));

    private static string TargetKey(ResolvedTarget target) => target.Upstream.Slug + "/" + target.NativeModel;

    private bool IsAllCooling(IReadOnlyList<ResolvedTarget> targets)
    {
        if (targets.Count == 0) return false;
        foreach (var t in targets)
        {
            if (!IsCooling(CooldownKey(t.Upstream.Slug, t.NativeModel)))
                return false;
        }
        return true;
    }

    private async Task WriteErrorToSink(IRouterClientSink sink, int status, byte[] body, bool stream, CancellationToken ct, int? retryAfterSeconds = null)
    {
        var headers = new Dictionary<string, string> { ["content-type"] = stream ? "text/event-stream" : "application/json" };
        if (stream) headers["cache-control"] = "no-cache";
        if (retryAfterSeconds is > 0) headers["retry-after"] = retryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try { await sink.SetStatusAndHeadersAsync(status, headers, ct).ConfigureAwait(false); } catch { }
        try
        {
            await sink.WriteAsync(body, ct).ConfigureAwait(false);
            await sink.FlushAsync(ct).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task<byte[]> ReadUpstreamBodyCapped(HttpResponseMessage resp, int cap, UsageTap? tap, CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var ms = new MemoryStream();
        var buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read;
            try
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(_idleTimeout);
                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException("idle timeout");
            }
            if (read == 0) break;
            if (total + read > cap)
            {
                var allowed = cap - total;
                if (allowed > 0)
                {
                    ms.Write(buffer, 0, allowed);
                    tap?.Feed(buffer.AsSpan(0, allowed));
                    total += allowed;
                }
                break;
            }
            ms.Write(buffer, 0, read);
            tap?.Feed(buffer.AsSpan(0, read));
            total += read;
        }
        return ms.ToArray();
    }

    private static string? RequestEffort(string wire, byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (wire == RouterWire.AnthropicMessages &&
                root.TryGetProperty("output_config", out var output) && output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("effort", out var anthropic) && anthropic.ValueKind == JsonValueKind.String)
                return anthropic.GetString();
            if (wire == RouterWire.OpenAiResponses &&
                root.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object &&
                reasoning.TryGetProperty("effort", out var responses) && responses.ValueKind == JsonValueKind.String)
                return responses.GetString();
            if (wire == RouterWire.OpenAiChat &&
                root.TryGetProperty("reasoning_effort", out var chat) && chat.ValueKind == JsonValueKind.String)
                return chat.GetString();
            return null;
        }
        catch (JsonException ex)
        {
            throw new TranslationException("invalid_request", "Invalid JSON.", ex);
        }
    }

    private static (string? model, bool stream) ExtractModelAndStream(byte[] body)
    {
        if (body == null || body.Length == 0) return (null, false);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? model = null;
            bool stream = false;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model = m.GetString();
                if (root.TryGetProperty("stream", out var s))
                {
                    if (s.ValueKind == JsonValueKind.True) stream = true;
                    else if (s.ValueKind == JsonValueKind.False) stream = false;
                }
            }
            return (model, stream);
        }
        catch (JsonException ex)
        {
            throw new TranslationException("invalid_request", "Invalid JSON.", ex);
        }
    }

    private static string GetResolverMessage(string code, string? model) => code switch
    {
        "unknown_model" => $"No route matches the model '{model ?? ""}'.",
        "ambiguous_model" => $"More than one upstream declares the model '{model ?? ""}'. Ask for it as provider/model.",
        "missing_model" => "This request names no model and the router has no default route.",
        "no_enabled_target" => "Every target of this route belongs to a disabled upstream.",
        "not_supported" => "Counting tokens needs an Anthropic upstream; this model does not resolve to one.",
        _ => code
    };

    /// <summary>The credential a native Anthropic request carries: the one the agent sent.</summary>
    private static UpstreamCredential NativeCredential(RouterInbound inbound) =>
        inbound.ClientCredential is { } credential
            ? new UpstreamCredential(null, new Dictionary<string, string> { [credential.Key] = credential.Value })
            : UpstreamCredential.None;

    private static readonly string RouterUserAgent =
        "agentnotify-router/" + (typeof(RouterProxy).Assembly.GetName().Version?.ToString(3) ?? "0");

    /// <summary>Used when an agent sends no conversation ID: stable for this broker's lifetime.</summary>
    private static readonly string ProcessSessionId = "agentnotify-" + Guid.NewGuid().ToString("N");

    private static bool IsOpenCodeHost(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        (uri.Host == "opencode.ai" || uri.Host.EndsWith(".opencode.ai", StringComparison.OrdinalIgnoreCase));

    private static string UpstreamPathFor(string wire) => wire switch
    {
        RouterWire.OpenAiResponses => "/responses",
        RouterWire.OpenAiChat => "/chat/completions",
        RouterWire.AnthropicMessages => "/messages",
        _ => "/chat/completions"
    };

    // Each of these is about the target, not the request, so another target can still serve it: 401 and
    // 403 a key or sign-in the provider refuses, 402 an account out of credit, 404 a model the provider
    // lists but does not serve, and the usual limits and outages.
    private static bool IsRetryableStatus(int status) =>
        status is 401 or 402 or 403 or 404 or 408 or 429 or 500 or 502 or 503 or 504 or 529;

    private static string UpstreamMessage(int status, string? providerCode) =>
        providerCode is null ? $"Upstream returned HTTP {status}" : $"Upstream returned HTTP {status} ({providerCode})";

    /// <summary>
    /// The provider's own short error code from an error body (<c>usage_limit_reached</c>,
    /// <c>insufficient_quota</c>), which says why far better than the status alone. Only an identifier
    /// is taken, never the provider's message text, which can quote the request back. It is logged
    /// with the upstream and model so a failure can be explained after the fact.
    /// </summary>
    private async Task<string?> ReadProviderCodeAsync(HttpResponseMessage response, ResolvedTarget target, int status)
    {
        string? code = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var body = await ReadUpstreamBodyCapped(response, 16 * 1024, null, cts.Token).ConfigureAwait(false);
            code = ProviderErrorCode(body);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or OperationCanceledException) { }
        _logger?.Warn($"Router upstream '{target.Upstream.Slug}' answered HTTP {status} for '{target.NativeModel}'" +
            (code is null ? "." : $" ({code})."));
        return code;
    }

    internal static string? ProviderErrorCode(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // OpenAI and Anthropic nest it under "error"; the ChatGPT backend puts it under "error" or "detail".
            foreach (var container in new[] { "error", "detail" })
            {
                if (!root.TryGetProperty(container, out var inner) || inner.ValueKind != JsonValueKind.Object) continue;
                if ((SafeCode(inner, "code") ?? SafeCode(inner, "type")) is { } nested) return nested;
            }
            return SafeCode(root, "code") ?? SafeCode(root, "type");
        }
        catch (JsonException) { return null; }
    }

    private static string? SafeCode(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is { Length: > 0 and <= 64 } &&
               text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') &&
               text != "error"
            ? text
            : null;
    }

    private static string CooldownKey(string slug, string model) => slug + "/" + model;

    private bool IsCooling(string key)
    {
        lock (_cooldownLock)
        {
            if (_cooldowns.TryGetValue(key, out var cooldown))
            {
                if (_clock.GetUtcNow() < cooldown.Until) return true;
                _cooldowns.Remove(key);
            }
            return false;
        }
    }

    private void SetCooldown(string key, TimeSpan duration, int status)
    {
        if (duration <= TimeSpan.Zero) return;
        duration = duration > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : duration;
        var expiry = _clock.GetUtcNow().Add(duration);
        lock (_cooldownLock) _cooldowns[key] = (expiry, status);
    }

    /// <summary>
    /// For targets that are all cooling down: the status that started the soonest-ending cooldown,
    /// and the seconds until it ends.
    /// </summary>
    private (int Status, int RetryAfterSeconds) Cooling(IReadOnlyList<ResolvedTarget> targets)
    {
        var now = _clock.GetUtcNow();
        lock (_cooldownLock)
        {
            var soonest = targets
                .Select(t => _cooldowns.TryGetValue(CooldownKey(t.Upstream.Slug, t.NativeModel), out var c) ? c : ((DateTimeOffset, int)?)null)
                .Where(c => c is not null)
                .Select(c => c!.Value)
                .OrderBy(c => c.Item1)
                .FirstOrDefault();
            var seconds = soonest.Item1 > now ? (int)Math.Ceiling((soonest.Item1 - now).TotalSeconds) : 1;
            return (soonest.Item2 == 0 ? 503 : soonest.Item2, Math.Max(1, seconds));
        }
    }

    private TimeSpan GetCooldownDuration(HttpResponseMessage resp, int status)
    {
        if (resp.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (int.TryParse(raw.Trim(), out var secs))
                {
                    var ts = TimeSpan.FromSeconds(secs);
                    return ts > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : ts;
                }
                if (DateTimeOffset.TryParse(raw, out var date))
                {
                    var diff = date - _clock.GetUtcNow();
                    if (diff > TimeSpan.Zero)
                        return diff > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : diff;
                }
            }
        }
        if (status == 429 || status == 529) return TimeSpan.FromSeconds(30);
        // Credit does not come back in seconds; stop asking for a while.
        if (status == 402) return TimeSpan.FromMinutes(5);
        if (status is 401 or 403 or 404) return TimeSpan.FromSeconds(60);
        return TimeSpan.FromSeconds(15);
    }

    private async Task InsertLedgerAsync(RouterRequestRecord record, List<RouterAttemptRecord> attempts)
    {
        try
        {
            await _repository.InsertRequestAsync(record, attempts, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Router ledger insert failed: {ex.Message}");
        }
    }
}

/// <summary>Periodically prunes the router ledger according to retention.</summary>
public sealed class RouterLedgerPruner : IDisposable
{
    private readonly RouterRepository _repository;
    private readonly AgentNotifyConfig _config;
    private readonly TimeProvider _clock;
    private readonly IAppLogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    public RouterLedgerPruner(RouterRepository repository, AgentNotifyConfig config, TimeProvider? clock = null, IAppLogger? logger = null)
    {
        _repository = repository;
        _config = config;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public void Start()
    {
        _loopTask = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        try
        {
            await PruneOnceAsync().ConfigureAwait(false);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromHours(24), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                await PruneOnceAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) { _logger?.Error("Router pruner loop failed", ex); }
    }

    private async Task PruneOnceAsync()
    {
        try
        {
            var days = Math.Clamp(_config.RouterLedgerRetentionDays, 1, 365);
            var cutoff = _clock.GetUtcNow().AddDays(-days);
            await _repository.PruneLedgerAsync(cutoff, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger?.Warn($"Router ledger prune failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
