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

        var nativeAnthropic = inbound.ClientCredential is not null && inbound.Wire == RouterWire.AnthropicMessages;
        var resolution = ResolveWithCountTokensFilter(snapshot, requestedModel, inbound.IsCountTokens, nativeAnthropic);
        if (!resolution.IsSuccess)
        {
            int status = resolution.HttpStatus ?? 404;
            string code = resolution.ErrorCode ?? "unknown_model";
            return await FailAsync(sink, ex, status, code, GetResolverMessage(code, requestedModel), false).ConfigureAwait(false);
        }

        ex.RouteKind = resolution.RouteKind;
        ex.RouteName = resolution.RouteName;
        var strategyKey = StrategyKey(resolution, requestedModel);
        resolution = resolution with { Targets = OrderTargets(resolution.Targets, snapshot.Settings.SwitchStrategy, strategyKey) };

        RouterRequest? decoded = null;
        bool hasTranslated = !RouterNative.IsNative(resolution.Targets[0].Upstream) &&
            resolution.Targets.Any(t => !RouterTranslator.IsPassthrough(inbound.Wire, t.Upstream.Wire));
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
            // Every target is waiting out a failure. Say which, and for how long, so the agent backs
            // off for that long instead of retrying into the same wall.
            var (coolingStatus, retryAfter) = Cooling(resolution.Targets);
            return coolingStatus is 429 or 529
                ? await FailAsync(sink, ex, 429, "rate_limited", $"Every target of this model is rate limited; try again in {retryAfter}s.", false, retryAfter).ConfigureAwait(false)
                : await FailAsync(sink, ex, 503, "all_targets_unavailable", $"Every target of this model failed recently; try again in {retryAfter}s.", false, retryAfter).ConfigureAwait(false);
        }

        // Anthropic's own answer — its 401 to renew a sign-in, its 429 and retry-after — goes back to
        // Claude Code untouched unless the owner configured somewhere for the request to go instead.
        // Only then may a native failure cool down and hand the turn to a routed target.
        bool nativeMayFailOver = resolution.Targets.Count > 1;

        int ordinal = 0;
        foreach (var target in resolution.Targets)
        {
            var cooldownKey = CooldownKey(target.Upstream.Slug, target.NativeModel);
            if (IsCooling(cooldownKey)) continue;

            var attemptStarted = _clock.GetUtcNow();
            var attemptOrdinal = ordinal++;

            UpstreamCredential credential;
            try
            {
                credential = RouterNative.IsNative(target.Upstream)
                    ? NativeCredential(inbound)
                    : await _credentials.GetAsync(target.Upstream, renew: false, ct).ConfigureAwait(false);
            }
            catch (CryptographicException)
            {
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, null, "provider_key_unreadable", (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, false, attemptStarted));
                continue;
            }
            catch (SubscriptionAuthException sae)
            {
                _logger?.Warn($"Router upstream '{target.Upstream.Slug}': {sae.Message}");
                // A subscription whose sign-in is gone, or an API account that was removed.
                var code = RouterAuth.IsSubscription(target.Upstream.Auth) ? "subscription_signed_out" : "provider_key_unreadable";
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, null, code, (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, false, attemptStarted));
                continue;
            }

            byte[] upstreamBody;
            try
            {
                upstreamBody = BuildUpstreamBody(inbound, target, decoded, snapshot.EffortMappings ?? [], snapshot.EffortFamilyOverrides ?? []);
            }
            catch (TranslationException te)
            {
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 400, te.Code, (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, false, attemptStarted));
                return await FailAsync(sink, ex, 400, te.Code, te.Message, false).ConfigureAwait(false);
            }

            if (target.Upstream.Auth == RouterAuth.CodexChatGpt)
                upstreamBody = ChatGptBackendBody.Adapt(upstreamBody);

            var httpReq = BuildUpstreamRequest(target, credential, upstreamBody, inbound, stream);

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
                // A subscription sign-in the provider no longer accepts is renewed once and the same
                // body sent again; nothing has reached the client yet.
                if (httpResp.StatusCode == HttpStatusCode.Unauthorized && RouterAuth.IsSubscription(target.Upstream.Auth))
                {
                    httpResp.Dispose();
                    httpResp = null;
                    try
                    {
                        var renewed = await _credentials.GetAsync(target.Upstream, renew: true, ct).ConfigureAwait(false);
                        httpReq.Dispose();
                        httpReq = BuildUpstreamRequest(target, renewed, upstreamBody, inbound, stream);
                        httpResp = await _httpClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);
                    }
                    catch (SubscriptionAuthException sae)
                    {
                        _logger?.Warn($"Router upstream '{target.Upstream.Slug}': {sae.Message}");
                        attemptErrorCode = "subscription_signed_out";
                    }
                }
                attemptStatus = httpResp is null ? null : (int)httpResp.StatusCode;
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

            if (httpResp is null && attemptErrorCode == "subscription_signed_out")
            {
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, 401, attemptErrorCode, (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, false, attemptStarted));
                continue;
            }

            if (isTimeout || isConnectionError)
            {
                if (attemptErrorCode == null) attemptErrorCode = isTimeout ? "timeout" : "connection_error";
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, null, attemptErrorCode, (long)(_clock.GetUtcNow() - attemptStarted).TotalMilliseconds, false, attemptStarted));
                if (!RouterNative.IsNative(target.Upstream) || nativeMayFailOver)
                    SetCooldown(cooldownKey, TimeSpan.FromSeconds(15), 503);
                ForgetStickyTarget(snapshot.Settings.SwitchStrategy, strategyKey, target);
                ex.ProviderCode = null; // the last failure said nothing of its own
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
                if (result is not null)
                {
                    if (result.Outcome == RouterOutcome.Ok)
                        RememberSuccessfulTarget(snapshot.Settings.SwitchStrategy, strategyKey, target);
                    return result;
                }
                continue;
            }

            if (isRetryable && !sink.HasStarted && (!RouterNative.IsNative(target.Upstream) || nativeMayFailOver))
            {
                attemptErrorCode = statusCode switch
                {
                    429 or 529 => "rate_limited",
                    402 => "payment_required",
                    401 or 403 => "upstream_unauthorized",
                    _ => "upstream_error"
                };
                ex.ProviderCode = await ReadProviderCodeAsync(httpResp, target, statusCode).ConfigureAwait(false);
                ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, attemptStatus, attemptErrorCode, durationMs, false, attemptStarted));
                SetCooldown(cooldownKey, GetCooldownDuration(httpResp, statusCode), statusCode);
                ForgetStickyTarget(snapshot.Settings.SwitchStrategy, strategyKey, target);
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
                var body = _translator.WriteErrorBody(inbound.Wire, status, errorCode, UpstreamMessage(status, ex.ProviderCode));
                await WriteErrorToSink(sink, status, body, false, ct).ConfigureAwait(false);
            }
            ex.UpstreamSlug = ex.Attempts.Last().UpstreamSlug;
            ex.NativeModel = ex.Attempts.Last().Model;
            return await ex.FinishAsync(status, errorCode, RouterOutcome.FailedOverExhausted).ConfigureAwait(false);
        }
    }

    private static RouteResolution ResolveWithCountTokensFilter(RouterSnapshot snapshot, string? requestedModel, bool isCountTokens, bool nativeAnthropic)
    {
        if (!isCountTokens) return RouteResolver.Resolve(snapshot, requestedModel, nativeAnthropic);
        var baseRes = RouteResolver.Resolve(snapshot, requestedModel, nativeAnthropic);
        if (!baseRes.IsSuccess) return baseRes;
        if (baseRes.RouteKind == RouterRouteKind.Native)
            return new RouteResolution([baseRes.Targets[0]], baseRes.RouteKind, baseRes.RouteName, null, null);
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

    private byte[] BuildUpstreamBody(RouterInbound inbound, ResolvedTarget target, RouterRequest? decoded,
        IReadOnlyList<RouterEffortMapping> effortMappings, IReadOnlyList<RouterEffortFamilyOverride> effortFamilyOverrides)
    {
        if (RouterNative.IsNative(target.Upstream)) return inbound.Body;
        bool isPassthrough = RouterTranslator.IsPassthrough(inbound.Wire, target.Upstream.Wire);
        var capability = RouterEffortCatalog.Resolve(target.Upstream, target.NativeModel, effortMappings, effortFamilyOverrides);
        if (isPassthrough)
        {
            // A same-wire hop is forwarded as it came. Effort applies to every wire now, so the body is
            // read once for the requested value; when mapping leaves it unchanged the bytes are not
            // rewritten at all.
            var source = RequestEffort(inbound.Wire, inbound.Body);
            var effort = RouterEffortCatalog.ForRequest(capability, source);
            if (effort == source)
                return PassthroughBody.ReplaceModel(inbound.Body, target.NativeModel);
            return PassthroughBody.ReplaceModel(inbound.Body, target.NativeModel, effort, inbound.Wire);
        }
        if (decoded == null)
        {
            var dec = _translator.DecodeRequest(inbound.Wire, inbound.Body);
            decoded = dec.Request;
        }
        return _translator.EncodeRequest(target.Upstream.Wire,
            RouterEffortCatalog.Apply(decoded!, capability), target.NativeModel);
    }

    private static HttpRequestMessage BuildUpstreamRequest(ResolvedTarget target, UpstreamCredential credential, byte[] upstreamBody, RouterInbound inbound, bool stream)
    {
        var plaintextKey = credential.Secret;
        var native = RouterNative.IsNative(target.Upstream);
        string path = inbound.IsCountTokens ? "/messages/count_tokens" : UpstreamPathFor(target.Upstream.Wire);
        var uri = target.Upstream.BaseUrl + path + (native ? inbound.Query ?? "" : "");
        var httpReq = new HttpRequestMessage(HttpMethod.Post, uri);
        httpReq.Content = new ByteArrayContent(upstreamBody);
        httpReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpReq.Headers.Accept.Clear();
        var streamsAnyway = target.Upstream.Auth == RouterAuth.CodexChatGpt;
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream || streamsAnyway ? "text/event-stream" : "application/json"));

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
        foreach (var (name, value) in credential.Headers)
            httpReq.Headers.TryAddWithoutValidation(name, value);
        if (native)
        {
            // The agent's own request, as it would have reached Anthropic without the router.
            foreach (var (name, value) in inbound.NativeHeaders)
            {
                httpReq.Headers.Remove(name);
                httpReq.Headers.TryAddWithoutValidation(name, value);
            }
            return httpReq;
        }
        // Providers that serve coding agents ask each client to name itself rather than send a
        // generic HTTP-library agent.
        httpReq.Headers.UserAgent.ParseAdd(RouterUserAgent);
        if (IsOpenCodeHost(target.Upstream.BaseUrl))
            httpReq.Headers.TryAddWithoutValidation("x-opencode-session", inbound.SessionId ?? ProcessSessionId);
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

}
