using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Core;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Delivery.Forms;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Harness;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Persistence;
using AgentNotify.Insights.Quota;
using AgentNotify.Core.Services;
using AgentNotify.Core.Skills;
using AgentNotify.Insights.Usage;
using AgentNotify.Core.Wsl;
using AgentNotify.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.WebUi;

/// <summary>
/// The local web UI: a browser front end for everything the Windows Settings window and
/// Notification Center manage, served by the broker itself so it works wherever the broker runs.
/// </summary>
/// <remarks>
/// <para>
/// There is no sign-in, by design: like the Windows tray app, the page trusts whoever is using this
/// computer. What it does defend against is other web sites reaching it through the browser.
/// </para>
/// <para>Checks, in the order <see cref="Guard"/> applies them:</para>
/// <list type="number">
/// <item>The listener is loopback-only, like the rest of the API.</item>
/// <item>The <c>Host</c> header must name this loopback listener. A DNS-rebinding page reaches the
/// port under its own host name and is refused before any handler runs.</item>
/// <item>Every state-changing request must carry <c>X-AgentNotify-UI: 1</c> and, when the browser
/// sends one, a same-origin <c>Origin</c>. A cross-site form cannot set that header, and a
/// cross-site script cannot send it without a CORS preflight this server never grants.</item>
/// <item>Provider secrets are write-only: no response carries a stored secret value.</item>
/// </list>
/// </remarks>
public sealed partial class WebUiEndpoints
{
    public const string BasePath = "/ui";
    public const string CsrfHeader = "X-AgentNotify-UI";

    private const long MaximumSoundUploadBytes = ManagedSoundStore.MaxSoundBytes + 64 * 1024;

    internal static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    /// <summary>The address of the web interface on a broker listening on <paramref name="port"/>.</summary>
    public static string Url(int port) => $"http://127.0.0.1:{port}{BasePath}/";

    /// <summary>Runs the web UI's host and cross-site checks. Returns false when it has already responded.</summary>
    internal static async Task<bool> Guard(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (!IsLoopbackHost(request.Host))
        {
            response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await response.WriteAsJsonAsync(new { error = "This interface is only served to the local machine address." }, JsonOptions);
            return false;
        }

        ApplySecurityHeaders(response, request.Path);

        // Routing treats "/ui" and "/ui/" alike, so the bare form is redirected here: the page's
        // relative asset and API URLs only resolve beneath the trailing slash.
        if (request.Path.Value == BasePath)
        {
            response.Redirect($"{BasePath}/");
            return false;
        }

        if (!request.Path.StartsWithSegments($"{BasePath}/api"))
            return true;

        response.Headers.CacheControl = "no-store";

        var mutating = !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method);
        if (mutating)
        {
            var origin = request.Headers.Origin.ToString();
            if (request.Headers[CsrfHeader] != "1" ||
                origin.Length > 0 && !IsLoopbackOrigin(origin, request.Host))
            {
                response.StatusCode = StatusCodes.Status403Forbidden;
                await response.WriteAsJsonAsync(new { error = "Cross-site request refused." }, JsonOptions);
                return false;
            }
        }


        return true;
    }

    internal static bool IsLoopbackHost(HostString host)
    {
        // An SSH local forward keeps the browser's local port in Host, which need not match
        // the broker's listening port. The listener itself remains bound to loopback.
        if (!host.HasValue || host.Port is not > 0) return false;
        return host.Host is "127.0.0.1" or "localhost" or "[::1]";
    }

    private static bool IsLoopbackOrigin(string origin, HostString host) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp &&
        string.Equals(uri.Authority, host.Value, StringComparison.OrdinalIgnoreCase) &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static void ApplySecurityHeaders(HttpResponse response, PathString path)
    {
        var headers = response.Headers;
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; " +
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    }

    private readonly WebApplication app;
    private readonly WebUiOptions options;
    private readonly AgentNotifyConfig config;
    private readonly INotificationRepository repository;
    private readonly NotificationService service;
    private readonly InteractionService? interactions;
    private readonly ApiCallbacks? callbacks;
    private readonly IAppLogger? logger;
    private readonly int port;
    private readonly string version;
    private readonly DateTimeOffset startedAt;
    private readonly RelayPairingSessions pairings;
    private readonly ProviderFormService forms;
    private readonly ManagedSoundStore sounds;
    private readonly IWslEnvironment wsl;
    private readonly string nativeHome;
    private readonly LocalUsageService usage;
    private readonly LiveQuotaService quota;
    private readonly object quotaAccountsGate = new();

    /// <summary>
    /// Mounts the web UI routes on <paramref name="app"/>. One instance holds the readers and
    /// stores every request shares; each route group lives in its own partial-class file.
    /// </summary>
    public static void Map(
        WebApplication app,
        WebUiOptions options,
        AgentNotifyConfig config,
        INotificationRepository repository,
        NotificationService service,
        InteractionService? interactions,
        ApiCallbacks? callbacks,
        IAppLogger? logger,
        int port,
        string version,
        DateTimeOffset startedAt) =>
        new WebUiEndpoints(app, options, config, repository, service, interactions, callbacks, logger, port, version, startedAt)
            .Register();

    private WebUiEndpoints(
        WebApplication app,
        WebUiOptions options,
        AgentNotifyConfig config,
        INotificationRepository repository,
        NotificationService service,
        InteractionService? interactions,
        ApiCallbacks? callbacks,
        IAppLogger? logger,
        int port,
        string version,
        DateTimeOffset startedAt)
    {
        this.app = app;
        this.options = options;
        this.config = config;
        this.repository = repository;
        this.service = service;
        this.interactions = interactions;
        this.callbacks = callbacks;
        this.logger = logger;
        this.port = port;
        this.version = version;
        this.startedAt = startedAt;
        pairings = new RelayPairingSessions();
        forms = new ProviderFormService(options.Providers);
        sounds = new ManagedSoundStore(options.ConfigStore.SoundsDir);
        wsl = options.Wsl ?? WslDiscovery.Default;
        nativeHome = options.NativeHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        usage = options.Usage ?? new LocalUsageService(wsl: wsl, accounts: UsageAccounts);
        quota = options.Quota ?? new LiveQuotaService(
            accounts: () => config.QuotaAccounts.ToArray(), usage: usage,
            defaultAccountLabel: key => config.DefaultQuotaAccountLabels.GetValueOrDefault(key), wsl: wsl,
            removedAccounts: () => config.RemovedQuotaAccounts, nativeHome: nativeHome,
            openCodeGoRenewalDay: () => config.OpenCodeGoRenewalDay);
        app.Lifetime.ApplicationStopping.Register(pairings.Dispose);
    }

    private void Register()
    {
        MapOverview();
        MapSettings();
        MapAttention();
        MapChannels();
        MapAgents();


        app.MapGet($"{BasePath}/{{**path}}", (string? path, HttpContext http) =>
        {
            path ??= "";
            if (path.StartsWith("api/", StringComparison.Ordinal))
                return Error("Not found.", StatusCodes.Status404NotFound);
            var asset = WebUiAssets.Find(path);
            if (asset is null && Path.HasExtension(path))
                return Results.NotFound();
            asset ??= WebUiAssets.Index;
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Bytes(asset.Content, asset.ContentType);
        });
    }
    private string? DetectedLabel(string key) => config.DefaultQuotaAccountLabels.GetValueOrDefault(key);

    /// <summary>Built-in and discovered accounts, including removed ones; the owner's list is filtered from these.</summary>
    private IReadOnlyList<QuotaAccountDefinition> DetectedAccounts() =>
    [
        QuotaAccountDefinition.Default("codex", DetectedLabel("codex")),
        QuotaAccountDefinition.Default("claude_code", DetectedLabel("claude_code")),
        .. QuotaAccountDefinition.MonitoredDiscoveredAccounts(wsl, DetectedLabel, config.QuotaAccounts,
            Array.Empty<string>(), nativeHome)
    ];

    /// <summary>Secondary profiles whose session logs Usage counts alongside the hand-added accounts.</summary>
    private IReadOnlyList<QuotaAccountDefinition> UsageAccounts() =>
    [
        .. config.QuotaAccounts.ToArray(),
        .. QuotaAccountDefinition.MonitoredDiscoveredAccounts(wsl, DetectedLabel, config.QuotaAccounts,
            config.RemovedQuotaAccounts, nativeHome)
            .Where(account => QuotaAccountDefinition.IsSecondaryAccountId(account.Id))
    ];

    private IEnumerable<QuotaAccountDefinition> MonitoredDetectedAccounts() =>
        DetectedAccounts().Where(account => !config.RemovedQuotaAccounts.Contains(account.Id));

    private void SaveQuotaAccounts(List<QuotaAccountDefinition> accounts, List<string> removed)
    {
        var (previousAccounts, previousRemoved) = (config.QuotaAccounts, config.RemovedQuotaAccounts);
        (config.QuotaAccounts, config.RemovedQuotaAccounts) = (accounts, removed);
        try { options.ConfigStore.Save(config); }
        catch { (config.QuotaAccounts, config.RemovedQuotaAccounts) = (previousAccounts, previousRemoved); throw; }
    }

}
