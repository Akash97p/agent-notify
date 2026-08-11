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
    private readonly NotificationSoundService _sounds;
    private readonly Action<bool> _saved;
    private readonly int _originalPort;
    private readonly List<NotificationTypeDefinition> _customTypes;
    private string? _defaultSound;
    private readonly Dictionary<string, string> _typeSounds;

    public SettingsWindow(AgentNotifyConfig config, ConfigStore store, NotificationSoundService sounds, Action<bool> saved)
    {
        InitializeComponent();
        _config = config;
        _store = store;
        _sounds = sounds;
        _saved = saved;
        _originalPort = config.Port;
        _customTypes = config.CustomNotificationTypes.Select(x => x.Clone()).ToList();
        _defaultSound = config.DefaultSoundFile;
        _typeSounds = new Dictionary<string, string>(config.TypeSoundFiles, StringComparer.OrdinalIgnoreCase);
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
        SoundVolumeBox.Text = Math.Round(_config.SoundVolume * 100).ToString();
        CriticalSoundDndBox.IsChecked = _config.PlayCriticalSoundsDuringDoNotDisturb;
        GlobalSoundBox.Text = _defaultSound ?? "No global sound selected";
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
        RefreshSoundTypes();
    }

    private string Duration(NotificationType type) => _config.ToastDurationSeconds(type).ToString();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryInt(PortBox, 1, 65535, "Port", out var port) ||
            !TryInt(RetentionBox, 0, 3650, "Retention", out var retention) ||
            !TryInt(MaxVisibleBox, 1, 20, "Maximum visible toasts", out var visible) ||
            !TryInt(SoundVolumeBox, 0, 100, "Sound volume", out var soundVolume) ||
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
        _config.SoundVolume = soundVolume / 100d;
        _config.PlayCriticalSoundsDuringDoNotDisturb = CriticalSoundDndBox.IsChecked == true;
        _config.DefaultSoundFile = _defaultSound;
        _config.TypeSoundFiles = new Dictionary<string, string>(_typeSounds, StringComparer.OrdinalIgnoreCase);
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
        var previousId = selected?.Id;
        item.Id = id; item.DisplayName = string.IsNullOrWhiteSpace(CustomNameBox.Text) ? id.Replace('_', ' ') : CustomNameBox.Text.Trim();
        item.AccentColor = color.ToUpperInvariant(); item.DefaultPriority = priority; item.DurationSeconds = duration; item.Enabled = CustomEnabledBox.IsChecked == true;
        if (selected is null) _customTypes.Add(item);
        else if (previousId != id && previousId is not null && _typeSounds.Remove(previousId, out var previousSound)) _typeSounds[id] = previousSound;
        StatusText.Text = ""; RefreshCustomTypes(item); RefreshSoundTypes(item.Id);
    }

    private void DeleteType_Click(object sender, RoutedEventArgs e)
    {
        if (CustomTypesList.SelectedItem is not NotificationTypeDefinition item) return;
        _customTypes.Remove(item); _typeSounds.Remove(item.Id); RefreshCustomTypes(); RefreshSoundTypes(); NewType_Click(sender, e);
    }

    private void RefreshSoundTypes(string? select = null)
    {
        select ??= SelectedSoundType;
        SoundTypeBox.ItemsSource = NotificationTypes.BuiltIns.Concat(_customTypes.Select(x => x.Id)).Distinct().OrderBy(x => x).ToList();
        SoundTypeBox.SelectedItem = select;
        if (SoundTypeBox.SelectedIndex < 0) SoundTypeBox.SelectedIndex = 0;
    }

    private string? ChooseAndImportSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose notification sound", Filter = "Audio files (*.wav;*.mp3)|*.wav;*.mp3|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return null;
        try { StatusText.Text = ""; return _sounds.Import(dialog.FileName); }
        catch (Exception ex) { StatusText.Text = ex.Message; return null; }
    }

    private void ChooseGlobalSound_Click(object sender, RoutedEventArgs e)
    {
        var file = ChooseAndImportSound(); if (file is null) return; _defaultSound = file; GlobalSoundBox.Text = file;
    }
    private void PreviewGlobalSound_Click(object sender, RoutedEventArgs e) => _sounds.Preview(_defaultSound);
    private void ClearGlobalSound_Click(object sender, RoutedEventArgs e) { _defaultSound = null; GlobalSoundBox.Text = "No global sound selected"; }
    private string? SelectedSoundType => SoundTypeBox.SelectedItem?.ToString();
    private void SoundType_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var type = SelectedSoundType; TypeSoundBox.Text = type is not null && _typeSounds.TryGetValue(type, out var file) ? file : "Using global sound";
    }
    private void ChooseTypeSound_Click(object sender, RoutedEventArgs e)
    {
        var type = SelectedSoundType; if (type is null) return; var file = ChooseAndImportSound(); if (file is null) return; _typeSounds[type] = file; TypeSoundBox.Text = file;
    }
    private void PreviewTypeSound_Click(object sender, RoutedEventArgs e)
    {
        var type = SelectedSoundType; _sounds.Preview(type is not null && _typeSounds.TryGetValue(type, out var file) ? file : _defaultSound);
    }
    private void ClearTypeSound_Click(object sender, RoutedEventArgs e)
    {
        var type = SelectedSoundType; if (type is not null) _typeSounds.Remove(type); TypeSoundBox.Text = "Using global sound";
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
