using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Router.Translation;

namespace AgentNotify.Core.Router;

/// <summary>Input as received from the client, with lightweight header context for auth.</summary>
public sealed record RouterInbound(
    string Wire,
    byte[] Body,
    string? AnthropicVersion = null,
    string? AnthropicBeta = null,
    bool IsCountTokens = false);

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
public sealed class RouterProxy : IDisposable
{
    private readonly RouterConfigService _configService;
    private readonly RouterRepository _repository;
    private readonly RouterTranslator _translator = new();
    private readonly IAppLogger? _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _headersTimeout;
    private readonly TimeSpan _idleTimeout;

    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new();
    private readonly object _cooldownLock = new();

    public const int MaxErrorBodyBytes = 64 * 1024;
    public const int MaxResponseBytes = 32 * 1024 * 1024;

    public RouterProxy(
        RouterConfigService configService,
        RouterRepository repository,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null,
        TimeProvider? clock = null,
        TimeSpan? headersTimeout = null,
        TimeSpan? idleTimeout = null)
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
    }

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

    private async Task<RouterProxyResult> FailAsync(IRouterClientSink sink, Exchange ex, int status, string code, string message, bool stream)
    {
        var body = _translator.WriteErrorBody(ex.Inbound.Wire, status, code, message);
        await WriteErrorToSink(sink, status, body, stream, CancellationToken.None).ConfigureAwait(false);
        string outcome = code == "all_targets_unavailable"
            ? RouterOutcome.FailedOverExhausted
            : status >= 500 ? RouterOutcome.UpstreamError
            : status >= 400 ? RouterOutcome.ClientError
            : RouterOutcome.UpstreamError;
        if (code == "all_targets_unavailable") outcome = RouterOutcome.FailedOverExhausted;
        ex.UpstreamSlug ??= null;
        return await ex.FinishAsync(status, code, outcome).ConfigureAwait(false);
    }

    public async Task<RouterProxyResult> ExecuteAsync(RouterInbound inbound, IRouterClientSink sink, CancellationToken ct)
    {
        var startedAt = _clock.GetUtcNow();
        var (requestedModel, stream, extractError) = ExtractModelAndStreamSafe(inbound.Body);
        var ex = new Exchange(this, inbound, startedAt, requestedModel, stream);

        if (extractError != null)
        {
            return await FailAsync(sink, ex, 400, extractError.Code, extractError.Message, false).ConfigureAwait(false);
        }

        RouterSnapshot snapshot;
        try
        {
            snapshot = await _configService.GetSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger?.Error("Router snapshot failed", e);
            return await FailAsync(sink, ex, 500, "upstream_error", "Upstream returned HTTP 500", false).ConfigureAwait(false);
        }

        var resolution = ResolveWithCountTokensFilter(snapshot, requestedModel, inbound.IsCountTokens);
        if (!resolution.IsSuccess)
        {
            int status = resolution.HttpStatus ?? 404;
            string code = resolution.ErrorCode ?? "unknown_model";
            return await FailAsync(sink, ex, status, code, GetResolverMessage(code, requestedModel), false).ConfigureAwait(false);
        }

        ex.RouteKind = resolution.RouteKind;
        ex.RouteName = resolution.RouteName;

        RouterRequest? decoded = null;
        bool hasTranslated = resolution.Targets.Any(t => !RouterTranslator.IsPassthrough(inbound.Wire, t.Upstream.Wire));
        if (hasTranslated)
        {
            var decodeResult = TryDecode(inbound);
            if (!decodeResult.Ok)
                return await FailAsync(sink, ex, 400, decodeResult.Error!.Code, decodeResult.Error.Message, false).ConfigureAwait(false);
            decoded = decodeResult.Request;
            if (decodeResult.DroppedNotes.Contains("previous_response_id"))
                return await FailAsync(sink, ex, 400, "unsupported_previous_response_id", "previous_response_id is not supported on this route.", false).ConfigureAwait(false);
        }

        if (IsAllCooling(resolution.Targets))
        {
            return await FailAsync(sink, ex, 503, "all_targets_unavailable", "Upstream returned HTTP 503", false).ConfigureAwait(false);
        }

        int ordinal = 0;
        foreach (var target in resolution.Targets)
        {
            var cooldownKey = CooldownKey(target.Upstream.Slug, target.NativeModel);
            if (IsCooling(cooldownKey)) continue;

            var attemptStarted = _clock.GetUtcNow();
            var attemptOrdinal = ordinal++;

            string? plaintextKey;
            try
            {
                plaintextKey = _configService.DecryptKey(target.Upstream);
            }
            catch (CryptographicException)
            {
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, null, "provider_key_unreadable", (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, false, attemptStarted));
                continue;
            }

            byte[] upstreamBody;
            try
            {
                upstreamBody = BuildUpstreamBody(inbound, target, decoded);
            }
            catch (TranslationException te)
            {
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 400, te.Code, (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, false, attemptStarted));
                return await FailAsync(sink, ex, 400, te.Code, te.Message, false).ConfigureAwait(false);
            }

            var httpReq = BuildUpstreamRequest(target, plaintextKey, upstreamBody, inbound, stream);

            HttpResponseMessage? httpResp = null;
            bool isTimeout = false;
            bool isConnectionError = false;
            string? attemptErrorCode = null;
            int? attemptStatus = null;
            try
            {
                using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headersCts.CancelAfter(_headersTimeout);
                httpResp = await _httpClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);
                attemptStatus = (int)httpResp.StatusCode;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                isTimeout = true;
                attemptErrorCode = "timeout";
            }
            catch (HttpRequestException)
            {
                isConnectionError = true;
                attemptErrorCode = "connection_error";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                isTimeout = true;
                attemptErrorCode = "timeout";
            }
            finally
            {
                httpReq.Dispose();
            }

            if (ct.IsCancellationRequested)
            {
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, attemptStatus, "canceled", (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, false, attemptStarted));
                httpResp?.Dispose();
                return await ex.FinishAsync(null, "canceled", RouterOutcome.Canceled).ConfigureAwait(false);
            }

            if (isTimeout || isConnectionError)
            {
                if (attemptErrorCode == null) attemptErrorCode = isTimeout ? "timeout" : "connection_error";
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, null, attemptErrorCode, (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, false, attemptStarted));
                SetCooldown(cooldownKey, TimeSpan.FromSeconds(15));
                httpResp?.Dispose();
                continue;
            }

            Debug.Assert(httpResp != null);
            int statusCode = (int)httpResp.StatusCode;
            attemptStatus = statusCode;
            long durationMs = (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds;

            bool isSuccess = statusCode >= 200 && statusCode < 300;
            bool isRetryable = IsRetryableStatus(statusCode);
            bool isClientError = statusCode >= 400 && statusCode < 500 && !isRetryable;
            bool isRedirect = statusCode >= 300 && statusCode < 400;

            if (isRedirect) isRetryable = false;

            if (isSuccess)
            {
                var result = await HandleSuccessAsync(ex, sink, inbound, target, decoded, httpResp, attemptOrdinal, attemptStarted, durationMs, ct);
                if (result != null) return result;
                continue;
            }

            if (isRetryable && !sink.HasStarted)
            {
                if (statusCode == 429 || statusCode == 529) attemptErrorCode = "rate_limited";
                else attemptErrorCode = "upstream_error";
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, attemptStatus, attemptErrorCode, durationMs, false, attemptStarted));
                SetCooldown(cooldownKey, GetCooldownDuration(httpResp, statusCode));
                httpResp.Dispose();
                continue;
            }

            return await HandleUpstreamErrorAsync(ex, sink, inbound, target, stream, httpResp, attemptOrdinal, attemptStarted, durationMs, isClientError, isRedirect, ct).ConfigureAwait(false);
        }

        if (ex.Attempts.Count == 0)
        {
            return await FailAsync(sink, ex, 503, "all_targets_unavailable", "Upstream returned HTTP 503", false).ConfigureAwait(false);
        }
        else
        {
            var lastAttempt = ex.Attempts.Last();
            int status = lastAttempt.Status ?? 502;
            string errorCode = lastAttempt.ErrorCode ?? "upstream_error";
            if (!sink.HasStarted)
            {
                var body = _translator.WriteErrorBody(inbound.Wire, status, errorCode, $"Upstream returned HTTP {status}");
                await WriteErrorToSink(sink, status, body, false, ct).ConfigureAwait(false);
            }
            ex.UpstreamSlug = ex.Attempts.Last().UpstreamSlug;
            ex.NativeModel = ex.Attempts.Last().Model;
            return await ex.FinishAsync(status, errorCode, RouterOutcome.FailedOverExhausted).ConfigureAwait(false);
        }
    }

    private static RouteResolution ResolveWithCountTokensFilter(RouterSnapshot snapshot, string? requestedModel, bool isCountTokens)
    {
        if (!isCountTokens) return RouteResolver.Resolve(snapshot, requestedModel);
        var baseRes = RouteResolver.Resolve(snapshot, requestedModel);
        if (!baseRes.IsSuccess) return baseRes;
        var anthropicTargets = baseRes.Targets.Where(t => t.Upstream.Wire == RouterWire.AnthropicMessages).ToList();
        if (anthropicTargets.Count == 0)
            return new RouteResolution([], null, null, "not_supported", 404);
        return new RouteResolution(anthropicTargets, baseRes.RouteKind, baseRes.RouteName, null, null);
    }

    private (bool Ok, RouterRequest? Request, IReadOnlyList<string> DroppedNotes, TranslationException? Error) TryDecode(RouterInbound inbound)
    {
        try
        {
            var dec = _translator.DecodeRequest(inbound.Wire, inbound.Body);
            return (true, dec.Request, dec.DroppedNotes, null);
        }
        catch (TranslationException ex)
        {
            return (false, null, Array.Empty<string>(), ex);
        }
    }

    private (string? model, bool stream, TranslationException? error) ExtractModelAndStreamSafe(byte[] body)
    {
        try
        {
            var (m, s) = ExtractModelAndStream(body);
            return (m, s, null);
        }
        catch (TranslationException ex)
        {
            return (null, false, ex);
        }
    }

    private byte[] BuildUpstreamBody(RouterInbound inbound, ResolvedTarget target, RouterRequest? decoded)
    {
        bool isPassthrough = RouterTranslator.IsPassthrough(inbound.Wire, target.Upstream.Wire);
        if (isPassthrough)
            return PassthroughBody.ReplaceModel(inbound.Body, target.NativeModel);
        if (decoded == null)
        {
            var dec = _translator.DecodeRequest(inbound.Wire, inbound.Body);
            decoded = dec.Request;
        }
        return _translator.EncodeRequest(target.Upstream.Wire, decoded!, target.NativeModel);
    }

    private HttpRequestMessage BuildUpstreamRequest(ResolvedTarget target, string? plaintextKey, byte[] upstreamBody, RouterInbound inbound, bool stream)
    {
        string path = inbound.IsCountTokens ? "/messages/count_tokens" : UpstreamPathFor(target.Upstream.Wire);
        var uri = target.Upstream.BaseUrl + path;
        var httpReq = new HttpRequestMessage(HttpMethod.Post, uri);
        httpReq.Content = new ByteArrayContent(upstreamBody);
        httpReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpReq.Headers.Accept.Clear();
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));

        if (target.Upstream.Wire == RouterWire.AnthropicMessages)
        {
            string version = "2023-06-01";
            bool isPassthrough = RouterTranslator.IsPassthrough(inbound.Wire, target.Upstream.Wire);
            if (isPassthrough && !string.IsNullOrEmpty(inbound.AnthropicVersion)) version = inbound.AnthropicVersion!;
            httpReq.Headers.TryAddWithoutValidation("anthropic-version", version);
            if (isPassthrough && !string.IsNullOrEmpty(inbound.AnthropicBeta))
                httpReq.Headers.TryAddWithoutValidation("anthropic-beta", inbound.AnthropicBeta!);
            if (plaintextKey != null)
                httpReq.Headers.TryAddWithoutValidation("x-api-key", plaintextKey);
        }
        else
        {
            if (plaintextKey != null)
                httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", plaintextKey);
        }
        return httpReq;
    }

    private async Task<RouterProxyResult?> HandleSuccessAsync(
        Exchange ex,
        IRouterClientSink sink,
        RouterInbound inbound,
        ResolvedTarget target,
        RouterRequest? decoded,
        HttpResponseMessage httpResp,
        int attemptOrdinal,
        DateTimeOffset attemptStarted,
        long durationMs,
        CancellationToken ct)
    {
        bool isPassthrough = RouterTranslator.IsPassthrough(inbound.Wire, target.Upstream.Wire);
        if (ex.Stream)
        {
            var headers = new Dictionary<string, string>
            {
                ["content-type"] = "text/event-stream",
                ["cache-control"] = "no-cache"
            };
            await sink.SetStatusAndHeadersAsync(200, headers, ct).ConfigureAwait(false);

            if (isPassthrough)
                return await RelayPassthroughStreamAsync(ex, sink, target, httpResp, attemptOrdinal, attemptStarted, ct).ConfigureAwait(false);
            else
                return await RelayTranslatedStreamAsync(ex, sink, inbound, target, decoded, httpResp, attemptOrdinal, attemptStarted, ct).ConfigureAwait(false);
        }
        else
        {
            return await CompleteNonStreamingAsync(ex, sink, inbound, target, decoded, httpResp, attemptOrdinal, attemptStarted, durationMs, isPassthrough, ct).ConfigureAwait(false);
        }
    }

    private async Task<RouterProxyResult> RelayPassthroughStreamAsync(
        Exchange ex,
        IRouterClientSink sink,
        ResolvedTarget target,
        HttpResponseMessage httpResp,
        int attemptOrdinal,
        DateTimeOffset attemptStarted,
        CancellationToken ct)
    {
        var tap = new UsageTap(ex.Inbound.Wire);
        try
        {
            using var respStream = await httpResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[8192];
            while (true)
            {
                int read;
                try
                {
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idleCts.CancelAfter(_idleTimeout);
                    read = await respStream.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("idle timeout");
                }
                if (read == 0) break;
                var chunk = buffer.AsMemory(0, read);
                tap.Feed(chunk.Span);
                await sink.WriteAsync(chunk, ct).ConfigureAwait(false);
                await sink.FlushAsync(ct).ConfigureAwait(false);
            }
            tap.Complete();
            ex.Usage = tap.GetUsage();
            if (ex.Usage != null) ex.UsageStatus = "reported";
            ex.UpstreamSlug = target.Upstream.Slug;
            ex.NativeModel = target.NativeModel;
            long durationMs = (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds;
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, null, durationMs, true, attemptStarted));
            var result = await ex.FinishAsync(200, null, RouterOutcome.Ok).ConfigureAwait(false);
            httpResp.Dispose();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, "canceled", (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, true, attemptStarted));
            httpResp.Dispose();
            return await ex.FinishAsync(null, "canceled", RouterOutcome.Canceled).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException || e is HttpRequestException)
        {
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, "upstream_error", (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, true, attemptStarted));
            ex.UpstreamSlug = target.Upstream.Slug;
            ex.NativeModel = target.NativeModel;
            var result = await ex.FinishAsync(200, "upstream_error", RouterOutcome.UpstreamError).ConfigureAwait(false);
            httpResp.Dispose();
            return result;
        }
    }

    private async Task<RouterProxyResult> RelayTranslatedStreamAsync(
        Exchange ex,
        IRouterClientSink sink,
        RouterInbound inbound,
        ResolvedTarget target,
        RouterRequest? decoded,
        HttpResponseMessage httpResp,
        int attemptOrdinal,
        DateTimeOffset attemptStarted,
        CancellationToken ct)
    {
        var parser = _translator.CreateStreamParser(target.Upstream.Wire);
        var writer = _translator.CreateStreamWriter(inbound.Wire, ex.RequestedModel ?? target.NativeModel, decoded);
        RouterUsage? finalUsage = null;
        try
        {
            using var respStream = await httpResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[8192];
            bool anyBytesStreamed = false;
            while (true)
            {
                int read;
                try
                {
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idleCts.CancelAfter(_idleTimeout);
                    read = await respStream.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("idle timeout");
                }
                if (read == 0) break;
                var chunk = buffer.AsMemory(0, read).Span;
                var events = parser.Feed(chunk);
                foreach (var ev in events)
                {
                    if (ev is UsageEvent ue) finalUsage = ue.Usage;
                    var outBytes = writer.Write(ev);
                    if (outBytes.Length > 0)
                    {
                        await sink.WriteAsync(outBytes, ct).ConfigureAwait(false);
                        await sink.FlushAsync(ct).ConfigureAwait(false);
                        anyBytesStreamed = true;
                    }
                }
            }
            var remaining = parser.Complete();
            foreach (var ev in remaining)
            {
                if (ev is UsageEvent ue) finalUsage = ue.Usage;
                var outBytes = writer.Write(ev);
                if (outBytes.Length > 0)
                {
                    await sink.WriteAsync(outBytes, ct).ConfigureAwait(false);
                    await sink.FlushAsync(ct).ConfigureAwait(false);
                    anyBytesStreamed = true;
                }
            }
            var completeBytes = writer.Complete();
            if (completeBytes.Length > 0)
            {
                await sink.WriteAsync(completeBytes, ct).ConfigureAwait(false);
                await sink.FlushAsync(ct).ConfigureAwait(false);
                anyBytesStreamed = true;
            }
            ex.Usage = finalUsage;
            if (finalUsage != null) ex.UsageStatus = "reported";
            ex.UpstreamSlug = target.Upstream.Slug;
            ex.NativeModel = target.NativeModel;
            long durationMs = (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds;
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, null, durationMs, anyBytesStreamed, attemptStarted));
            var result = await ex.FinishAsync(200, null, RouterOutcome.Ok).ConfigureAwait(false);
            httpResp.Dispose();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, "canceled", (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, true, attemptStarted));
            httpResp.Dispose();
            return await ex.FinishAsync(null, "canceled", RouterOutcome.Canceled).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException || e is HttpRequestException)
        {
            try
            {
                var errBytes = writer.WriteError("upstream_error", "Upstream returned HTTP 200");
                if (errBytes.Length > 0)
                {
                    await sink.WriteAsync(errBytes, ct).ConfigureAwait(false);
                    await sink.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch { }
            ex.Usage = finalUsage;
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, "upstream_error", (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, true, attemptStarted));
            ex.UpstreamSlug = target.Upstream.Slug;
            ex.NativeModel = target.NativeModel;
            var result = await ex.FinishAsync(200, "upstream_error", RouterOutcome.UpstreamError).ConfigureAwait(false);
            httpResp.Dispose();
            return result;
        }
    }

    private async Task<RouterProxyResult> CompleteNonStreamingAsync(
        Exchange ex,
        IRouterClientSink sink,
        RouterInbound inbound,
        ResolvedTarget target,
        RouterRequest? decoded,
        HttpResponseMessage httpResp,
        int attemptOrdinal,
        DateTimeOffset attemptStarted,
        long durationMs,
        bool isPassthrough,
        CancellationToken ct)
    {
        if (isPassthrough)
        {
            var tap = new UsageTap(target.Upstream.Wire);
            byte[] bodyBytes;
            try
            {
                bodyBytes = await ReadUpstreamBodyCapped(httpResp, MaxResponseBytes, tap, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, "canceled", durationMs, false, attemptStarted));
                httpResp.Dispose();
                return await ex.FinishAsync(null, "canceled", RouterOutcome.Canceled).ConfigureAwait(false);
            }
            tap.Complete();
            ex.Usage = tap.GetUsage();
            if (ex.Usage != null) ex.UsageStatus = "reported";
            var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };
            await sink.SetStatusAndHeadersAsync(200, headers, ct).ConfigureAwait(false);
            await sink.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
            await sink.FlushAsync(ct).ConfigureAwait(false);
            ex.UpstreamSlug = target.Upstream.Slug;
            ex.NativeModel = target.NativeModel;
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, null, durationMs, true, attemptStarted));
            var result = await ex.FinishAsync(200, null, RouterOutcome.Ok).ConfigureAwait(false);
            httpResp.Dispose();
            return result;
        }
        else
        {
            byte[] bodyBytes;
            try
            {
                bodyBytes = await ReadUpstreamBodyCapped(httpResp, MaxResponseBytes, null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, "canceled", durationMs, false, attemptStarted));
                httpResp.Dispose();
                return await ex.FinishAsync(null, "canceled", RouterOutcome.Canceled).ConfigureAwait(false);
            }
            RouterResponse parsed;
            try
            {
                parsed = _translator.ParseResponse(target.Upstream.Wire, bodyBytes);
            }
            catch (TranslationException)
            {
                var errBody = _translator.WriteErrorBody(inbound.Wire, 502, "upstream_error", "Upstream returned HTTP 502");
                await WriteErrorToSink(sink, 502, errBody, false, ct).ConfigureAwait(false);
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, "upstream_error", durationMs, false, attemptStarted));
                ex.UpstreamSlug = target.Upstream.Slug;
                ex.NativeModel = target.NativeModel;
                var result2 = await ex.FinishAsync(502, "upstream_error", RouterOutcome.UpstreamError).ConfigureAwait(false);
                httpResp.Dispose();
                return result2;
            }
            ex.Usage = parsed.Usage;
            if (parsed.Usage != null) ex.UsageStatus = "reported";
            var outBytes = _translator.WriteResponse(inbound.Wire, parsed, ex.RequestedModel ?? target.NativeModel, decoded);
            var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };
            await sink.SetStatusAndHeadersAsync(200, headers, ct).ConfigureAwait(false);
            await sink.WriteAsync(outBytes, ct).ConfigureAwait(false);
            await sink.FlushAsync(ct).ConfigureAwait(false);
            ex.UpstreamSlug = target.Upstream.Slug;
            ex.NativeModel = target.NativeModel;
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 200, null, durationMs, true, attemptStarted));
            var result = await ex.FinishAsync(200, null, RouterOutcome.Ok).ConfigureAwait(false);
            httpResp.Dispose();
            return result;
        }
    }

    private async Task<RouterProxyResult> HandleUpstreamErrorAsync(
        Exchange ex,
        IRouterClientSink sink,
        RouterInbound inbound,
        ResolvedTarget target,
        bool stream,
        HttpResponseMessage httpResp,
        int attemptOrdinal,
        DateTimeOffset attemptStarted,
        long durationMs,
        bool isClientError,
        bool isRedirect,
        CancellationToken ct)
    {
        int statusCode = (int)httpResp.StatusCode;
        string attemptErrorCode;
        string outcome;
        if (isClientError)
        {
            attemptErrorCode = "client_error";
            outcome = RouterOutcome.ClientError;
        }
        else if (isRedirect)
        {
            attemptErrorCode = "upstream_error";
            outcome = RouterOutcome.UpstreamError;
        }
        else
        {
            attemptErrorCode = "upstream_error";
            outcome = RouterOutcome.UpstreamError;
        }

        bool isPassthrough = RouterTranslator.IsPassthrough(inbound.Wire, target.Upstream.Wire);
        if (isPassthrough)
        {
            try
            {
                var capped = await ReadUpstreamBodyCapped(httpResp, MaxErrorBodyBytes, null, CancellationToken.None).ConfigureAwait(false);
                var headers = new Dictionary<string, string> { ["content-type"] = "application/json" };
                await sink.SetStatusAndHeadersAsync(statusCode, headers, ct).ConfigureAwait(false);
                await sink.WriteAsync(capped, ct).ConfigureAwait(false);
                await sink.FlushAsync(ct).ConfigureAwait(false);
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, statusCode, attemptErrorCode, durationMs, true, attemptStarted));
                ex.UpstreamSlug = target.Upstream.Slug;
                ex.NativeModel = target.NativeModel;
                var result = await ex.FinishAsync(statusCode, attemptErrorCode, outcome).ConfigureAwait(false);
                httpResp.Dispose();
                return result;
            }
            catch
            {
                var errBody = _translator.WriteErrorBody(inbound.Wire, statusCode, attemptErrorCode, $"Upstream returned HTTP {statusCode}");
                await WriteErrorToSink(sink, statusCode, errBody, false, ct).ConfigureAwait(false);
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, statusCode, attemptErrorCode, durationMs, false, attemptStarted));
                ex.UpstreamSlug = target.Upstream.Slug;
                ex.NativeModel = target.NativeModel;
                var result = await ex.FinishAsync(statusCode, attemptErrorCode, outcome).ConfigureAwait(false);
                httpResp.Dispose();
                return result;
            }
        }
        else
        {
            var errBody = _translator.WriteErrorBody(inbound.Wire, statusCode, attemptErrorCode, $"Upstream returned HTTP {statusCode}");
            await WriteErrorToSink(sink, statusCode, errBody, false, ct).ConfigureAwait(false);
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, statusCode, attemptErrorCode, durationMs, false, attemptStarted));
            ex.UpstreamSlug = target.Upstream.Slug;
            ex.NativeModel = target.NativeModel;
            var result = await ex.FinishAsync(statusCode, attemptErrorCode, outcome).ConfigureAwait(false);
            httpResp.Dispose();
            return result;
        }
    }

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

    private async Task WriteErrorToSink(IRouterClientSink sink, int status, byte[] body, bool stream, CancellationToken ct)
    {
        var headers = new Dictionary<string, string> { ["content-type"] = stream ? "text/event-stream" : "application/json" };
        if (stream) headers["cache-control"] = "no-cache";
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

    private static string UpstreamPathFor(string wire) => wire switch
    {
        RouterWire.OpenAiResponses => "/responses",
        RouterWire.OpenAiChat => "/chat/completions",
        RouterWire.AnthropicMessages => "/messages",
        _ => "/chat/completions"
    };

    private static bool IsRetryableStatus(int status) => status is 408 or 429 or 500 or 502 or 503 or 504 or 529;

    private static string CooldownKey(string slug, string model) => slug + "/" + model;

    private bool IsCooling(string key)
    {
        lock (_cooldownLock)
        {
            if (_cooldowns.TryGetValue(key, out var expiry))
            {
                if (_clock.GetUtcNow() < expiry) return true;
                _cooldowns.Remove(key);
            }
            return false;
        }
    }

    private void SetCooldown(string key, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;
        duration = duration > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : duration;
        var expiry = _clock.GetUtcNow().Add(duration);
        lock (_cooldownLock) _cooldowns[key] = expiry;
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
