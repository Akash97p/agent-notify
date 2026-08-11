using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;
using AgentNotify.Core.Domain;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AgentNotify.App;

/// <summary>Borderless, focus-safe toast. Clicking the body opens the Notification Center;
/// the X dismisses via the API. Closing is visual only unless DismissRequested is raised.</summary>
public partial class ToastWindow : Window
{
    private readonly AgentNotifyConfig _config;
    private DispatcherTimer? _autoClose;
    private bool _isClosing;

    public string NotificationId { get; }

    /// <summary>Raised when the user asks to dismiss (X button).</summary>
    public event Action? DismissRequested;

    /// <summary>Raised when the toast body is clicked.</summary>
    public event Action? OpenCenterRequested;

    public ToastWindow(Notification notification, AgentNotifyConfig config, Action? onClosed)
    {
        NotificationId = notification.Id;
        _config = config;
        InitializeComponent();
        Closed += (_, _) => onClosed?.Invoke();
        Icon = AppIcons.CreateWindowIcon();
        Apply(notification);
        ResetTimer(notification.Type);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
    }

    public void Refresh(Notification notification)
    {
        Apply(notification);
        ResetTimer(notification.Type);
    }

    private void Apply(Notification notification)
    {
        var accent = TypeVisuals.WpfColorFor(notification.Type, _config);
        var brush = new SolidColorBrush(accent);
        Root.Tag = brush;
        Root.BorderBrush = brush;
        TypeLabel.Text = TypeVisuals.LabelFor(notification.Type, _config);
        TitleText.Text = notification.Title;
        MessageText.Text = notification.Message;
        AgentText.Text = notification.Agent;
        TimeText.Text = notification.CreatedAt.ToLocalTime().ToString("HH:mm");
    }

    private void ResetTimer(string type)
    {
        _autoClose?.Stop();
        _autoClose = null;
        var seconds = _config.ToastDurationSeconds(type);
        if (seconds > 0)
        {
            _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _autoClose.Tick += (_, _) =>
            {
                _autoClose?.Stop();
                DismissRequested?.Invoke();
                BeginFadeOut(openCenter: false);
            };
            _autoClose.Start();
        }
    }

    public void CloseVisualOnly() => BeginFadeOut(openCenter: false);

    private void OnBodyClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        BeginFadeOut(openCenter: true);
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DismissRequested?.Invoke();
        BeginFadeOut(openCenter: false);
    }

    private void BeginFadeOut(bool openCenter)
    {
        if (_isClosing)
            return;
        _isClosing = true;
        _autoClose?.Stop();
        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(220)
        };
        animation.Completed += (_, _) =>
        {
            if (openCenter)
                OpenCenterRequested?.Invoke();
            Close();
        };
        BeginAnimation(OpacityProperty, animation);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
                return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newStyle);
}
