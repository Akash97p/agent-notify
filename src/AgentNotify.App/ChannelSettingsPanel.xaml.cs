using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AgentNotify.Protocol;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core;

namespace AgentNotify.App;

public partial class ChannelSettingsPanel : System.Windows.Controls.UserControl
{
    private ProviderProfileService _profiles = null!;
    private DeliveryRouteService _routes = null!;
    private DeliveryDispatcher _dispatcher = null!;
    private bool _initialized;
    private CancellationTokenSource? _relayPairingCts;
    private string? _pendingRelayInstallationToken;
    private string? _pendingRelayInstallationId;
    private string? _pendingRelayName;
    /// <summary>
    /// The identity this computer presents when it pairs, generated once and
    /// then kept in the provider config. Reconnecting without it made the relay
    /// mint a fresh sender every time, so one machine turned into a column of
    /// identically-named rows with only the newest one reachable.
    /// </summary>
    private string? _pendingRelayInstallId;

    public ChannelSettingsPanel()
    {
        InitializeComponent();
        ProviderKindBox.SelectedIndex = 0;
        SmtpSecurityBox.SelectedIndex = 0;
        GoogleChatReplyPolicyBox.SelectedIndex = 0;
        PushbulletTargetTypeBox.SelectedIndex = 0;
        TwilioCredentialModeBox.SelectedIndex = 0;
        TwilioSenderTypeBox.SelectedIndex = 0;
        TwilioMinimumPriorityBox.SelectedIndex = 0;
        WhatsAppMinimumPriorityBox.SelectedIndex = 0;
        TwilioWhatsAppCredentialModeBox.SelectedIndex = 0;
        TwilioWhatsAppMinimumPriorityBox.SelectedIndex = 0;
        MqttAuthenticationModeBox.SelectedIndex = 0;
        MqttQosBox.SelectedIndex = 1;
        RoutePriorityBox.ItemsSource = Enum.GetNames<NotificationPriority>();
        RoutePriorityBox.SelectedItem = nameof(NotificationPriority.Normal);
    }

    public void Initialize(
        ProviderProfileService profiles,
        DeliveryRouteService routes,
        DeliveryDispatcher dispatcher)
    {
        _profiles = profiles;
        _routes = routes;
        _dispatcher = dispatcher;
        _initialized = true;
        _ = RunAsync(() => ReloadAsync());
    }

    private async Task ReloadAsync(string? providerId = null, string? routeId = null)
    {
        if (!_initialized)
            return;
        var providers = await _profiles.ListAsync();
        ProviderList.ItemsSource = providers;
        RouteProviderBox.ItemsSource = providers;
        ProviderEmptyHint.Visibility = providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (providerId is not null)
            ProviderList.SelectedItem = providers.FirstOrDefault(profile => profile.Id == providerId);

        var routes = await _routes.ListAsync();
        RouteList.ItemsSource = routes;
        RouteEmptyHint.Visibility = routes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (routeId is not null)
            RouteList.SelectedItem = routes.FirstOrDefault(route => route.Id == routeId);
        await RefreshDiagnosticsAsync();
    }

    private void Provider_Selected(object sender, SelectionChangedEventArgs e)
    {
        CancelRelayPairing(clearPending: true);
        // Selection runs outside RunAsync, so anything thrown here reaches the WPF dispatcher
        // unhandled and terminates the tray process, taking the broker and its API down with it.
        // A stored profile must never be able to do that: report it and leave the fields cleared.
        var profile = ProviderList.SelectedItem as ProviderProfile;
        try
        {
            // Clearing the selection is the only way back to a blank editor, so it must always
            // land on a usable "new provider" form instead of leaving the previous profile's
            // values and its locked provider type behind.
            if (profile is null)
                ResetProviderForm();
            else
                LoadSelectedProvider(profile);
        }
        catch (Exception exception)
        {
            SetStatus(
                profile is null
                    ? $"Could not clear the provider editor: {exception.Message}"
                    : $"Could not load the saved settings for '{profile.Name}': {exception.Message}",
                success: false);
        }
    }

    private void ProviderList_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
            return;
        e.Handled = true;
        StartNewProvider();
    }

    private void LoadSelectedProvider(ProviderProfile profile)
    {
        ProviderEditorHeader.Text = $"Editing “{profile.Name}”";
        ProviderBackToNewButton.Visibility = Visibility.Visible;
        ProviderNameBox.Text = profile.Name;
        ProviderEnabledBox.IsChecked = profile.Enabled;
        SelectProviderKind(profile.Kind);
        ProviderKindBox.IsEnabled = false;
        ProviderKindLockHint.Visibility = Visibility.Visible;
        EndpointBox.Clear();
        AuthorizationBox.Clear();
        HmacBox.Clear();
        ClearAuthorizationBox.IsChecked = false;
        ClearHmacBox.IsChecked = false;
        AllowPrivateBox.IsChecked = ReadAllowPrivate(profile.ConfigJson);
        LoadSmtpConfiguration(profile);
        LoadTelegramConfiguration(profile);
        LoadDiscordConfiguration(profile);
        LoadSlackConfiguration(profile);
        LoadTeamsConfiguration(profile);
        LoadZohoCliqConfiguration(profile);
        LoadGoogleChatConfiguration(profile);
        LoadMattermostConfiguration(profile);
        LoadMatrixConfiguration(profile);
        LoadNtfyConfiguration(profile);
        LoadGotifyConfiguration(profile);
        LoadPushoverConfiguration(profile);
        LoadPushbulletConfiguration(profile);
        LoadTwilioSmsConfiguration(profile);
        LoadWhatsAppCloudConfiguration(profile);
        LoadTwilioWhatsAppConfiguration(profile);
        LoadMqttConfiguration(profile);
        LoadRelayConfiguration(profile);
        StoredSecretsText.Text = profile.SecretNames.Count == 0
            ? "No encrypted values stored."
            : "Stored encrypted fields: " + string.Join(", ", profile.SecretNames);
    }

    private void NewProvider_Click(object sender, RoutedEventArgs e) => StartNewProvider();

    /// <summary>
    /// Returns the editor to a blank provider. Clearing the list selection raises
    /// <see cref="Provider_Selected"/>, which performs the reset, so the form and the list can
    /// never disagree about which profile is being edited.
    /// </summary>
    private void StartNewProvider()
    {
        try
        {
            if (ProviderList.SelectedItem is null)
                ResetProviderForm();
            else
                ProviderList.SelectedItem = null;
            ProviderNameBox.Focus();
            SetStatus(
                "Editing a new provider. Choose a provider type, fill in the fields, then press Save provider.",
                success: true);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not start a new provider: {exception.Message}", success: false);
        }
    }

    private void ResetProviderForm()
    {
        CancelRelayPairing(clearPending: true);
        ProviderEditorHeader.Text = "New provider";
        ProviderBackToNewButton.Visibility = Visibility.Collapsed;
        ProviderNameBox.Text = "Webhook";
        ProviderKindBox.IsEnabled = true;
        ProviderKindLockHint.Visibility = Visibility.Collapsed;
        ProviderKindBox.SelectedIndex = 0;
        ProviderEnabledBox.IsChecked = false;
        EndpointBox.Clear();
        AuthorizationBox.Clear();
        HmacBox.Clear();
        SmtpHostBox.Clear();
        SmtpPortBox.Text = "587";
        SmtpSecurityBox.SelectedIndex = 0;
        SmtpFromBox.Clear();
        SmtpFromNameBox.Text = "AgentNotify";
        SmtpRecipientsBox.Clear();
        SmtpSubjectPrefixBox.Text = "[AgentNotify] ";
        SmtpUsernameBox.Clear();
        SmtpPasswordBox.Clear();
        TelegramTokenBox.Clear();
        TelegramChatBox.Clear();
        TelegramThreadBox.Clear();
        TelegramSilentBox.IsChecked = false;
        TelegramProtectBox.IsChecked = true;
        DiscordWebhookBox.Clear();
        DiscordUsernameBox.Text = "AgentNotify";
        DiscordThreadBox.Clear();
        SlackWebhookBox.Clear();
        SlackThreadBox.Clear();
        TeamsWebhookBox.Clear();
        ZohoCliqWebhookBox.Clear();
        GoogleChatWebhookBox.Clear();
        GoogleChatThreadBox.Clear();
        GoogleChatReplyPolicyBox.SelectedIndex = 0;
        MattermostWebhookBox.Clear();
        MattermostSilentBox.IsChecked = false;
        MatrixHomeserverBox.Clear();
        MatrixTokenBox.Clear();
        MatrixRoomBox.Clear();
        NtfyServerBox.Text = "https://ntfy.sh";
        NtfyTopicBox.Clear();
        NtfyTokenBox.Clear();
        NtfyClearTokenBox.IsChecked = false;
        NtfyAnonymousBox.IsChecked = false;
        GotifyServerBox.Clear();
        GotifyTokenBox.Clear();
        PushoverTokenBox.Clear();
        PushoverUserKeyBox.Clear();
        PushoverDeviceBox.Clear();
        PushoverClearDeviceBox.IsChecked = false;
        PushoverSoundBox.Clear();
        PushoverEmergencyBox.IsChecked = false;
        PushoverRetryBox.Text = "60";
        PushoverExpireBox.Text = "3600";
        PushbulletTokenBox.Clear();
        PushbulletTargetBox.Clear();
        PushbulletTargetTypeBox.SelectedIndex = 0;
        PushbulletQuotaBox.IsChecked = false;
        TwilioAccountSidBox.Clear();
        TwilioCredentialModeBox.SelectedIndex = 0;
        TwilioCredentialSidBox.Clear();
        TwilioCredentialSecretBox.Clear();
        TwilioRecipientBox.Clear();
        TwilioSenderTypeBox.SelectedIndex = 0;
        TwilioSenderBox.Clear();
        TwilioMinimumPriorityBox.SelectedIndex = 0;
        TwilioValidityBox.Text = "300";
        TwilioPaidConsentBox.IsChecked = false;
        WhatsAppVersionBox.Text = "v25.0";
        WhatsAppPhoneNumberIdBox.Clear();
        WhatsAppAccessTokenBox.Clear();
        WhatsAppRecipientBox.Clear();
        WhatsAppTemplateBox.Text = "agentnotify_alert";
        WhatsAppLanguageBox.Text = "en_US";
        WhatsAppParametersBox.Text = "title,message";
        WhatsAppMinimumPriorityBox.SelectedIndex = 0;
        WhatsAppOptInBox.IsChecked = false;
        WhatsAppTemplateApprovedBox.IsChecked = false;
        WhatsAppPaidConsentBox.IsChecked = false;
        TwilioWhatsAppAccountSidBox.Clear();
        TwilioWhatsAppCredentialModeBox.SelectedIndex = 0;
        TwilioWhatsAppCredentialSidBox.Clear();
        TwilioWhatsAppCredentialSecretBox.Clear();
        TwilioWhatsAppRecipientBox.Clear();
        TwilioWhatsAppServiceSidBox.Clear();
        TwilioWhatsAppContentSidBox.Clear();
        TwilioWhatsAppVariablesBox.Text = "title,message";
        TwilioWhatsAppMinimumPriorityBox.SelectedIndex = 0;
        TwilioWhatsAppValidityBox.Text = "300";
        TwilioWhatsAppOptInBox.IsChecked = false;
        TwilioWhatsAppTemplateApprovedBox.IsChecked = false;
        TwilioWhatsAppTextOnlyBox.IsChecked = false;
        TwilioWhatsAppPaidConsentBox.IsChecked = false;
        MqttHostBox.Clear();
        MqttPortBox.Text = "8883";
        MqttClientIdBox.Text = "agentnotify";
        MqttTopicBox.Clear();
        MqttAuthenticationModeBox.SelectedIndex = 0;
        MqttUsernameBox.Clear();
        MqttPasswordBox.Clear();
        MqttCertificateThumbprintBox.Clear();
        MqttQosBox.SelectedIndex = 1;
        MqttDuplicateRiskBox.IsChecked = false;
        MqttAnonymousBox.IsChecked = false;
        MqttExpiryBox.Text = "300";
        RelaySenderNameBox.Clear();
        RelayInstallationTokenBox.Clear();
        ClearRelayTokenBox.IsChecked = false;
        ResetRelayConnectionPresentation(hasStoredCredential: false);
        ClearAuthorizationBox.IsChecked = false;
        ClearHmacBox.IsChecked = false;
        AllowPrivateBox.IsChecked = false;
        StoredSecretsText.Text = "Enter the provider configuration. New providers start disabled.";
    }

}
