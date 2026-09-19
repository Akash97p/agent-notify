using AgentNotify.Api;
using AgentNotify.Api.WebUi;
using AgentNotify.Core;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Persistence;
using AgentNotify.Core.Services;
using AgentNotify.Desktop;
using Microsoft.AspNetCore.Builder;

namespace AgentNotify.Host;

/// <summary>
/// Composes and runs the AgentNotify broker without a desktop UI framework.
/// </summary>
/// <remarks>
/// This is the same broker the Windows tray process hosts — config, SQLite history, the durable
/// delivery outbox, and the loopback API — with the WPF toast stack replaced by an
/// <see cref="IDesktopNotifier"/>. Agents cannot tell the two apart: the CLI, the bearer token, and
/// the <c>/v1</c> contract are identical.
/// </remarks>
public sealed class BrokerRuntime : IAsyncDisposable
{
    private readonly ConfigStore _configStore;
    private readonly AgentNotifyConfig _config;
    private readonly FileLogger _logger;
    private readonly IDesktopNotifier _notifier;

    private SqliteNotificationRepository? _repository;
    private SqliteDeliveryRepository? _deliveryRepository;
    private DeliveryDispatcher? _dispatcher;
    private InteractionResponsePoller? _interactionResponsePoller;
    private IReadOnlyList<IOutboundChannelAdapter>? _adapters;
    private AgentNotify.Core.Billing.BillingService? _billingService;
    private AgentNotify.Core.Router.RouterRepository? _routerRepository;
    private AgentNotify.Core.Router.RouterConfigService? _routerConfigService;
    private AgentNotify.Core.Router.RouterProxy? _routerProxy;
    private AgentNotify.Core.Router.RouterLedgerPruner? _routerPruner;
    private AgentNotify.Core.Router.Connect.RouterConnectService? _routerConnect;
    private MacMenuBarController? _macMenuBar;
    private WebApplication? _api;

    private BrokerRuntime(
        ConfigStore configStore,
        AgentNotifyConfig config,
        FileLogger logger,
        IDesktopNotifier notifier)
    {
        _configStore = configStore;
        _config = config;
        _logger = logger;
        _notifier = notifier;
    }

    /// <summary>The loopback base address the API listens on.</summary>
    public string Url => $"http://127.0.0.1:{_config.Port}";

    /// <summary>How provider secrets are protected on this machine.</summary>
    public SecretProtection Protection { get; private set; } = new("none", "not initialized", IsUserBound: false);

    /// <summary>The desktop notification backend in use.</summary>
    public string NotifierName => _notifier.Name;

    /// <summary>Starts the broker: storage, delivery dispatch, and the loopback API.</summary>
    public static async Task<BrokerRuntime> StartAsync(
        string? configDir = null,
        int? portOverride = null,
        bool desktopNotifications = true,
        CancellationToken cancellationToken = default)
    {
        var configStore = new ConfigStore(configDir, applyEnvOverrides: true);
        var config = configStore.Load();
        if (portOverride is { } port) config.Port = port;
        configStore.EnsureAuthToken(config);
        configStore.Save(config);

        UnixFilePermissions.CreateOwnerOnlyDirectory(configStore.LogsDir);
        var logger = new FileLogger(configStore.LogsDir);

        var notifier = desktopNotifications
            ? DesktopNotifierFactory.Create(logger)
            : new ConsoleDesktopNotifier();

        var runtime = new BrokerRuntime(configStore, config, logger, notifier);
        try
        {
            await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.Info($"agentnotifyd starting: port={_config.Port} config={_configStore.ConfigPath}");

        _repository = new SqliteNotificationRepository(_configStore.DbPath);
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _deliveryRepository = new SqliteDeliveryRepository(_configStore.DbPath);
        await _deliveryRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var interactionRepository = new SqliteInteractionRepository(_configStore.DbPath);
        await interactionRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var interactionService = new InteractionService(interactionRepository);

        var protector = SecretProtectorFactory.Create(_configStore.ConfigDir, _logger, out var protection);
        Protection = protection;

        var billingRepository = new AgentNotify.Core.Billing.BillingAccountRepository(_configStore.DbPath);
        await billingRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var billingService = new AgentNotify.Core.Billing.BillingService(billingRepository, protector);
        _billingService = billingService;

        var routerRepository = new AgentNotify.Core.Router.RouterRepository(_configStore.DbPath);
        await routerRepository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var routerConfigService = new AgentNotify.Core.Router.RouterConfigService(routerRepository, protector, _configStore, _config);
        var routerProxy = new AgentNotify.Core.Router.RouterProxy(routerConfigService, routerRepository, _logger);
        // The router spends through the same keys the API accounts list holds, so a key is entered once.
        routerProxy.Credentials.ApiAccountKey = billingService.GetKeyAsync;
        var routerPruner = new AgentNotify.Core.Router.RouterLedgerPruner(routerRepository, _config, TimeProvider.System, _logger);
        routerPruner.Start();
        var routerConnect = new AgentNotify.Core.Router.Connect.RouterConnectService(
            routerConfigService,
            Path.Combine(_configStore.ConfigDir, "router"),
            () => _config.Port,
            // The agents' configuration normally lives under the broker user's home. An override
            // exists so a second profile — or a check like the one in VERIFICATION.md — can be
            // pointed at a throwaway home instead of the owner's real Codex and Claude Code setup.
            Environment.GetEnvironmentVariable("AGENTNOTIFY_AGENT_HOME"),
            profiles: AgentProfiles(Environment.GetEnvironmentVariable("AGENTNOTIFY_AGENT_HOME")));
        // A connected agent's generated catalogue and embedded key follow the router's configuration,
        // so they are rewritten whenever it changes rather than going stale until the next connect.
        routerConfigService.Changed = () => _ = Task.Run(async () =>
        {
            try { await routerConnect.RefreshAsync().ConfigureAwait(false); }
            catch (Exception exception) { _logger.Warn($"Refreshing connected agents failed: {exception.Message}"); }
        });
        _routerRepository = routerRepository;
        _routerConfigService = routerConfigService;
        _routerProxy = routerProxy;
        _routerPruner = routerPruner;
        _routerConnect = routerConnect;

        var profiles = new ProviderProfileService(_deliveryRepository, protector);
        _adapters = ChannelAdapterFactory.CreateAll();
        _dispatcher = new DeliveryDispatcher(_deliveryRepository, profiles, _adapters, _logger);
        var coordinator = new NotificationDeliveryCoordinator(_deliveryRepository, _dispatcher.Signal);
        _dispatcher.Start();

        var interactionPublisher = new InteractionRelayPublisher(_deliveryRepository, _dispatcher.Signal);
        _interactionResponsePoller = new InteractionResponsePoller(
            profiles,
            new RelayCursorStore(_configStore.ConfigDir),
            Url,
            _config.AuthToken,
            _logger);

        var service = new NotificationService(_repository, _config);

        var callbacks = new ApiCallbacks
        {
            PersistOutbound = coordinator.EnqueueAsync,
            Created = ShowOnDesktop,
            // There is no persistent surface to update without a UI; the notification center in a
            // future native client will subscribe here.
            Updated = _ => { },
            InteractionCreated = async (interaction, ct) =>
            {
                await interactionPublisher.PublishAsync(DtoMapper.ToDto(interaction), ct);
            }
        };

        _macMenuBar = new MacMenuBarController(_config, _logger);
        var webUi = new WebUiOptions
        {
            ConfigStore = _configStore,
            Providers = profiles,
            Routes = new DeliveryRouteService(_deliveryRepository),
            Dispatcher = _dispatcher,
            Billing = billingService,
            Router = routerProxy,
            RouterConfig = routerConfigService,
            RouterConnect = routerConnect,
            SecretProtection = protection.Description,
            DesktopSurface = DesktopSurfaceName(_notifier.Name),
            // Toast placement and sounds belong to the Windows tray app; the portable broker hands
            // notifications to the platform, which decides both.
            SupportsToastPlacement = false,
            SupportsSounds = false,
            ConfigSaved = (_, _) => _macMenuBar.Apply()
        };

        _api = ApiHost.Build(_config, _repository, service, _logger, Url, callbacks, interactionService, interactionPublisher, webUi);
        await _api.StartAsync(cancellationToken).ConfigureAwait(false);
        _interactionResponsePoller.Start();
        _logger.Info($"API listening on {Url}");
        _macMenuBar.Apply();

        await PruneHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The web interface address.</summary>
    public string WebUiUrl => WebUiEndpoints.Url(_config.Port);

    private static string DesktopSurfaceName(string notifier) => notifier switch
    {
        "console" => "the broker's console output (desktop notifications are off)",
        "osascript" or "terminal-notifier" => $"macOS Notification Center ({notifier})",
        _ => $"the desktop notification service ({notifier})"
    };

    /// <summary>
    /// Fires the desktop notification without blocking the API response. Local persistence has
    /// already succeeded at this point, so a notifier failure is logged and nothing more.
    /// </summary>
    private void ShowOnDesktop(Notification notification)
    {
        if (_config.PauseNotifications) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var lifetime = new NotificationLifetime(_config.ToastDurationSeconds(notification.Type));
                var shown = await _notifier.ShowAsync(notification, lifetime).ConfigureAwait(false);
                if (!shown)
                    _logger.Warn($"Desktop backend '{_notifier.Name}' did not display notification {notification.Id}.");
            }
            catch (Exception exception)
            {
                _logger.Error("Desktop notification failed", exception);
            }
        });
    }

    private async Task PruneHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var retention = TimeSpan.FromDays(Math.Max(1, _config.HistoryRetentionDays));
            var pruned = await _repository!
                .PruneAsync(DateTimeOffset.UtcNow - retention, cancellationToken)
                .ConfigureAwait(false);
            if (pruned > 0) _logger.Info($"Pruned {pruned} old notification(s)");
        }
        catch (Exception exception)
        {
            _logger.Error("History pruning failed", exception);
        }
    }

    /// <summary>
    /// Stops the API and the delivery dispatcher.
    /// </summary>
    /// <remarks>
    /// Every step is bounded. A dispatcher waiting on a slow provider must not stop the process
    /// from exiting: a daemon that ignores SIGTERM cannot be stopped by a service manager. An
    /// interrupted delivery is safe — the outbox claim is recovered on the next start.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try { _macMenuBar?.Dispose(); } catch { }

        if (_interactionResponsePoller is not null)
        {
            try
            {
                await _interactionResponsePoller.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Warn("Relay interaction response poller did not stop within 5 seconds; exiting anyway.");
            }
            catch { }
        }

        if (_api is not null)
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _api.StopAsync(stopTimeout.Token).ConfigureAwait(false); } catch { }
            try { await _api.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        if (_dispatcher is not null)
        {
            try
            {
                await _dispatcher.StopAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.Warn("Delivery dispatcher did not stop within 5 seconds; exiting anyway.");
            }
            catch { }
        }

        if (_adapters is not null)
        {
            foreach (var adapter in _adapters)
                try { (adapter as IDisposable)?.Dispose(); } catch { }
        }

        try { _billingService?.Dispose(); } catch { }
        try { _routerPruner?.Dispose(); } catch { }
        try { _routerProxy?.Dispose(); } catch { }

        _logger.Info("agentnotifyd stopped");
        _logger.Dispose();
    }

    /// <summary>
    /// The Codex and Claude Code accounts the router can connect: the same list Live quota monitors, so
    /// a second account added or discovered there can be connected too. A throwaway agent home (the
    /// override used for checks) offers only its own two built-in profiles.
    /// </summary>
    private Func<IReadOnlyList<AgentNotify.Core.Router.Connect.RouterAgentProfile>> AgentProfiles(string? agentHome)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(agentHome))
            return () => AgentNotify.Core.Router.Connect.RouterAgentProfile.FromAccounts([], agentHome);
        return () => AgentNotify.Core.Router.Connect.RouterAgentProfile.FromAccounts(
            AgentNotify.Core.Config.QuotaAccountDefinition.Monitored(_config, AgentNotify.Core.Wsl.WslDiscovery.Default, home), home);
    }
}
