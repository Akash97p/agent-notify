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

public static partial class RouterEndpoints
{
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
            var families = await routerConfig.ListEffortFamiliesAsync(ct);
            return Results.Json(new
            {
                source_levels = RouterEffortCatalog.SourceLevels,
                families = families.Select(family => new
                {
                    family = family.Family,
                    source = family.Source,
                    supported_values = family.SupportedValues,
                    level_map = family.LevelMap,
                    default_value = family.DefaultValue,
                    model_override_count = family.ModelOverrideCount,
                    models = family.Models.Select(model => new
                    {
                        upstream_id = model.UpstreamId,
                        upstream_slug = model.UpstreamSlug,
                        model = model.Model,
                        wire = model.Wire,
                    })
                }),
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
            if (body?.UpstreamId is null && body?.Family is null) return Error("The request body is not valid JSON.");
            if (body!.Family is not null)
            {
                if (body.UpstreamId is not null || body.Model is not null)
                    return Error("Choose either a family or one concrete model, not both.");
                try
                {
                    await routerConfig.SetEffortFamilyMappingAsync(body.Family, body.SupportedValues,
                        body.LevelMap, body.DefaultValue, http.RequestAborted);
                    return Results.Json(new { saved = true }, WebUiEndpoints.JsonOptions);
                }
                catch (ArgumentException ex) { return Error(ex.Message); }
            }
            if (body.Model is null) return Error("The request body is not valid JSON.");
            try
            {
                await routerConfig.SetEffortMappingAsync(body.UpstreamId!, body.Model, body.SupportedValues,
                    body.LevelMap, body.DefaultValue, http.RequestAborted);
                return Results.Json(new { saved = true }, WebUiEndpoints.JsonOptions);
            }
            catch (KeyNotFoundException) { return Error("That upstream was not found.", 404); }
            catch (ArgumentException ex) { return Error(ex.Message); }
        });

        app.MapDelete($"{WebUiEndpoints.BasePath}/api/router/effort-mappings", async (HttpContext http) =>
        {
            if (routerConfig is null) return Error("Router is not configured.", 404);
            var family = http.Request.Query["family"].ToString();
            if (family.Length > 0)
            {
                try { await routerConfig.ResetEffortFamilyMappingAsync(family, http.RequestAborted); }
                catch (ArgumentException ex) { return Error(ex.Message); }
                return Results.Json(new { reset = true }, WebUiEndpoints.JsonOptions);
            }
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

}
