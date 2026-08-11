using System.Windows;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace AgentNotify.App;

public partial class SettingsWindow : Window
{
    public static string[] PriorityNames { get; } = ["Low", "Normal", "High", "Critical"];
    private readonly AgentNotifyConfig _config;
    private readonly ConfigStore _store;
    private readonly Action<bool> _saved;
    private readonly int _originalPort;
    private readonly List<NotificationTypeDefinition> _customTypes;

    public SettingsWindow(AgentNotifyConfig config, ConfigStore store, Action<bool> saved)
    {
        InitializeComponent();
        _config = config;
        _store = store;
        _saved = saved;
        _originalPort = config.Port;
        _customTypes = config.CustomNotificationTypes.Select(x => x.Clone()).ToList();
        LoadValues();
    }

    private void LoadValues()
    {
        PortBox.Text = _config.Port.ToString();
        RetentionBox.Text = _config.HistoryRetentionDays.ToString();
        MaxVisibleBox.Text = _config.MaxVisibleToasts.ToString();
        PauseBox.IsChecked = _config.PauseNotifications;
        DndBox.IsChecked = _config.DoNotDisturb;
        SoundsBox.IsChecked = _config.SoundsEnabled;
        LocationBox.SelectedIndex = string.Equals(_config.ToastLocation, "TopRight", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        InfoDuration.Text = Duration(NotificationType.Info);
        SuccessDuration.Text = Duration(NotificationType.Success);
        WarningDuration.Text = Duration(NotificationType.Warning);
        CompletedDuration.Text = Duration(NotificationType.Completed);
        ErrorDuration.Text = Duration(NotificationType.Error);
        InputDuration.Text = Duration(NotificationType.InputRequired);
        PermissionDuration.Text = Duration(NotificationType.PermissionRequired);
        BlockedDuration.Text = Duration(NotificationType.Blocked);
        RefreshCustomTypes();
    }

    private string Duration(NotificationType type) => _config.ToastDurationSeconds(type).ToString();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryInt(PortBox, 1, 65535, "Port", out var port) ||
            !TryInt(RetentionBox, 0, 3650, "Retention", out var retention) ||
            !TryInt(MaxVisibleBox, 1, 20, "Maximum visible toasts", out var visible) ||
            !TryDuration(InfoDuration, "Info", out var info) || !TryDuration(SuccessDuration, "Success", out var success) ||
            !TryDuration(WarningDuration, "Warning", out var warning) || !TryDuration(CompletedDuration, "Completed", out var completed) ||
            !TryDuration(ErrorDuration, "Error", out var error) || !TryDuration(InputDuration, "Input required", out var input) ||
            !TryDuration(PermissionDuration, "Permission", out var permission) || !TryDuration(BlockedDuration, "Blocked", out var blocked)) return;

        _config.Port = port;
        _config.HistoryRetentionDays = retention;
        _config.MaxVisibleToasts = visible;
        _config.PauseNotifications = PauseBox.IsChecked == true;
        _config.DoNotDisturb = DndBox.IsChecked == true;
        _config.SoundsEnabled = SoundsBox.IsChecked == true;
        _config.ToastLocation = LocationBox.SelectedIndex == 1 ? "TopRight" : "BottomRight";
        _config.CustomNotificationTypes = _customTypes.Select(x => x.Clone()).ToList();
        SetDuration(NotificationType.Info, info); SetDuration(NotificationType.Success, success);
        SetDuration(NotificationType.Warning, warning); SetDuration(NotificationType.Completed, completed);
        SetDuration(NotificationType.Error, error); SetDuration(NotificationType.InputRequired, input);
        SetDuration(NotificationType.PermissionRequired, permission); SetDuration(NotificationType.Blocked, blocked);
        _store.Save(_config);
        _saved(port != _originalPort);
        Close();
    }

    private void SetDuration(NotificationType type, int seconds) => _config.ToastDurations[AgentNotifyConfig.EnumName(type)] = seconds;

    private void RefreshCustomTypes(NotificationTypeDefinition? selected = null)
    {
        CustomTypesList.ItemsSource = null;
        CustomTypesList.ItemsSource = _customTypes.OrderBy(x => x.DisplayName).ToList();
        if (selected is not null) CustomTypesList.SelectedItem = CustomTypesList.Items.Cast<NotificationTypeDefinition>().FirstOrDefault(x => x.Id == selected.Id);
    }

    private void CustomType_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CustomTypesList.SelectedItem is not NotificationTypeDefinition item) return;
        CustomIdBox.Text = item.Id; CustomNameBox.Text = item.DisplayName; CustomColorBox.Text = item.AccentColor;
        CustomPriorityBox.SelectedItem = item.DefaultPriority.ToString(); CustomDurationBox.Text = item.DurationSeconds.ToString();
        CustomEnabledBox.IsChecked = item.Enabled;
    }

    private void NewType_Click(object sender, RoutedEventArgs e)
    {
        CustomTypesList.SelectedItem = null; CustomIdBox.Text = ""; CustomNameBox.Text = ""; CustomColorBox.Text = "#4A90D9";
        CustomPriorityBox.SelectedItem = "Normal"; CustomDurationBox.Text = "7"; CustomEnabledBox.IsChecked = true; CustomIdBox.Focus();
    }

    private void ApplyType_Click(object sender, RoutedEventArgs e)
    {
        var id = NotificationTypes.Normalize(CustomIdBox.Text);
        if (id is null || NotificationTypes.BuiltIns.Contains(id)) { StatusText.Text = "Enter a unique custom type ID."; return; }
        if (!int.TryParse(CustomDurationBox.Text, out var duration) || duration is < 0 or > 86400) { StatusText.Text = "Custom lifetime must be 0–86400."; return; }
        var color = CustomColorBox.Text.Trim();
        if (color.Length != 7 || color[0] != '#' || !color[1..].All(Uri.IsHexDigit)) { StatusText.Text = "Accent must use #RRGGBB."; return; }
        if (!Enum.TryParse<NotificationPriority>(CustomPriorityBox.SelectedItem?.ToString(), out var priority)) priority = NotificationPriority.Normal;
        var selected = CustomTypesList.SelectedItem as NotificationTypeDefinition;
        if (_customTypes.Any(x => x.Id == id && x != selected)) { StatusText.Text = "That custom type ID already exists."; return; }
        var item = selected ?? new NotificationTypeDefinition();
        item.Id = id; item.DisplayName = string.IsNullOrWhiteSpace(CustomNameBox.Text) ? id.Replace('_', ' ') : CustomNameBox.Text.Trim();
        item.AccentColor = color.ToUpperInvariant(); item.DefaultPriority = priority; item.DurationSeconds = duration; item.Enabled = CustomEnabledBox.IsChecked == true;
        if (selected is null) _customTypes.Add(item);
        StatusText.Text = ""; RefreshCustomTypes(item);
    }

    private void DeleteType_Click(object sender, RoutedEventArgs e)
    {
        if (CustomTypesList.SelectedItem is not NotificationTypeDefinition item) return;
        _customTypes.Remove(item); RefreshCustomTypes(); NewType_Click(sender, e);
    }
    private bool TryDuration(WpfTextBox box, string label, out int value) => TryInt(box, 0, 86400, label + " duration", out value);
    private bool TryInt(WpfTextBox box, int min, int max, string label, out int value)
    {
        if (int.TryParse(box.Text, out value) && value >= min && value <= max) return true;
        StatusText.Text = $"{label} must be between {min} and {max}.";
        box.Focus(); box.SelectAll(); return false;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
