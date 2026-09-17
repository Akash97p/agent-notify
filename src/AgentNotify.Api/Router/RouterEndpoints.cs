using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Router;
using AgentNotify.Core.Router.Translation;
using AgentNotify.Api.WebUi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.Router;

/// <summary>UI and proxy endpoints for the provider router.</summary>
public static class RouterEndpoints
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

        string? provided = null;
        var authHeader = context.Request.Headers.Authorization.ToString();
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

        var inbound = new RouterInbound(inboundWire, bodyBytes, anthVersion, anthBeta, isCountTokens);
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

    public static void Map(WebApplication app, WebUiOptions options, AgentNotifyConfig config, int port)
    {
        var routerConfig = options.RouterConfig;
        var routerRepo = new RouterRepository(options.ConfigStore.DbPath);

        app.MapGet($"{WebUiEndpoints.BasePath}/api/router", async (HttpContext http, CancellationToken ct) =>
        {
            if (routerConfig is null)
            {
                return Results.Json(new
                {
                    enabled = config.RouterEnabled,
                    has_key = !string.IsNullOrWhiteSpace(config.RouterKey),
                    base_url = $"http://127.0.0.1:{config.Port}/router/v1",
                    anthropic_base_url = $"http://127.0.0.1:{config.Port}/router",
                    upstreams = Array.Empty<object>(),
                    routes = Array.Empty<object>(),
                    default_route = (string?)null,
                    presets = RouterPresetCatalog.Presets.Select(p => new { id = p.Id, display_name = p.DisplayName, wire = p.Wire, base_url = p.BaseUrl, needs_key = p.NeedsKey, docs_url = p.DocsUrl }),
                    limits = new { max_request_body_bytes = config.RouterMaxRequestBodyBytes, ledger_retention_days = config.RouterLedgerRetentionDays }
                }, WebUiEndpoints.JsonOptions);
            }

            var upstreams = await routerConfig.ListUpstreamsAsync(ct);
            var routes = await routerConfig.ListRoutesAsync(ct);
            var settings = await routerConfig.GetSettingsAsync(ct);
            return Results.Json(new
            {
                enabled = config.RouterEnabled,
                has_key = !string.IsNullOrWhiteSpace(config.RouterKey),
                base_url = $"http://127.0.0.1:{config.Port}/router/v1",
                anthropic_base_url = $"http://127.0.0.1:{config.Port}/router",
                upstreams = upstreams.Select(u => new { id = u.Id, slug = u.Slug, label = u.Label, wire = u.Wire, base_url = u.BaseUrl, has_key = u.HasKey, models = u.Models, enabled = u.Enabled, created_at = u.CreatedAt, updated_at = u.UpdatedAt }),
                routes = routes.Select(r => new { id = r.Id, name = r.Name, kind = r.Kind, targets = r.Targets, enabled = r.Enabled, created_at = r.CreatedAt, updated_at = r.UpdatedAt }),
                default_route = settings.DefaultRoute,
                presets = RouterPresetCatalog.Presets.Select(p => new { id = p.Id, display_name = p.DisplayName, wire = p.Wire, base_url = p.BaseUrl, needs_key = p.NeedsKey, docs_url = p.DocsUrl }),
                limits = new { max_request_body_bytes = config.RouterMaxRequestBodyBytes, ledger_retention_days = config.RouterLedgerRetentionDays }
            }, WebUiEndpoints.JsonOptions);
        });

        app.MapPost($"{WebUiEndpoints.BasePath}/api/router/enable", async (HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured on this host.", 404);
            var body = await ReadAsync<EnableBody>(http);
            if (body is null || body.Enabled is null) return Error("The request body is not valid JSON.");
            string? generated = null;
            if (body.Enabled.Value)
                generated = await routerConfig.SetRouterEnabledAsync(true, http.RequestAborted);
            else
            {
                await routerConfig.SetRouterEnabledAsync(false, http.RequestAborted);
            }
            var resp = new Dictionary<string, object?> { ["enabled"] = config.RouterEnabled };
            if (generated != null) resp["key"] = generated;
            return Results.Json(resp, WebUiEndpoints.JsonOptions);
        });

        app.MapPost($"{WebUiEndpoints.BasePath}/api/router/key/regenerate", async (HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured on this host.", 404);
            var key = await routerConfig.RegenerateRouterKeyAsync(http.RequestAborted);
            return Results.Json(new { key }, WebUiEndpoints.JsonOptions);
        });

        app.MapPost($"{WebUiEndpoints.BasePath}/api/router/upstreams", async (HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var body = await ReadAsync<UpstreamBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            bool ack = (body.AckKeyStorage == true) || (body.AcknowledgeRisk == true);
            if (!string.IsNullOrWhiteSpace(body.ApiKey) && !ack)
                return Error("You must acknowledge the storage risk to add an API key.");
            try
            {
                var created = await routerConfig.CreateUpstreamAsync(body.Slug, body.Label, body.Wire, body.BaseUrl, body.ApiKey, body.Models, body.Enabled ?? true, http.RequestAborted);
                return Results.Json(ToPublic(created), WebUiEndpoints.JsonOptions, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapPut($"{WebUiEndpoints.BasePath}/api/router/upstreams/{{id}}", async (string id, HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var body = await ReadAsync<UpstreamBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            bool ack = (body.AckKeyStorage == true) || (body.AcknowledgeRisk == true);
            if (!string.IsNullOrWhiteSpace(body.ApiKey) && !ack)
                return Error("You must acknowledge the storage risk to add an API key.");
            try
            {
                var updated = await routerConfig.UpdateUpstreamAsync(id, body.Slug, body.Label, body.Wire, body.BaseUrl, body.ApiKey, body.Models, body.Enabled, body.ClearKey ?? false, http.RequestAborted);
                return Results.Json(ToPublic(updated), WebUiEndpoints.JsonOptions);
            }
            catch (KeyNotFoundException) { return Error("That upstream was not found.", 404); }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapDelete($"{WebUiEndpoints.BasePath}/api/router/upstreams/{{id}}", async (string id, CancellationToken ct) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            try
            {
                await routerConfig.DeleteUpstreamAsync(id, ct);
                return Results.Json(new { deleted = id }, WebUiEndpoints.JsonOptions);
            }
            catch (KeyNotFoundException) { return Error("That upstream was not found.", 404); }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapPost($"{WebUiEndpoints.BasePath}/api/router/routes", async (HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var body = await ReadAsync<RouteBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                var created = await routerConfig.CreateRouteAsync(body.Name, body.Kind, body.Targets, body.Enabled ?? true, http.RequestAborted);
                return Results.Json(created, WebUiEndpoints.JsonOptions, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapPut($"{WebUiEndpoints.BasePath}/api/router/routes/{{id}}", async (string id, HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var body = await ReadAsync<RouteBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                var updated = await routerConfig.UpdateRouteAsync(id, body.Name, body.Kind, body.Targets, body.Enabled, http.RequestAborted);
                return Results.Json(updated, WebUiEndpoints.JsonOptions);
            }
            catch (KeyNotFoundException) { return Error("That route was not found.", 404); }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapDelete($"{WebUiEndpoints.BasePath}/api/router/routes/{{id}}", async (string id, CancellationToken ct) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            try
            {
                await routerConfig.DeleteRouteAsync(id, ct);
                return Results.Json(new { deleted = id }, WebUiEndpoints.JsonOptions);
            }
            catch (KeyNotFoundException) { return Error("That route was not found.", 404); }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapPut($"{WebUiEndpoints.BasePath}/api/router/default", async (HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var body = await ReadAsync<DefaultRouteBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                await routerConfig.SetDefaultRouteAsync(body.Route, http.RequestAborted);
                var settings = await routerConfig.GetSettingsAsync(http.RequestAborted);
                return Results.Json(new { default_route = settings.DefaultRoute }, WebUiEndpoints.JsonOptions);
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapGet($"{WebUiEndpoints.BasePath}/api/router/requests", async (HttpContext http, CancellationToken ct) =>
        {
            await routerRepo.InitializeAsync(ct);
            int limit = 50;
            if (http.Request.Query.TryGetValue("limit", out var v) && int.TryParse(v, out var parsed)) limit = parsed;
            limit = Math.Clamp(limit, 1, 200);
            var entries = await routerRepo.ListRecentRequestsAsync(limit, ct);
            return Results.Json(new { requests = entries.Select(e => new { request = e.Request, attempts = e.Attempts }) }, WebUiEndpoints.JsonOptions);
        });

        app.MapGet($"{WebUiEndpoints.BasePath}/api/router/summary", async (HttpContext http, CancellationToken ct) =>
        {
            await routerRepo.InitializeAsync(ct);
            var daysRaw = http.Request.Query["days"].ToString();
            int days = 1;
            if (!string.IsNullOrEmpty(daysRaw))
            {
                if (!int.TryParse(daysRaw, out days) || (days != 1 && days != 7 && days != 30))
                    return Error("Choose 1, 7, or 30 days.");
            }
            var since = DateTimeOffset.UtcNow.AddDays(-days);
            var summary = await routerRepo.SummarizeAsync(since, ct);
            return Results.Json(new { summary }, WebUiEndpoints.JsonOptions);
        });
    }

    private static object ToPublic(RouterUpstream u) => new { id = u.Id, slug = u.Slug, label = u.Label, wire = u.Wire, base_url = u.BaseUrl, has_key = u.HasKey, models = u.Models, enabled = u.Enabled, created_at = u.CreatedAt, updated_at = u.UpdatedAt };

    private static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, WebUiEndpoints.JsonOptions, statusCode: status);

    private static async Task<T?> ReadAsync<T>(HttpContext http) where T : class
    {
        try { return await http.Request.ReadFromJsonAsync<T>(WebUiEndpoints.JsonOptions, http.RequestAborted); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException) { return null; }
    }

    private sealed class EnableBody { public bool? Enabled { get; set; } }
    private sealed class UpstreamBody
    {
        public string? Slug { get; set; }
        public string? Label { get; set; }
        public string? Wire { get; set; }
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public bool? ClearKey { get; set; }
        public IReadOnlyList<string>? Models { get; set; }
        public bool? Enabled { get; set; }
        public bool? AckKeyStorage { get; set; }
        public bool? AcknowledgeRisk { get; set; }
    }
    private sealed class RouteBody { public string? Name { get; set; } public string? Kind { get; set; } public IReadOnlyList<string>? Targets { get; set; } public bool? Enabled { get; set; } }
    private sealed class DefaultRouteBody { public string? Route { get; set; } }
}
