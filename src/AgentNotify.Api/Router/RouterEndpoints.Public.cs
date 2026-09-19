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
}
