using AgentNotify.Core.Billing;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Router;
using AgentNotify.Core.Usage;
using AgentNotify.Core.Quota;
using AgentNotify.Core.Wsl;

namespace AgentNotify.Api.WebUi;

/// <summary>
/// The broker services the local web UI manages. Supplying these to <see cref="ApiHost.Build"/>
/// mounts the UI at <c>/ui/</c> on the same loopback listener as the API.
/// </summary>
public sealed class WebUiOptions
{
    public required ConfigStore ConfigStore { get; init; }
    public required ProviderProfileService Providers { get; init; }
    public required DeliveryRouteService Routes { get; init; }
    public required DeliveryDispatcher Dispatcher { get; init; }

    /// <summary>Optional local usage reader; the default reads the broker user's agent logs.</summary>
    public LocalUsageService? Usage { get; init; }

    /// <summary>Optional live account-quota reader; the default uses local agent credentials/CLI.</summary>
    public LiveQuotaService? Quota { get; init; }

    /// <summary>Optional stored provider-key balance reader; the default uses the config database.</summary>
    public BillingService? Billing { get; init; }

    /// <summary>
    /// Running WSL distributions whose agents' usage, quota, and skill folders are included. The
    /// default discovers them on Windows and finds none elsewhere.
    /// </summary>
    public IWslEnvironment? Wsl { get; init; }

    /// <summary>
    /// Native home scanned for secondary Codex/Claude Code profiles. The real user profile is
    /// used when null; tests pass an isolated directory.
    /// </summary>
    public string? NativeHome { get; init; }

    /// <summary>How provider secrets are protected on this machine, as shown to the user.</summary>
    public string SecretProtection { get; init; } = "";

    /// <summary>What shows notifications on this machine: the tray app, or a platform backend.</summary>
    public string DesktopSurface { get; init; } = "";

    /// <summary>True where toast corner, stack size, and lifetimes have an effect (the Windows app).</summary>
    public bool SupportsToastPlacement { get; init; }

    /// <summary>True where notification sounds are played (the Windows app).</summary>
    public bool SupportsSounds { get; init; }

    public RouterProxy? Router { get; init; }

    public RouterConfigService? RouterConfig { get; init; }

    /// <summary>
    /// Raised after the web UI saved configuration, on a thread-pool thread. The host applies
    /// anything it caches; values read live from the shared config object need nothing.
    /// </summary>
    public Action<AgentNotifyConfig, bool>? ConfigSaved { get; init; }
}
