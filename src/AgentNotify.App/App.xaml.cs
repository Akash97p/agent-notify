using System.IO;
using System.Windows;
using System.Windows.Threading;
using AgentNotify.Api;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Persistence;
using AgentNotify.Core.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace AgentNotify.App;

public partial class App : System.Windows.Application
{
    private SingleInstance? _singleInstance;
    private ConfigStore? _configStore;
    private AgentNotifyConfig _config = null!;
    private FileLogger _logger = null!;
    private SqliteNotificationRepository _repository = null!;
    private NotificationService _service = null!;
    private WebApplication? _api;
    private ApiCallbacks? _apiCallbacks;
    private TrayIcon? _tray;
    private ToastStackManager? _toasts;
    private NotificationCenter? _center;
    private SettingsWindow? _settings;
    private NotificationSoundService? _sounds;
    private SqliteDeliveryRepository _deliveryRepository = null!;
    private ProviderProfileService _providerProfiles = null!;
    private DeliveryDispatcher? _deliveryDispatcher;
    private NotificationDeliveryCoordinator? _deliveryCoordinator;
    private WebhookChannelAdapter? _webhookAdapter;
    private DeliveryRouteService _deliveryRoutes = null!;
    private DispatcherTimer? _pruneTimer;
    private bool _showCenterRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Thread.CurrentThread.Name = "AgentNotify UI";
        var chosen = Array.Exists(e.Args, a => string.Equals(a, "--show-center", StringComparison.OrdinalIgnoreCase));

        // Single-instance: if already running, signal the owner and exit.
        _singleInstance = SingleInstance.TryAcquire();
        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.SignalShowCenter();
            Shutdown(0);
            return;
        }

        // Spin a background waiter so the second-process signal brings the center up.
        var waiter = new Thread(() =>
        {
            while (true)
            {
                var signaled = _singleInstance.WaitForShowCenter();
                if (!signaled) break;
                try
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_center is null) _showCenterRequested = true;
                        else _center.ShowAndActivate();
                    });
                }
                catch (TaskCanceledException) { break; }
            }
        })
        { IsBackground = true, Name = "AgentNotify SingleInstance waiter" };
        waiter.Start();

        InitializeInBackground(chosen);
    }

    private void InitializeInBackground(bool showCenter)
    {
        // Do the blocking I/O off the UI thread so startup stays snappy.
        Task.Run(async () =>
        {
            try
            {
                await InitializeCoreAsync();
                Dispatcher.Invoke(() =>
                {
                    WireUi();
                    if (showCenter || _showCenterRequested) _center?.ShowAndActivate();
                });
            }
            catch (Exception ex)
            {
                _logger?.Error("Initialization failed", ex);
                Dispatcher.Invoke(() => Shutdown(1));
            }
        });
    }

    private async Task InitializeCoreAsync()
    {
        _configStore = new ConfigStore();
        _config = _configStore.Load();
        _configStore.EnsureAuthToken(_config);
        _configStore.Save(_config);

        _logger = new FileLogger(_configStore.LogsDir);
        _logger.Info($"AgentNotify starting: port={_config.Port} center={(_configStore.ConfigPath)}");

        _repository = new SqliteNotificationRepository(_configStore.DbPath);
        await _repository.InitializeAsync();
        _deliveryRepository = new SqliteDeliveryRepository(_configStore.DbPath);
        await _deliveryRepository.InitializeAsync();
        _providerProfiles = new ProviderProfileService(_deliveryRepository, new DpapiSecretProtector());
        _deliveryRoutes = new DeliveryRouteService(_deliveryRepository);
        _webhookAdapter = new WebhookChannelAdapter();
        _deliveryDispatcher = new DeliveryDispatcher(
            _deliveryRepository,
            _providerProfiles,
            [_webhookAdapter],
            _logger);
        _deliveryCoordinator = new NotificationDeliveryCoordinator(
            _deliveryRepository,
            _deliveryDispatcher.Signal);
        _deliveryDispatcher.Start();

        _service = new NotificationService(_repository, _config);

        var url = $"http://127.0.0.1:{_config.Port}";
        _apiCallbacks = new ApiCallbacks
        {
            PersistOutbound = _deliveryCoordinator.EnqueueAsync,
            Created = n =>
            {
                _toasts?.Show(n);
                _ = Dispatcher.InvokeAsync(() =>
                {
                    _sounds?.PlayFor(n.Type, n.Priority);
                    _ = _center?.RefreshAsync();
                });
            },
            Updated = n =>
            {
                _toasts?.Update(n);
                _ = Dispatcher.InvokeAsync(() => _center?.RefreshAsync());
            }
        };
        _api = ApiHost.Build(_config, _repository, _service, _logger, url, _apiCallbacks);
        _api.Start();
        _logger.Info($"API listening on {url}");

        // Honor persisted startup preference (registry is source of truth; reconcile config).
        var registryStartup = StartupRegistrar.IsEnabled();
        if (registryStartup != _config.LaunchAtStartup)
        {
            _config.LaunchAtStartup = registryStartup;
            _configStore.Save(_config);
        }

        // Periodic prune of resolved/dismissed history.
        _ = Task.Run(async () =>
        {
            var retention = TimeSpan.FromDays(Math.Max(1, _config.HistoryRetentionDays));
            var cutoff = DateTimeOffset.UtcNow - retention;
            var pruned = await _repository.PruneAsync(cutoff);
            if (pruned > 0) _logger.Info($"Pruned {pruned} old notification(s)");
        });
    }

    private void WireUi()
    {
        _sounds = new NotificationSoundService(_config, _configStore!.SoundsDir, _logger);
        _toasts = new ToastStackManager(_config, _logger, Dispatcher);
        _toasts.DismissRequested += id =>
        {
            _ = Task.Run(async () =>
            {
                var result = await _service.UpdateStatusAsync(id, new UpdateNotificationRequest { Status = NotificationStatus.Dismissed });
                if (result.Value is not null)
                    _toasts.Update(result.Value);
                _ = Dispatcher.InvokeAsync(() => _center?.RefreshAsync());
            });
        };
        _toasts.ToastClicked += _ => _center?.ShowAndActivate();

        _center = new NotificationCenter(_repository, _service, _config);
        _center.StatusChanged += n => _toasts.Update(n);

        _tray = new TrayIcon(
            AppIcons.CreateTrayIcon(),
            _configStore!.LogsDir,
            isPaused: () => _config.PauseNotifications,
            isStartupEnabled: StartupRegistrar.IsEnabled,
            onShowCenter: () => _center.ShowAndActivate(),
            onOpenSettings: ShowSettings,
            onOpenGettingStarted: () => RunTrayAction("Getting started", AgentResources.OpenGettingStarted),
            onCopySkill: () => RunTrayAction("Agent skill", () =>
            {
                System.Windows.Clipboard.SetText(AgentResources.SkillText);
                _tray?.ShowMessage("AgentNotify", "SKILL.md copied to the clipboard.");
            }),
            onSaveSkill: SaveSkill,
            onTogglePause: () =>
            {
                _config.PauseNotifications = !_config.PauseNotifications;
                _configStore.Save(_config);
                _logger.Info($"Pause toggled -> {_config.PauseNotifications}");
            },
            onToggleStartup: () =>
            {
                var next = !StartupRegistrar.IsEnabled();
                StartupRegistrar.Set(next);
                _config.LaunchAtStartup = next;
                _configStore.Save(_config);
                _logger.Info($"Startup toggled -> {next}");
            },
            onExit: () =>
            {
                _center.CloseForExit();
                Shutdown(0);
            });

        _ = RestoreAttentionToastsAsync();

        _pruneTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _pruneTimer.Tick += async (_, _) =>
        {
            var retention = TimeSpan.FromDays(Math.Max(1, _config.HistoryRetentionDays));
            var pruned = await _repository.PruneAsync(DateTimeOffset.UtcNow - retention);
            if (pruned > 0) _logger.Info($"Hourly prune: {pruned}");
        };
        _pruneTimer.Start();
    }

    private void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(
                _config,
                _configStore!,
                _sounds!,
                _providerProfiles,
                _deliveryRoutes,
                _deliveryDispatcher!,
                restartRequired =>
            {
                _logger.Info("Settings updated");
                _tray?.ShowMessage("AgentNotify", restartRequired
                    ? "Settings saved. Restart AgentNotify to apply the new API port."
                    : "Settings saved.");
            });
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Show();
        _settings.Activate();
    }

    private async Task RestoreAttentionToastsAsync()
    {
        try
        {
            var active = await _repository.QueryAsync(new NotificationQuery { Unresolved = true, Limit = 500 });
            foreach (var notification in active.Where(NotificationCenter.NeedsAttention).OrderBy(n => n.CreatedAt))
                _toasts?.Show(notification);
        }
        catch (Exception ex)
        {
            _logger.Error("Could not restore attention notifications", ex);
        }
    }

    private void SaveSkill()
    {
        RunTrayAction("Save agent skill", () =>
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save AgentNotify skill",
                FileName = "SKILL.md",
                DefaultExt = ".md",
                Filter = "Markdown file (*.md)|*.md|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(downloads) ? downloads : null
            };
            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, AgentResources.SkillText);
                _tray?.ShowMessage("AgentNotify", $"Saved {Path.GetFileName(dialog.FileName)}.");
            }
        });
    }

    private void RunTrayAction(string operation, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            _logger.Error($"{operation} failed", ex);
            _tray?.ShowMessage("AgentNotify", $"{operation} failed. See the log for details.",
                System.Windows.Forms.ToolTipIcon.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pruneTimer?.Stop();
        _toasts?.Dispose();
        _sounds?.Dispose();
        _settings?.Close();
        _tray?.Dispose();
        try { _api?.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { (_api as IDisposable)?.Dispose(); } catch { }
        try { _deliveryDispatcher?.StopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        _webhookAdapter?.Dispose();
        _logger?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
