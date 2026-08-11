using System.Windows;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Logging;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace AgentNotify.App;

/// <summary>Centralizes toast positioning, stacking, and lifetime.
/// All visual state (visible windows + auto-dismiss timers) lives here.</summary>
public sealed class ToastStackManager : IDisposable
{
    private const double MarginPx = 16;
    private const double SpacingPx = 10;

    private readonly AgentNotifyConfig _config;
    private readonly IAppLogger _logger;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly List<ToastWindow> _toasts = [];
    private readonly List<Notification> _pending = [];
    private string? _targetScreenName;
    private uint _targetDpi = 96;
    private bool _disposed;

    /// <summary>Raised when the user clicks the X. The host should dismiss via the API.</summary>
    public event Action<string>? DismissRequested;

    /// <summary>Raised when the toast body is clicked. The host should show the center.</summary>
    public event Action<ToastWindow>? ToastClicked;

    public ToastStackManager(AgentNotifyConfig config, IAppLogger logger,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        _config = config;
        _logger = logger;
        _dispatcher = dispatcher;
    }

    /// <summary>Shows a toast for a newly created notification, or refreshes an existing one.</summary>
    public void Show(Notification notification)
    {
        if (_config.PauseNotifications)
        {
            _logger.Info($"[Toast] paused, not showing {notification.Id} {notification.Title}");
            return;
        }
        Dispatch(() => ShowCore(notification));
    }

    /// <summary>Called after an API update, deduplication update, or local status change.
    /// Either refreshes the toast or dismisses it.</summary>
    public void Update(Notification notification)
    {
        Dispatch(() =>
        {
            var match = _toasts.FirstOrDefault(t => t.NotificationId == notification.Id);
            if (match is null)
            {
                var pendingIndex = _pending.FindIndex(n => n.Id == notification.Id);
                if (pendingIndex >= 0)
                {
                    if (notification.Status == NotificationStatus.Active)
                        _pending[pendingIndex] = notification;
                    else
                        _pending.RemoveAt(pendingIndex);
                }
                return;
            }

            if (notification.Status != NotificationStatus.Active)
            {
                _logger.Info($"[Toast] notification {notification.Id} now {notification.Status}, closing toast");
                RemoveWindow(match);
                return;
            }

            match.Refresh(notification);
            PositionAll();
        });
    }

    /// <summary>Closes the toast that belongs to this id when it is resolved/dismissed elsewhere.</summary>
    public void RemoveById(string id)
    {
        Dispatch(() =>
        {
            var match = _toasts.FirstOrDefault(t => t.NotificationId == id);
            if (match is not null)
                RemoveWindow(match);
        });
    }

    private void ShowCore(Notification notification)
    {
        var existing = _toasts.FirstOrDefault(t => t.NotificationId == notification.Id);
        if (existing is not null)
        {
            existing.Refresh(notification);
            PositionAll();
            return;
        }

        var pendingIndex = _pending.FindIndex(n => n.Id == notification.Id);
        if (pendingIndex >= 0)
        {
            _pending[pendingIndex] = notification;
            return;
        }

        if (_toasts.Count >= _config.MaxVisibleToasts)
        {
            _pending.Add(notification);
            _logger.Info($"[Toast] max visible ({_config.MaxVisibleToasts}) reached, queued {notification.Id}");
            return;
        }

        ToastWindow window = new(notification, _config, onClosed: () =>
        {
            // window closed without reaching here if removed via RemoveWindow
        });

        window.DismissRequested += () => DismissRequested?.Invoke(notification.Id);
        window.OpenCenterRequested += () => ToastClicked?.Invoke(window);
        window.Closed += (_, _) =>
        {
            if (_toasts.Remove(window))
            {
                PositionAll();
                ShowNextPending();
            }
        };

        _toasts.Add(window);
        window.Opacity = 1.0;
        window.Show();
        window.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180)));
        PositionAll();
    }

    private void ShowNextPending()
    {
        while (_toasts.Count < _config.MaxVisibleToasts && _pending.Count > 0)
        {
            var next = _pending[0];
            _pending.RemoveAt(0);
            if (next.Status == NotificationStatus.Active)
                ShowCore(next);
        }

        if (_toasts.Count == 0)
            _targetScreenName = null;
    }

    private void RemoveWindow(ToastWindow window)
    {
        window.Close();
        // Closed handler in ShowCore will PositionAll; call again defensively
    }

    private void PositionAll()
    {
        if (_toasts.Count == 0)
            return;

        WinForms.Screen screen;
        if (_targetScreenName is null)
        {
            var foreground = GetForegroundWindow();
            screen = WinForms.Screen.FromHandle(foreground);
            _targetScreenName = screen.DeviceName;
            _targetDpi = foreground != IntPtr.Zero ? GetDpiForWindow(foreground) : 96u;
            if (_targetDpi == 0) _targetDpi = 96;
        }
        else
        {
            screen = WinForms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _targetScreenName)
                ?? WinForms.Screen.PrimaryScreen!;
        }
        var toDip = 96d / _targetDpi;
        var pixels = screen.WorkingArea;
        var workTop = pixels.Top * toDip;
        var workRight = pixels.Right * toDip;
        var workBottom = pixels.Bottom * toDip;
        var atTop = string.Equals(_config.ToastLocation, "TopRight", StringComparison.OrdinalIgnoreCase);
        var spacing = SpacingPx;
        var margin = MarginPx;

        // Use enqueued order: newest at bottom. Bottom toast anchors to workArea.Bottom.
        // Work path: bottom toast -> up, or top -> down.
        if (atTop)
        {
            double y = workTop + margin;
            foreach (var t in _toasts.Reverse<ToastWindow>())
            {
                t.UpdateLayout();
                var height = t.ActualHeight > 1 ? t.ActualHeight : 110;
                var width = t.Width is > 1 ? t.Width : 360;
                t.Left = workRight - margin - width;
                t.Top = y;
                y += height + spacing;
            }
        }
        else
        {
            double y = workBottom - margin;
            foreach (var t in _toasts.AsEnumerable().Reverse())
            {
                t.UpdateLayout();
                var height = t.ActualHeight > 1 ? t.ActualHeight : 110;
                var width = t.Width is > 1 ? t.Width : 360;
                y -= height;
                t.Left = workRight - margin - width;
                t.Top = y;
                y -= spacing;
            }
        }
    }

    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var t in _toasts.ToList())
            try { t.Close(); } catch { }
        _toasts.Clear();
        _pending.Clear();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
