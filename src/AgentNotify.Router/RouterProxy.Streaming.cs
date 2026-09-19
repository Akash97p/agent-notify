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
        // A backend that only streams: its stream is collected into the one response the client asked for.
        if (target.Upstream.Auth == RouterAuth.CodexChatGpt)
        {
            decoded ??= TryDecode(inbound).Request;
            isPassthrough = false;
        }
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
                parsed = target.Upstream.Auth == RouterAuth.CodexChatGpt
                    ? CollectStream(target.Upstream.Wire, bodyBytes)
                    : _translator.ParseResponse(target.Upstream.Wire, bodyBytes);
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
                if (httpResp.Headers.RetryAfter?.ToString() is { Length: > 0 } retryAfter) headers["retry-after"] = retryAfter;
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
            var providerCode = await ReadProviderCodeAsync(httpResp, target, statusCode).ConfigureAwait(false);
            var errBody = _translator.WriteErrorBody(inbound.Wire, statusCode, attemptErrorCode, UpstreamMessage(statusCode, providerCode));
            await WriteErrorToSink(sink, statusCode, errBody, false, ct).ConfigureAwait(false);
            ex.Attempts.Add(new RouterAttemptRecord(ex.RequestId, attemptOrdinal, target.Upstream.Slug, target.NativeModel, target.Upstream.Wire, statusCode, attemptErrorCode, durationMs, false, attemptStarted));
            ex.UpstreamSlug = target.Upstream.Slug;
            ex.NativeModel = target.NativeModel;
            var result = await ex.FinishAsync(statusCode, attemptErrorCode, outcome).ConfigureAwait(false);
            httpResp.Dispose();
            return result;
        }
    }

    /// <summary>One response from a whole upstream stream: its text, reasoning, tool calls, and usage.</summary>
    private RouterResponse CollectStream(string wire, byte[] body)
    {
        var parser = _translator.CreateStreamParser(wire);
        var events = parser.Feed(body).Concat(parser.Complete()).ToList();
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        RouterUsage? usage = null;
        string? finish = null;
        foreach (var item in events)
        {
            switch (item)
            {
                case TextDeltaEvent delta: text.Append(delta.Text); break;
                case ReasoningDeltaEvent delta: reasoning.Append(delta.Text); break;
                case ToolCallStartEvent start: calls[start.Index] = (start.Id, start.Name, new StringBuilder()); break;
                case ToolCallArgumentsDeltaEvent delta when calls.TryGetValue(delta.Index, out var call): call.Arguments.Append(delta.JsonDelta); break;
                case UsageEvent reported: usage = reported.Usage; break;
                case FinishEvent end: finish = end.Reason; break;
            }
        }
        if (finish == FinishEvent.Error) throw new TranslationException("upstream_error", "The upstream stream failed.");
        var toolCalls = calls.Values
            .Select(call => new RouterToolCallPart(call.Id, call.Name, call.Arguments.Length == 0 ? "{}" : call.Arguments.ToString()))
            .ToList();
        return new RouterResponse(text.ToString(), reasoning.ToString(), toolCalls,
            finish ?? (toolCalls.Count > 0 ? FinishEvent.ToolCalls : FinishEvent.Stop), usage);
    }

    /// <summary>
    /// Which target a request starts on. Ordered keeps the resolver's own order. Sticky starts on the
    /// routed target that last answered, and leaves out a native Claude the owner has already been moved
    /// off, so an exhausted account is not retried every turn. Round robin advances the starting routed
    /// target once per request. The rest of the order is unchanged, so every target is still tried.
    /// </summary>
    private IReadOnlyList<ResolvedTarget> OrderTargets(IReadOnlyList<ResolvedTarget> targets, string strategy, string key)
    {
        if (targets.Count < 2 || strategy is RouterSwitchStrategy.Off or RouterSwitchStrategy.Ordered)
            return targets;

        var hasNative = RouterNative.IsNative(targets[0].Upstream);
        var routed = hasNative ? targets.Skip(1).ToList() : targets.ToList();
        if (routed.Count == 0) return targets;

        var offset = 0;
        lock (_strategyLock)
        {
            if (strategy == RouterSwitchStrategy.Sticky)
            {
                if (_stickyTargets.TryGetValue(key, out var sticky))
                {
                    hasNative = false;
                    var found = routed.FindIndex(target => TargetKey(target) == sticky);
                    if (found > 0) offset = found;
                }
            }
            else if (strategy == RouterSwitchStrategy.RoundRobin)
            {
                offset = _roundRobinOffsets.TryGetValue(key, out var next) ? next % routed.Count : 0;
                _roundRobinOffsets[key] = (offset + 1) % routed.Count;
            }
        }

        if (offset > 0) routed = [.. routed.Skip(offset), .. routed.Take(offset)];
        return hasNative ? [targets[0], .. routed] : routed;
    }

    private void RememberSuccessfulTarget(string strategy, string key, ResolvedTarget target)
    {
        if (strategy != RouterSwitchStrategy.Sticky || RouterNative.IsNative(target.Upstream)) return;
        lock (_strategyLock) _stickyTargets[key] = TargetKey(target);
    }

    private void ForgetStickyTarget(string strategy, string key, ResolvedTarget target)
    {
        if (strategy != RouterSwitchStrategy.Sticky) return;
        lock (_strategyLock)
        {
            if (_stickyTargets.TryGetValue(key, out var sticky) && sticky == TargetKey(target))
                _stickyTargets.Remove(key);
        }
    }

}
