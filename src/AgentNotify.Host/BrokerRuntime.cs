using AgentNotify.Api;
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
    private IReadOnlyList<IOutboundChannelAdapter>? _adapters;
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

        var protector = SecretProtectorFactory.Create(_configStore.ConfigDir, _logger, out var protection);
        Protection = protection;

        var profiles = new ProviderProfileService(_deliveryRepository, protector);
        _adapters = ChannelAdapterFactory.CreateAll();
        _dispatcher = new DeliveryDispatcher(_deliveryRepository, profiles, _adapters, _logger);
        var coordinator = new NotificationDeliveryCoordinator(_deliveryRepository, _dispatcher.Signal);
        _dispatcher.Start();

        var service = new NotificationService(_repository, _config);

        var callbacks = new ApiCallbacks
        {
            PersistOutbound = coordinator.EnqueueAsync,
            Created = ShowOnDesktop,
            // There is no persistent surface to update without a UI; the notification center in a
            // future native client will subscribe here.
            Updated = _ => { }
        };

        _api = ApiHost.Build(_config, _repository, service, _logger, Url, callbacks);
        await _api.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.Info($"API listening on {Url}");

        await PruneHistoryAsync(cancellationToken).ConfigureAwait(false);
    }

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

        _logger.Info("agentnotifyd stopped");
        _logger.Dispose();
    }
}
