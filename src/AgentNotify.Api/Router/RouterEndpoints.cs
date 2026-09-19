using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Router;
using AgentNotify.Router.Connect;
using AgentNotify.Router.Translation;
using AgentNotify.Api.WebUi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.Router;

/// <summary>UI and proxy endpoints for the provider router.</summary>
public static partial class RouterEndpoints
{
    internal static async Task<bool> TryHandleAsync(HttpContext context, AgentNotifyConfig config, WebUiOptions? wUi, RouterTranslator translator)
    {
        var path = context.Request.Path.Value ?? "";
        bool isAnthropicErrorWire = path.StartsWith("/router/v1/messages", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/router/v1/messages/count_tokens", StringComparison.OrdinalIgnoreCase);
        string wireForError = isAnthropicErrorWire ? RouterWire.AnthropicMessages : RouterWire.OpenAiResponses;

        Task WriteRouterError(int status, string code, string message)
        {
            var body = translator.WriteErrorBody(wireForError, status, code, message);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "no-store";
            return context.Response.Body.WriteAsync(body, context.RequestAborted).AsTask();
        }

        if (!WebUiEndpoints.IsLoopbackHost(context.Request.Host))
        {
            await WriteRouterError(StatusCodes.Status421MisdirectedRequest, "host_not_loopback", "This interface is only served to the local machine address.");
            return true;
        }

        if (context.Request.Headers.ContainsKey("Origin") && !string.IsNullOrWhiteSpace(context.Request.Headers.Origin.ToString()))
        {
            await WriteRouterError(StatusCodes.Status403Forbidden, "forbidden", "Cross-site request refused.");
            return true;
        }

        if (!config.RouterEnabled || wUi?.Router is null || wUi?.RouterConfig is null)
        {
            await WriteRouterError(StatusCodes.Status404NotFound, "router_disabled", "The router is disabled.");
            return true;
        }

        var bodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySizeFeature is not null && !bodySizeFeature.IsReadOnly)
            bodySizeFeature.MaxRequestBodySize = config.RouterMaxRequestBodyBytes;

        // A client that keeps its own Anthropic sign-in (Claude Code) sends the router key in a header of
        // its own, so its Authorization or x-api-key is its credential for Anthropic, not for us.
        string? provided = null;
        KeyValuePair<string, string>? clientCredential = null;
        var routerKeyHeader = context.Request.Headers[RouterNative.RouterKeyHeader].ToString();
        if (!string.IsNullOrWhiteSpace(routerKeyHeader))
        {
            provided = routerKeyHeader.Trim();
            var clientAuthorization = context.Request.Headers.Authorization.ToString();
            var clientApiKey = context.Request.Headers["x-api-key"].ToString();
            if (!string.IsNullOrWhiteSpace(clientAuthorization)) clientCredential = new("Authorization", clientAuthorization.Trim());
            else if (!string.IsNullOrWhiteSpace(clientApiKey)) clientCredential = new("x-api-key", clientApiKey.Trim());
        }
        var authHeader = provided is null ? context.Request.Headers.Authorization.ToString() : "";
        if (!string.IsNullOrWhiteSpace(authHeader))
        {
            var trimmed = authHeader.Trim();
            const string scheme = "Bearer ";
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                var tokenPart = trimmed[scheme.Length..].Trim();
                if (!string.IsNullOrEmpty(tokenPart)) provided = tokenPart;
            }
        }
        if (provided is null)
        {
            var xApi = context.Request.Headers["x-api-key"].ToString();
            if (!string.IsNullOrWhiteSpace(xApi)) provided = xApi.Trim();
        }

        bool authorized = false;
        if (!string.IsNullOrWhiteSpace(config.RouterKey) && !string.IsNullOrWhiteSpace(provided))
        {
            var a = Encoding.UTF8.GetBytes(provided);
            var b = Encoding.UTF8.GetBytes(config.RouterKey);
            if (a.Length == b.Length)
                authorized = CryptographicOperations.FixedTimeEquals(a, b);
        }
        if (!authorized)
        {
            await WriteRouterError(StatusCodes.Status401Unauthorized, "unauthorized", "Invalid router key.");
            return true;
        }

        var method = context.Request.Method;
        var trimmedPath = path.TrimEnd('/');
        if (HttpMethods.IsGet(context.Request.Method) && trimmedPath.Equals("/router/v1/models", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await wUi.Router!.ListModelsAsync(context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
            return true;
        }

        string? inboundWire = null;
        bool isCountTokens = false;
        bool isRouterPost = HttpMethods.IsPost(method);
        if (isRouterPost && trimmedPath.Equals("/router/v1/responses", StringComparison.OrdinalIgnoreCase)) inboundWire = RouterWire.OpenAiResponses;
        else if (isRouterPost && trimmedPath.Equals("/router/v1/chat/completions", StringComparison.OrdinalIgnoreCase)) inboundWire = RouterWire.OpenAiChat;
        else if (isRouterPost && trimmedPath.Equals("/router/v1/messages", StringComparison.OrdinalIgnoreCase)) inboundWire = RouterWire.AnthropicMessages;
        else if (isRouterPost && trimmedPath.Equals("/router/v1/messages/count_tokens", StringComparison.OrdinalIgnoreCase)) { inboundWire = RouterWire.AnthropicMessages; isCountTokens = true; }

        if (inboundWire is null)
        {
            await WriteRouterError(StatusCodes.Status404NotFound, "not_found", "Not found.");
            return true;
        }

        byte[] bodyBytes;
        try
        {
            bodyBytes = await ReadRouterBodyAsync(context, config.RouterMaxRequestBodyBytes);
        }
        catch (BadHttpRequestException)
        {
            await WriteRouterError(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "Request body too large.");
            return true;
        }

        var anthVersion = context.Request.Headers["anthropic-version"].ToString();
        if (string.IsNullOrWhiteSpace(anthVersion)) anthVersion = null;
        var anthBeta = context.Request.Headers["anthropic-beta"].ToString();
        if (string.IsNullOrWhiteSpace(anthBeta)) anthBeta = null;

        // The agent's own conversation ID, when it sends one, so a provider that pins a conversation
        // to one backend for prompt caching (OpenCode Go requires it) sees each conversation as one.
        string? sessionId = null;
        foreach (var header in RouterInbound.SessionHeaders)
        {
            var value = context.Request.Headers[header].ToString();
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 200) { sessionId = value.Trim(); break; }
        }

        var nativeHeaders = clientCredential is null
            ? []
            : context.Request.Headers
                .Where(header => RouterInbound.IsNativeHeader(header.Key))
                .Select(header => new KeyValuePair<string, string>(header.Key, header.Value.ToString()))
                .ToList();
        var inbound = new RouterInbound(inboundWire, bodyBytes, anthVersion, anthBeta, isCountTokens)
        {
            SessionId = sessionId,
            ClientCredential = clientCredential,
            NativeHeaders = nativeHeaders,
            Query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null
        };
        var sink = new RouterSink(context);
        try
        {
            await wUi.Router.ExecuteAsync(inbound, sink, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch
        {
            if (!sink.HasStarted)
            {
                try { await WriteRouterError(StatusCodes.Status500InternalServerError, "upstream_error", "The router could not complete this request."); } catch { }
            }
        }
        return true;
    }

    private static async Task<byte[]> ReadRouterBodyAsync(HttpContext ctx, long limit)
    {
        if (ctx.Request.ContentLength == 0) return Array.Empty<byte>();
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        while (true)
        {
            int read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted);
            if (read == 0) break;
            total += read;
            if (total > limit) throw new BadHttpRequestException("Request body too large.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    internal sealed class RouterSink : IRouterClientSink
    {
        private readonly HttpContext _context;
        private bool _headersSent;
        public RouterSink(HttpContext context) => _context = context;
        public bool HasStarted => _headersSent;
        public Task SetStatusAndHeadersAsync(int statusCode, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            if (_headersSent) throw new InvalidOperationException("Headers already sent.");
            _context.Response.StatusCode = statusCode;
            foreach (var kv in headers) _context.Response.Headers[kv.Key] = kv.Value;
            _headersSent = true;
            return Task.CompletedTask;
        }
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _context.Response.Body.WriteAsync(data, ct).AsTask();
        public Task FlushAsync(CancellationToken ct) => _context.Response.Body.FlushAsync(ct);
    }

}
