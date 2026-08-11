using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AgentNotify.Contracts;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Persistence;
using AgentNotify.Core.Services;
using AgentNotify.Core.Config;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace AgentNotify.App;

public partial class NotificationCenter : Window
{
    private readonly INotificationRepository _repository;
    private readonly NotificationService _service;
    private readonly AgentNotifyConfig _config;
    private bool _allowClose;

    public event Action<Notification>? StatusChanged;

    public NotificationCenter(INotificationRepository repository, NotificationService service, AgentNotifyConfig config)
    {
        _repository = repository;
        _service = service;
        _config = config;
        InitializeComponent();
        Icon = AppIcons.CreateWindowIcon();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IReadOnlyList<Notification> all;
        try
        {
            all = await _repository.QueryAsync(new NotificationQuery { Limit = 200 });
        }
        catch
        {
            return;
        }

        var active = all.Where(NeedsAttention).OrderByDescending(n => n.CreatedAt).ToList();
        var recent = all.Where(n => !NeedsAttention(n)).OrderByDescending(n => n.UpdatedAt).Take(60).ToList();

        BuildPanel(ActivePanel, active, showActions: true);
        BuildPanel(RecentPanel, recent, showActions: false);

        ActiveEmpty.Visibility = active.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveCountPill.Visibility = active.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveCountText.Text = active.Count.ToString();
        DismissAllButton.IsEnabled = active.Count > 0;
        Subtitle.Text = active.Count == 0
            ? "All caught up."
            : $"{active.Count} notification{(active.Count == 1 ? "" : "s")} need{(active.Count == 1 ? "s" : "")} attention";
    }

    private void BuildPanel(StackPanel panel, IReadOnlyList<Notification> items, bool showActions)
    {
        panel.Children.Clear();
        foreach (var n in items)
            panel.Children.Add(BuildRow(n, showActions));
    }

    private Border BuildRow(Notification n, bool showActions)
    {
        var accent = TypeVisuals.WpfColorFor(n.Type, _config);
        var accentBrush = new SolidColorBrush(accent);

        var outer = new Border
        {
            Margin = new Thickness(8, 4, 8, 4),
            Padding = new Thickness(10, 8, 8, 8),
            Background = new SolidColorBrush(WpfColor.FromRgb(0x20, 0x24, 0x31)),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x30, 0x44))
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (showActions)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new Border { Background = accentBrush, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 8, 2) };
        Grid.SetColumn(bar, 0);
        Grid.SetRowSpan(bar, 2);
        grid.Children.Add(bar);

        var header = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        header.Children.Add(new TextBlock
        {
            Text = TypeVisuals.LabelFor(n.Type, _config),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = accentBrush
        });
        header.Children.Add(new TextBlock { Text = $"  {n.Agent}", FontSize = 10, Foreground = new SolidColorBrush(WpfColor.FromRgb(0x9A, 0xA0, 0xB4)) });
        header.Children.Add(new TextBlock
        {
            Text = $"  {n.CreatedAt.ToLocalTime():g}",
            FontSize = 10,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x6B, 0x70, 0x89))
        });
        if (!showActions)
            header.Children.Add(new TextBlock
            {
                Text = $"  \u00b7 {n.Status}",
                FontSize = 10,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x6B, 0x70, 0x89))
            });
        Grid.SetColumn(header, 1);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = n.Title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = WpfBrushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(n.Message))
            body.Children.Add(new TextBlock
            {
                Text = n.Message,
                FontSize = 11,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0xC6, 0xCA, 0xDF)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        Grid.SetColumn(body, 1);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);

        if (showActions)
        {
            var actions = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var dismiss = GhostButton("Dismiss", "#9AA0B4", async (_, _) => { await ChangeStatus(n.Id, NotificationStatus.Dismissed); });
            var resolve = GhostButton("Resolve", "#2E9E5B", async (_, _) => { await ChangeStatus(n.Id, NotificationStatus.Resolved); });
            actions.Children.Add(dismiss);
            actions.Children.Add(resolve);
            Grid.SetColumn(actions, 2);
            Grid.SetRowSpan(actions, 2);
            Grid.SetRow(actions, 0);
            grid.Children.Add(actions);
        }

        outer.Child = grid;
        outer.ToolTip = string.IsNullOrWhiteSpace(n.Cwd) ? null : n.Cwd;
        return outer;
    }

    private System.Windows.Controls.Button GhostButton(string text, string color, RoutedEventHandler handler)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = text,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 11,
            Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!,
            Background = new SolidColorBrush(WpfColor.FromRgb(0x2A, 0x30, 0x44)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.Click += handler;
        return b;
    }

    private async Task ChangeStatus(string id, NotificationStatus status)
    {
        var result = await _service.UpdateStatusAsync(id, new UpdateNotificationRequest { Status = status });
        if (result.Value is not null)
            StatusChanged?.Invoke(result.Value);
        await RefreshAsync();
    }

    private async void OnDismissAllClick(object sender, RoutedEventArgs e)
    {
        var active = (await _repository.QueryAsync(new NotificationQuery { Unresolved = true, Limit = 500 }))
            .Where(NeedsAttention);
        foreach (var n in active)
        {
            var result = await _service.UpdateStatusAsync(n.Id, new UpdateNotificationRequest { Status = NotificationStatus.Dismissed });
            if (result.Value is not null)
                StatusChanged?.Invoke(result.Value);
        }
        await RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnCloseClick(object sender, RoutedEventArgs e) { Hide(); await Task.CompletedTask; }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _ = RefreshAsync();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    public static bool NeedsAttention(Notification n) =>
        n.Status == NotificationStatus.Active && NotificationTypes.IsAttention(n.Type);
}
