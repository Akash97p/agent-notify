using System.Windows;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace AgentNotify.App;

public partial class SettingsWindow : Window
{
    private readonly AgentNotifyConfig _config;
    private readonly ConfigStore _store;
    private readonly Action<bool> _saved;
    private readonly int _originalPort;

    public SettingsWindow(AgentNotifyConfig config, ConfigStore store, Action<bool> saved)
    {
        InitializeComponent();
        _config = config;
        _store = store;
        _saved = saved;
        _originalPort = config.Port;
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
        SetDuration(NotificationType.Info, info); SetDuration(NotificationType.Success, success);
        SetDuration(NotificationType.Warning, warning); SetDuration(NotificationType.Completed, completed);
        SetDuration(NotificationType.Error, error); SetDuration(NotificationType.InputRequired, input);
        SetDuration(NotificationType.PermissionRequired, permission); SetDuration(NotificationType.Blocked, blocked);
        _store.Save(_config);
        _saved(port != _originalPort);
        Close();
    }

    private void SetDuration(NotificationType type, int seconds) => _config.ToastDurations[AgentNotifyConfig.EnumName(type)] = seconds;
    private bool TryDuration(WpfTextBox box, string label, out int value) => TryInt(box, 0, 86400, label + " duration", out value);
    private bool TryInt(WpfTextBox box, int min, int max, string label, out int value)
    {
        if (int.TryParse(box.Text, out value) && value >= min && value <= max) return true;
        StatusText.Text = $"{label} must be between {min} and {max}.";
        box.Focus(); box.SelectAll(); return false;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
