using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Router;
using AgentNotify.Core.Router.Connect;
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
                    presets = PublicPresets(options.Router?.Credentials),
                    limits = new { max_request_body_bytes = config.RouterMaxRequestBodyBytes, ledger_retention_days = config.RouterLedgerRetentionDays }
                }, WebUiEndpoints.JsonOptions);
            }

            await SyncCodexAccountsAsync(options, ct);
            var upstreams = await routerConfig.ListUpstreamsAsync(ct);
            var routes = await routerConfig.ListRoutesAsync(ct);
            var settings = await routerConfig.GetSettingsAsync(ct);
            return Results.Json(new
            {
                enabled = config.RouterEnabled,
                has_key = !string.IsNullOrWhiteSpace(config.RouterKey),
                base_url = $"http://127.0.0.1:{config.Port}/router/v1",
                anthropic_base_url = $"http://127.0.0.1:{config.Port}/router",
                upstreams = upstreams.Select(ToPublic),
                routes = routes.Select(r => new { id = r.Id, name = r.Name, kind = r.Kind, targets = r.Targets, enabled = r.Enabled, created_at = r.CreatedAt, updated_at = r.UpdatedAt }),
                default_route = settings.DefaultRoute,
                smart_routing = settings.SmartRouting,
                switch_strategy = settings.SwitchStrategy,
                claude_fallback_route = settings.ClaudeFallbackRoute,
                smart_groups = SmartGroups(upstreams),
                presets = PublicPresets(options.Router?.Credentials),
                accounts = await AccountsAsync(options, ct),
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
            if ((!string.IsNullOrWhiteSpace(body.ApiKey) || body.UseOpencodeKey == true) && !ack)
                return Error("You must acknowledge the storage risk to add an API key.");
            var preset = RouterPresetCatalog.FindById(body.PresetId);
            try
            {
                if (await CheckReferenceAsync(body, options, http.RequestAborted) is { } refError) return Error(refError);
                var key = ResolveKey(body, preset, options.Router?.Credentials);
                var created = await routerConfig.CreateUpstreamAsync(body.Slug, body.Label, body.Wire, body.BaseUrl, key, body.Models, body.Enabled ?? true, http.RequestAborted,
                    body.Auth ?? preset?.Auth, ResolveModelWires(body, preset), body.CredentialRef);
                // The ChatGPT plan covers every Codex account: the others are added beside this one.
                if (created.Auth == RouterAuth.CodexChatGpt) await SyncCodexAccountsAsync(options, http.RequestAborted);
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
            if ((!string.IsNullOrWhiteSpace(body.ApiKey) || body.UseOpencodeKey == true) && !ack)
                return Error("You must acknowledge the storage risk to add an API key.");
            var preset = RouterPresetCatalog.FindById(body.PresetId);
            try
            {
                if (await CheckReferenceAsync(body, options, http.RequestAborted) is { } refError) return Error(refError);
                var key = ResolveKey(body, preset, options.Router?.Credentials);
                var updated = await routerConfig.UpdateUpstreamAsync(id, body.Slug, body.Label, body.Wire, body.BaseUrl, key, body.Models, body.Enabled, body.ClearKey ?? false, http.RequestAborted,
                    body.Auth, ResolveModelWires(body, preset), body.CredentialRef);
                return Results.Json(ToPublic(updated), WebUiEndpoints.JsonOptions);
            }
            catch (KeyNotFoundException) { return Error("That upstream was not found.", 404); }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        // Lists a provider's models so they can be ticked rather than typed. The key is the one being
        // entered, the one OpenCode holds, or — for a saved upstream — its stored one; it is sent only
        // to the base URL named, under the same destination rule as routed traffic.
        app.MapPost($"{WebUiEndpoints.BasePath}/api/router/models/fetch", async (HttpContext http) =>
        {
            if (routerConfig is null || options.Router is null) return Error("Router is not configured.", 404);
            var body = await ReadAsync<UpstreamBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            var preset = RouterPresetCatalog.FindById(body.PresetId);
            var baseUrl = body.BaseUrl ?? preset?.BaseUrl;
            var wire = body.Wire ?? preset?.Wire ?? RouterWire.OpenAiChat;
            var auth = body.Auth ?? preset?.Auth ?? RouterAuth.ApiKey;
            if (await CheckReferenceAsync(body, options, http.RequestAborted) is { } refError) return Error(refError);
            try
            {
                var key = ResolveKey(body, preset, options.Router.Credentials);
                var reference = body.CredentialRef;
                if (key is null && reference is null && body.UpstreamId is { Length: > 0 } upstreamId)
                {
                    var stored = await routerConfig.GetStoredUpstreamAsync(upstreamId, http.RequestAborted);
                    if (stored is null) return Error("That upstream was not found.", 404);
                    reference = stored.CredentialRef;
                    // The stored key is only ever sent back to the host it was saved for.
                    if (reference is null && string.Equals(stored.BaseUrl, baseUrl?.Trim().TrimEnd('/'), StringComparison.Ordinal))
                        key = routerConfig.DecryptKey(stored);
                }
                if (key is null && RouterCredentialRef.ApiAccountId(reference) is { } accountId)
                    key = await options.Router.Credentials.ApiAccountKeyAsync(accountId, http.RequestAborted);
                var models = await options.Router.Discovery.FetchAsync(baseUrl, wire, auth, key, preset, http.RequestAborted,
                    RouterCredentialRef.ProfileDirectory(reference));
                return Results.Json(new
                {
                    models = models.Select(model => new { id = model, wire = preset?.WireFor(model) ?? wire })
                }, WebUiEndpoints.JsonOptions);
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
            catch (SubscriptionAuthException ex) { return Error(ex.Message, StatusCodes.Status409Conflict); }
            catch (InvalidOperationException ex) { return Error(ex.Message, StatusCodes.Status502BadGateway); }
            catch (CryptographicException) { return Error("The stored key cannot be read on this computer. Enter it again.", StatusCodes.Status409Conflict); }
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

        app.MapPut($"{WebUiEndpoints.BasePath}/api/router/switch-settings", async (HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var body = await ReadAsync<SwitchSettingsBody>(http);
            if (body is null) return Error("The request body is not valid JSON.");
            try
            {
                await routerConfig.SetSwitchSettingsAsync(body.Strategy, body.ClaudeFallbackRoute, http.RequestAborted);
                var settings = await routerConfig.GetSettingsAsync(http.RequestAborted);
                return Results.Json(new
                {
                    switch_strategy = settings.SwitchStrategy,
                    smart_routing = settings.SmartRouting,
                    claude_fallback_route = settings.ClaudeFallbackRoute
                }, WebUiEndpoints.JsonOptions);
            }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapGet($"{WebUiEndpoints.BasePath}/api/router/effort-mappings", async (CancellationToken ct) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var mappings = await routerConfig.ListEffortCapabilitiesAsync(ct);
            return Results.Json(new
            {
                source_levels = RouterEffortCatalog.SourceLevels,
                mappings = mappings.Select(mapping => new
                {
                    upstream_id = mapping.UpstreamId,
                    upstream_slug = mapping.UpstreamSlug,
                    model = mapping.Model,
                    wire = mapping.Wire,
                    family = mapping.Family,
                    source = mapping.Source,
                    supported_values = mapping.SupportedValues,
                    level_map = mapping.LevelMap,
                    default_value = mapping.DefaultValue
                })
            }, WebUiEndpoints.JsonOptions);
        });

        app.MapPut($"{WebUiEndpoints.BasePath}/api/router/effort-mappings", async (HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var body = await ReadAsync<EffortMappingBody>(http);
            if (body?.UpstreamId is null || body.Model is null) return Error("The request body is not valid JSON.");
            try
            {
                await routerConfig.SetEffortMappingAsync(body.UpstreamId, body.Model, body.SupportedValues,
                    body.LevelMap, body.DefaultValue, http.RequestAborted);
                return Results.Json(new { saved = true }, WebUiEndpoints.JsonOptions);
            }
            catch (KeyNotFoundException) { return Error("That upstream was not found.", 404); }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapDelete($"{WebUiEndpoints.BasePath}/api/router/effort-mappings", async (HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var upstreamId = http.Request.Query["upstream_id"].ToString();
            var model = http.Request.Query["model"].ToString();
            if (string.IsNullOrEmpty(upstreamId) || string.IsNullOrEmpty(model)) return Error("upstream_id and model are required.");
            await routerConfig.ResetEffortMappingAsync(upstreamId, model, http.RequestAborted);
            return Results.Json(new { reset = true }, WebUiEndpoints.JsonOptions);
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

        app.MapGet($"{WebUiEndpoints.BasePath}/api/router/agents", (Func<CancellationToken, Task<IResult>>)(async ct =>
        {
            var connect = options.RouterConnect;
            if (connect is null) return Error("Connecting agents is not available on this host.", StatusCodes.Status404NotFound);
            var snapshot = routerConfig is null ? null : await routerConfig.GetSnapshotAsync(ct);
            return Results.Json(new
            {
                agents = (await connect.ListAsync(ct)).Select(ToPublicAgent).ToArray(),
                selectable = snapshot is null
                    ? Array.Empty<string>()
                    : RouterConnectService.Selectable(snapshot).ToArray(),
                claude_native_selectable = ClaudeCodeRouterConnector.NativeModels,
                slots = ClaudeCodeRouterConnector.Slots.Keys,
                openai_base_url = connect.OpenAiBaseUrl,
                anthropic_base_url = connect.AnthropicBaseUrl
            }, WebUiEndpoints.JsonOptions);
        }));

        app.MapPost($"{WebUiEndpoints.BasePath}/api/router/agents/{{id}}/connect", (Func<string, HttpContext, Task<IResult>>)(async (id, http) =>
        {
            var connect = options.RouterConnect;
            if (connect is null) return Error("Connecting agents is not available on this host.", StatusCodes.Status404NotFound);
            var body = await ReadAsync<ConnectBody>(http) ?? new ConnectBody();
            return await RunConnectAsync(() => connect.ConnectAsync(
                id,
                new RouterConnectRequest(body.Model, body.ModelSlots, body.Options),
                http.RequestAborted));
        }));

        app.MapPost($"{WebUiEndpoints.BasePath}/api/router/agents/{{id}}/disconnect", (Func<string, HttpContext, Task<IResult>>)(async (id, http) =>
        {
            var connect = options.RouterConnect;
            if (connect is null) return Error("Connecting agents is not available on this host.", StatusCodes.Status404NotFound);
            return await RunConnectAsync(() => connect.DisconnectAsync(id, http.RequestAborted));
        }));

        app.MapPost($"{WebUiEndpoints.BasePath}/api/router/agents/{{id}}/restore", (Func<string, HttpContext, Task<IResult>>)(async (id, http) =>
        {
            var connect = options.RouterConnect;
            if (connect is null) return Error("Connecting agents is not available on this host.", StatusCodes.Status404NotFound);
            var body = await ReadAsync<RestoreBody>(http);
            if (body?.BackupId is not { Length: > 0 }) return Error("Choose which saved copy to restore.");
            return await RunConnectAsync(() => connect.RestoreAsync(id, body.BackupId, http.RequestAborted));
        }));

    }

    /// <summary>
    /// Runs one connector action, turning its refusals into the same error shape the rest of the
    /// interface uses. Writing another program's configuration file can fail for ordinary reasons —
    /// the agent is not installed, the file is not valid JSON, the disk is read-only — and each of
    /// those is the owner's to fix, not a server fault.
    /// </summary>
    private static async Task<IResult> RunConnectAsync(Func<Task<RouterConnectResult>> action)
    {
        try
        {
            var result = await action();
            return Results.Json(new { agent = ToPublicAgent(result.Agent), message = result.Message }, WebUiEndpoints.JsonOptions);
        }
        catch (KeyNotFoundException error) { return Error(error.Message, StatusCodes.Status404NotFound); }
        catch (FileNotFoundException error) { return Error(error.Message, StatusCodes.Status404NotFound); }
        catch (ArgumentException error) { return Error(error.Message); }
        catch (InvalidOperationException error) { return Error(error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Error($"That file could not be written: {error.Message}");
        }
    }

    private static object ToPublicAgent(RouterAgentInfo agent) => new
    {
        id = agent.Id,
        kind = agent.Kind,
        account_label = agent.AccountLabel,
        display_name = agent.DisplayName,
        config_path = agent.ConfigPath,
        detected = agent.Detected,
        connected = agent.Connected,
        connected_at = agent.ConnectedAt,
        selected_model = agent.SelectedModel,
        model_slots = agent.ModelSlots,
        catalog_path = agent.CatalogPath,
        catalog_model_count = agent.CatalogModelCount,
        options = agent.Options.Select(option => new
        {
            id = option.Id,
            display_name = option.DisplayName,
            description = option.Description,
            is_model_selector = option.IsModelSelector,
            choices = option.Choices
        }).ToArray(),
        option_values = agent.OptionValues,
        blocked = agent.Blocked,
        backups = agent.Backups.Select(b => new { id = b.Id, created_at = b.CreatedAt, reason = b.Reason })
    };


    private sealed class ConnectBody
    {
        public string? Model { get; set; }
        public Dictionary<string, string>? ModelSlots { get; set; }
        public Dictionary<string, string>? Options { get; set; }
    }

    private sealed class RestoreBody { public string? BackupId { get; set; } }

    private static object ToPublic(RouterUpstream u) => new { id = u.Id, slug = u.Slug, label = u.Label, wire = u.Wire, base_url = u.BaseUrl, has_key = u.HasKey, models = u.Models, enabled = u.Enabled, created_at = u.CreatedAt, updated_at = u.UpdatedAt, auth = u.Auth, model_wires = u.ModelWires, credential_ref = u.CredentialRef };

    /// <summary>
    /// The presets, with what this computer already has for each: a subscription sign-in, or a key
    /// OpenCode holds. Only whether one exists is said, never the key.
    /// </summary>
    private static IEnumerable<object> PublicPresets(RouterCredentialSource? credentials) =>
        RouterPresetCatalog.Presets.Select(p =>
        {
            var signin = credentials is not null && RouterAuth.IsSubscription(p.Auth) ? credentials.Describe(p.Auth) : (true, "");
            return new
            {
                id = p.Id,
                display_name = p.DisplayName,
                wire = p.Wire,
                base_url = p.BaseUrl,
                needs_key = p.NeedsKey,
                docs_url = p.DocsUrl,
                kind = p.Kind,
                auth = p.Auth,
                blurb = p.Blurb,
                key_url = p.KeyUrl,
                unofficial = p.Unofficial,
                opencode_key = credentials?.ReadOpenCodeKey(p.OpenCodeAuthId) is not null,
                signin_ready = signin.Item1,
                signin_detail = signin.Item2
            };
        });

    /// <summary>API-account providers whose key also works for inference, mapped to the router preset.</summary>
    private static readonly IReadOnlyDictionary<string, string> ApiAccountPresets = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["deepseek"] = "deepseek",
        ["moonshot"] = "moonshot",
        ["siliconflow"] = "siliconflow",
        ["openrouter"] = "openrouter"
        // OpenAI and Anthropic accounts there hold Admin keys, which cannot send model requests.
    };

    /// <summary>
    /// What the rest of AgentNotify already knows that a provider can reuse: keys under Live quota's
    /// API accounts, and the Codex accounts (the same list Live quota monitors) whose ChatGPT sign-in a
    /// subscription upstream can use. No key or token is included, only what exists.
    /// </summary>
    private static async Task<object> AccountsAsync(WebUiOptions options, CancellationToken ct)
    {
        var apiAccounts = new List<object>();
        if (options.Billing is not null)
        {
            try
            {
                foreach (var account in await options.Billing.ListAsync(ct))
                    if (ApiAccountPresets.TryGetValue(account.Provider, out var preset))
                        apiAccounts.Add(new { id = account.Id, provider = account.Provider, label = account.Label, preset_id = preset,
                            credential_ref = RouterCredentialRef.ApiAccountPrefix + account.Id });
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { }
        }

        var codexAccounts = new List<object>();
        if (options.RouterConnect is not null && options.Router is not null)
        {
            foreach (var profile in options.RouterConnect.Profiles().Where(p => p.Kind == CodexRouterConnector.Id))
            {
                var (ready, detail) = options.Router.Credentials.Describe(RouterAuth.CodexChatGpt, profile.Directory);
                codexAccounts.Add(new
                {
                    id = profile.Id, label = profile.Label, directory = profile.Directory, is_default = profile.IsDefault,
                    display_directory = WebUiEndpoints.TildePath(profile.Directory, options.NativeHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                    signed_in = ready, detail, credential_ref = RouterCredentialRef.ProfilePrefix + profile.Directory
                });
            }
        }
        return new { api_accounts = apiAccounts, codex_accounts = codexAccounts };
    }

    /// <summary>
    /// Every model more than one enabled provider serves, with the order smart routing tries them in
    /// when nothing else decides (a bare model name), so the page shows what the switch does.
    /// </summary>
    private static IEnumerable<object> SmartGroups(IReadOnlyList<RouterUpstream> upstreams) =>
        upstreams
            .Select((upstream, order) => (upstream, order))
            .Where(item => item.upstream.Enabled)
            .SelectMany(item => item.upstream.Models.Select(model => (item.upstream, item.order, model)))
            .GroupBy(item => RouteResolver.ModelKey(item.model))
            .Where(group => group.Select(item => item.upstream.Slug).Distinct().Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                model = group.Key,
                targets = group
                    .OrderBy(item => RouteResolver.CostTier(item.upstream.Auth, item.upstream.BaseUrl))
                    .ThenBy(item => item.order)
                    .Select(item => item.upstream.Slug + "/" + item.model)
                    .ToList()
            });

    /// <summary>
    /// The Codex accounts this computer lists (the ones Insights shows), with whether each is signed
    /// in, handed to <see cref="RouterConfigService.SyncCodexAccountsAsync"/>.
    /// </summary>
    private static async Task SyncCodexAccountsAsync(WebUiOptions options, CancellationToken ct)
    {
        if (options.RouterConfig is null || options.RouterConnect is null || options.Router is null) return;
        var accounts = options.RouterConnect.Profiles()
            .Where(profile => profile.Kind == CodexRouterConnector.Id)
            .Select(profile => new CodexPlanAccount(profile.Directory, profile.Label, profile.IsDefault,
                options.Router.Credentials.Describe(RouterAuth.CodexChatGpt, profile.Directory).Ready))
            .ToList();
        try { await options.RouterConfig.SyncCodexAccountsAsync(accounts, ct); }
        catch (Exception exception) when (exception is ArgumentException or IOException or Microsoft.Data.Sqlite.SqliteException) { }
    }

    /// <summary>A referenced API account must still exist before an upstream is pointed at it.</summary>
    private static async Task<string?> CheckReferenceAsync(UpstreamBody body, WebUiOptions options, CancellationToken ct)
    {
        if (RouterCredentialRef.ProfileDirectory(body.CredentialRef) is { } directory)
        {
            // Only an account this computer already lists: never an arbitrary directory's sign-in.
            var known = (options.RouterConnect?.Profiles().Select(p => p.Directory) ?? [])
                .Append(options.Router?.Credentials.MuseHome);
            return known.Any(item => QuotaAccountDefinition.SameDirectory(item, directory))
                ? null : "That account directory is not one of your agent accounts.";
        }
        if (RouterCredentialRef.ApiAccountId(body.CredentialRef) is not { } id) return null;
        if (options.Billing is null) return "API accounts are not available in this host.";
        var accounts = await options.Billing.ListAsync(ct);
        return accounts.Any(account => account.Id == id) ? null : "That API account no longer exists.";
    }

    /// <summary>The key a create or update should store: the typed one, or the one OpenCode already holds.</summary>
    private static string? ResolveKey(UpstreamBody body, RouterPreset? preset, RouterCredentialSource? credentials)
    {
        if (!string.IsNullOrWhiteSpace(body.ApiKey)) return body.ApiKey;
        if (body.UseOpencodeKey == true)
            return credentials?.ReadOpenCodeKey(preset?.OpenCodeAuthId)
                ?? throw new ArgumentException("OpenCode has no key for this provider any more. Paste one instead.");
        return null;
    }

    /// <summary>Per-model wires sent by the client, or else derived from the preset's rules.</summary>
    private static IReadOnlyDictionary<string, string>? ResolveModelWires(UpstreamBody body, RouterPreset? preset) =>
        body.ModelWires ?? (preset is null || body.Models is null ? null : preset.ModelWires(body.Models));

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
        public string? Auth { get; set; }
        public string? PresetId { get; set; }
        public string? UpstreamId { get; set; }
        public bool? UseOpencodeKey { get; set; }
        public Dictionary<string, string>? ModelWires { get; set; }
        public string? CredentialRef { get; set; }
    }
    private sealed class RouteBody { public string? Name { get; set; } public string? Kind { get; set; } public IReadOnlyList<string>? Targets { get; set; } public bool? Enabled { get; set; } }
    private sealed class DefaultRouteBody { public string? Route { get; set; } }
    private sealed class SwitchSettingsBody
    {
        public string? Strategy { get; set; }
        public string? ClaudeFallbackRoute { get; set; }
    }
    private sealed class EffortMappingBody
    {
        public string? UpstreamId { get; set; }
        public string? Model { get; set; }
        public IReadOnlyList<string>? SupportedValues { get; set; }
        public IReadOnlyList<string>? LevelMap { get; set; }
        public string? DefaultValue { get; set; }
    }
}
