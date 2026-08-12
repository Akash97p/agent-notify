using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AgentNotify.Contracts;
using AgentNotify.Core.Delivery;

namespace AgentNotify.App;

public partial class ChannelSettingsPanel : System.Windows.Controls.UserControl
{
    private ProviderProfileService _profiles = null!;
    private DeliveryRouteService _routes = null!;
    private DeliveryDispatcher _dispatcher = null!;
    private bool _initialized;

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
        if (providerId is not null)
            ProviderList.SelectedItem = providers.FirstOrDefault(profile => profile.Id == providerId);

        var routes = await _routes.ListAsync();
        RouteList.ItemsSource = routes;
        if (routeId is not null)
            RouteList.SelectedItem = routes.FirstOrDefault(route => route.Id == routeId);
        await RefreshDiagnosticsAsync();
    }

    private void Provider_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderList.SelectedItem is not ProviderProfile profile)
            return;
        ProviderNameBox.Text = profile.Name;
        ProviderEnabledBox.IsChecked = profile.Enabled;
        SelectProviderKind(profile.Kind);
        ProviderKindBox.IsEnabled = false;
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
        StoredSecretsText.Text = profile.SecretNames.Count == 0
            ? "No encrypted values stored."
            : "Stored encrypted fields: " + string.Join(", ", profile.SecretNames);
    }

    private void NewProvider_Click(object sender, RoutedEventArgs e)
    {
        ProviderList.SelectedItem = null;
        ProviderNameBox.Text = "Webhook";
        ProviderKindBox.IsEnabled = true;
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
        ClearAuthorizationBox.IsChecked = false;
        ClearHmacBox.IsChecked = false;
        AllowPrivateBox.IsChecked = false;
        StoredSecretsText.Text = "Enter the provider configuration. New providers start disabled.";
        ProviderNameBox.Focus();
    }

    private async void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var profile = await SaveProviderAsync();
            await ReloadAsync(providerId: profile.Id);
            SetStatus("Provider saved.", success: true);
        });
    }

    private async Task<ProviderProfile> SaveProviderAsync()
    {
        var existing = ProviderList.SelectedItem as ProviderProfile;
        return SelectedProviderKind switch
        {
            "smtp" => await SaveSmtpProviderAsync(existing),
            "telegram" => await SaveTelegramProviderAsync(existing),
            "discord" => await SaveDiscordProviderAsync(existing),
            "slack" => await SaveSlackProviderAsync(existing),
            "teams" => await SaveTeamsProviderAsync(existing),
            "zoho_cliq" => await SaveZohoCliqProviderAsync(existing),
            "google_chat" => await SaveGoogleChatProviderAsync(existing),
            "mattermost" => await SaveMattermostProviderAsync(existing),
            "matrix" => await SaveMatrixProviderAsync(existing),
            "ntfy" => await SaveNtfyProviderAsync(existing),
            "gotify" => await SaveGotifyProviderAsync(existing),
            "pushover" => await SavePushoverProviderAsync(existing),
            "pushbullet" => await SavePushbulletProviderAsync(existing),
            "twilio_sms" => await SaveTwilioSmsProviderAsync(existing),
            "whatsapp_cloud" => await SaveWhatsAppCloudProviderAsync(existing),
            _ => await SaveWebhookProviderAsync(existing)
        };
    }

    private async Task<ProviderProfile> SaveWebhookProviderAsync(ProviderProfile? existing)
    {
        var hasEndpoint = existing?.SecretNames.Contains("endpoint_url", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(EndpointBox.Password) && !hasEndpoint)
            throw new ArgumentException("Enter the webhook HTTPS endpoint.");

        var keepAuthorization =
            !string.IsNullOrEmpty(AuthorizationBox.Password) ||
            existing?.SecretNames.Contains("authorization", StringComparer.Ordinal) == true &&
            ClearAuthorizationBox.IsChecked != true;
        var keepHmac =
            !string.IsNullOrEmpty(HmacBox.Password) ||
            existing?.SecretNames.Contains("hmac_secret", StringComparer.Ordinal) == true &&
            ClearHmacBox.IsChecked != true;
        var config = JsonSerializer.Serialize(new
        {
            urlSecretName = "endpoint_url",
            allowPrivateNetwork = AllowPrivateBox.IsChecked == true,
            secretHeaders = keepAuthorization
                ? new Dictionary<string, string> { ["Authorization"] = "authorization" }
                : null,
            signature = keepHmac ? new { secretName = "hmac_secret" } : null
        }, Json.Options);

        var initialSecrets = existing is null
            ? BuildEnteredSecrets()
            : null;
        var saved = await _profiles.SaveAsync(
            existing?.Id,
            ProviderNameBox.Text,
            "webhook",
            ProviderEnabledBox.IsChecked == true,
            config,
            initialSecrets);
        if (existing is not null)
        {
            var remove = new List<string>();
            if (ClearAuthorizationBox.IsChecked == true) remove.Add("authorization");
            if (ClearHmacBox.IsChecked == true) remove.Add("hmac_secret");
            await _profiles.UpdateSecretsAsync(saved.Id, BuildEnteredSecrets(), remove);
        }
        EndpointBox.Clear();
        AuthorizationBox.Clear();
        HmacBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveSmtpProviderAsync(ProviderProfile? existing)
    {
        var hasUsername = existing?.SecretNames.Contains("username", StringComparer.Ordinal) == true;
        var hasPassword = existing?.SecretNames.Contains("password", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(SmtpUsernameBox.Password) && !hasUsername)
            throw new ArgumentException("Enter the SMTP username.");
        if (string.IsNullOrEmpty(SmtpPasswordBox.Password) && !hasPassword)
            throw new ArgumentException("Enter the SMTP password or app password.");
        if (!int.TryParse(SmtpPortBox.Text, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("SMTP port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(SmtpHostBox.Text))
            throw new ArgumentException("Enter the SMTP host.");
        if (string.IsNullOrWhiteSpace(SmtpFromBox.Text))
            throw new ArgumentException("Enter the From email address.");
        var recipients = SmtpRecipientsBox.Text
            .Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (recipients.Length is 0 or > 10)
            throw new ArgumentException("Enter one to ten recipient email addresses.");
        var config = JsonSerializer.Serialize(new
        {
            host = SmtpHostBox.Text.Trim(),
            port,
            security = (SmtpSecurityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "start_tls",
            allowPrivateNetwork = AllowPrivateBox.IsChecked == true,
            fromAddress = SmtpFromBox.Text.Trim(),
            fromName = SmtpFromNameBox.Text.Trim(),
            recipients,
            subjectPrefix = SmtpSubjectPrefixBox.Text,
            usernameSecretName = "username",
            passwordSecretName = "password"
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(SmtpUsernameBox.Password))
            changes["username"] = SmtpUsernameBox.Password.Trim();
        if (!string.IsNullOrEmpty(SmtpPasswordBox.Password))
            changes["password"] = SmtpPasswordBox.Password;
        var saved = await _profiles.SaveAsync(
            existing?.Id,
            ProviderNameBox.Text,
            "smtp",
            ProviderEnabledBox.IsChecked == true,
            config,
            existing is null ? changes : null);
        if (existing is not null && changes.Count > 0)
            await _profiles.UpdateSecretsAsync(saved.Id, changes);
        SmtpUsernameBox.Clear();
        SmtpPasswordBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveTelegramProviderAsync(ProviderProfile? existing)
    {
        var hasToken = existing?.SecretNames.Contains("bot_token", StringComparer.Ordinal) == true;
        var hasChat = existing?.SecretNames.Contains("chat_id", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(TelegramTokenBox.Password) && !hasToken)
            throw new ArgumentException("Enter the Telegram bot token.");
        if (string.IsNullOrWhiteSpace(TelegramChatBox.Password) && !hasChat)
            throw new ArgumentException("Enter the Telegram chat ID or @channel username.");
        int? threadId = null;
        if (!string.IsNullOrWhiteSpace(TelegramThreadBox.Text))
        {
            if (!int.TryParse(TelegramThreadBox.Text, out var parsedThreadId) || parsedThreadId <= 0)
                throw new ArgumentException("Telegram topic/thread ID must be a positive integer.");
            threadId = parsedThreadId;
        }

        var config = JsonSerializer.Serialize(new
        {
            botTokenSecretName = "bot_token",
            chatIdSecretName = "chat_id",
            messageThreadId = threadId,
            disableNotification = TelegramSilentBox.IsChecked == true,
            protectContent = TelegramProtectBox.IsChecked == true
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(TelegramTokenBox.Password))
            changes["bot_token"] = TelegramTokenBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(TelegramChatBox.Password))
            changes["chat_id"] = TelegramChatBox.Password.Trim();
        var saved = await _profiles.SaveAsync(
            existing?.Id,
            ProviderNameBox.Text,
            "telegram",
            ProviderEnabledBox.IsChecked == true,
            config,
            existing is null ? changes : null);
        if (existing is not null && changes.Count > 0)
            await _profiles.UpdateSecretsAsync(saved.Id, changes);
        TelegramTokenBox.Clear();
        TelegramChatBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveDiscordProviderAsync(ProviderProfile? existing)
    {
        var hasWebhook = existing?.SecretNames.Contains("webhook_url", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(DiscordWebhookBox.Password) && !hasWebhook)
            throw new ArgumentException("Enter the Discord incoming-webhook URL.");
        var username = DiscordUsernameBox.Text.Trim();
        if (username.Length is 0 or > 80 || username.Any(char.IsControl))
            throw new ArgumentException("Discord webhook display name must contain 1 to 80 characters.");
        var threadId = string.IsNullOrWhiteSpace(DiscordThreadBox.Text)
            ? null
            : DiscordThreadBox.Text.Trim();
        if (threadId is not null &&
            (threadId.Length is < 5 or > 20 || !threadId.All(char.IsAsciiDigit) || !threadId.Any(c => c != '0')))
            throw new ArgumentException("Discord thread ID must be a valid numeric snowflake.");

        var config = JsonSerializer.Serialize(new
        {
            webhookUrlSecretName = "webhook_url",
            username,
            threadId
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(DiscordWebhookBox.Password))
            changes["webhook_url"] = DiscordWebhookBox.Password.Trim();
        var saved = await _profiles.SaveAsync(
            existing?.Id,
            ProviderNameBox.Text,
            "discord",
            ProviderEnabledBox.IsChecked == true,
            config,
            existing is null ? changes : null);
        if (existing is not null && changes.Count > 0)
            await _profiles.UpdateSecretsAsync(saved.Id, changes);
        DiscordWebhookBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveSlackProviderAsync(ProviderProfile? existing)
    {
        var hasWebhook = existing?.SecretNames.Contains("webhook_url", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(SlackWebhookBox.Password) && !hasWebhook)
            throw new ArgumentException("Enter the Slack incoming-webhook URL.");
        var threadTimestamp = string.IsNullOrWhiteSpace(SlackThreadBox.Text)
            ? null
            : SlackThreadBox.Text.Trim();
        if (threadTimestamp is not null)
        {
            var separator = threadTimestamp.IndexOf('.');
            if (separator is < 10 or > 20 || separator != threadTimestamp.LastIndexOf('.') ||
                !threadTimestamp[..separator].All(char.IsAsciiDigit) ||
                threadTimestamp[(separator + 1)..].Length != 6 ||
                !threadTimestamp[(separator + 1)..].All(char.IsAsciiDigit))
                throw new ArgumentException("Slack thread timestamp must look like 1712345678.123456.");
        }

        var config = JsonSerializer.Serialize(new
        {
            webhookUrlSecretName = "webhook_url",
            threadTimestamp
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(SlackWebhookBox.Password))
            changes["webhook_url"] = SlackWebhookBox.Password.Trim();
        var saved = await _profiles.SaveAsync(
            existing?.Id,
            ProviderNameBox.Text,
            "slack",
            ProviderEnabledBox.IsChecked == true,
            config,
            existing is null ? changes : null);
        if (existing is not null && changes.Count > 0)
            await _profiles.UpdateSecretsAsync(saved.Id, changes);
        SlackWebhookBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveTeamsProviderAsync(ProviderProfile? existing)
    {
        var hasWebhook = existing?.SecretNames.Contains("webhook_url", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(TeamsWebhookBox.Password) && !hasWebhook)
            throw new ArgumentException("Enter the current Microsoft Teams Workflows webhook URL.");
        var config = JsonSerializer.Serialize(new { webhookUrlSecretName = "webhook_url" }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(TeamsWebhookBox.Password))
            changes["webhook_url"] = TeamsWebhookBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "teams",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null && changes.Count > 0)
            await _profiles.UpdateSecretsAsync(saved.Id, changes);
        TeamsWebhookBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveZohoCliqProviderAsync(ProviderProfile? existing)
    {
        var hasWebhook = existing?.SecretNames.Contains("webhook_url", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(ZohoCliqWebhookBox.Password) && !hasWebhook)
            throw new ArgumentException("Enter the Zoho Cliq webhook URL generated from Webhook Tokens.");
        var config = JsonSerializer.Serialize(new { webhookUrlSecretName = "webhook_url" }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(ZohoCliqWebhookBox.Password))
            changes["webhook_url"] = ZohoCliqWebhookBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "zoho_cliq",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null && changes.Count > 0)
            await _profiles.UpdateSecretsAsync(saved.Id, changes);
        ZohoCliqWebhookBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveGoogleChatProviderAsync(ProviderProfile? existing)
    {
        var hasWebhook = existing?.SecretNames.Contains("webhook_url", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(GoogleChatWebhookBox.Password) && !hasWebhook)
            throw new ArgumentException("Enter the incoming-webhook URL copied from Google Chat.");
        var threadKey = string.IsNullOrWhiteSpace(GoogleChatThreadBox.Text)
            ? null
            : GoogleChatThreadBox.Text.Trim();
        if (threadKey is not null && (threadKey.Length > 4000 || threadKey.Any(char.IsControl)))
            throw new ArgumentException("Google Chat thread key must contain at most 4000 characters and no control characters.");
        var replyPolicy = (GoogleChatReplyPolicyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "fallback";
        var config = JsonSerializer.Serialize(new
        {
            webhookUrlSecretName = "webhook_url",
            threadKey,
            threadReplyPolicy = replyPolicy
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(GoogleChatWebhookBox.Password))
            changes["webhook_url"] = GoogleChatWebhookBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "google_chat",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null && changes.Count > 0)
            await _profiles.UpdateSecretsAsync(saved.Id, changes);
        GoogleChatWebhookBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveMattermostProviderAsync(ProviderProfile? existing)
    {
        var hasWebhook = existing?.SecretNames.Contains("webhook_url", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(MattermostWebhookBox.Password) && !hasWebhook)
            throw new ArgumentException("Enter the Mattermost incoming-webhook URL.");
        var config = JsonSerializer.Serialize(new
        {
            webhookUrlSecretName = "webhook_url",
            allowPrivateNetwork = AllowPrivateBox.IsChecked == true,
            silent = MattermostSilentBox.IsChecked == true
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(MattermostWebhookBox.Password))
            changes["webhook_url"] = MattermostWebhookBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "mattermost",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null && changes.Count > 0)
            await _profiles.UpdateSecretsAsync(saved.Id, changes);
        MattermostWebhookBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveMatrixProviderAsync(ProviderProfile? existing)
    {
        var hasToken = existing?.SecretNames.Contains("access_token", StringComparer.Ordinal) == true;
        var hasRoom = existing?.SecretNames.Contains("room_id", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(MatrixHomeserverBox.Text)) throw new ArgumentException("Enter the Matrix homeserver HTTPS base URL.");
        if (string.IsNullOrWhiteSpace(MatrixTokenBox.Password) && !hasToken) throw new ArgumentException("Enter the Matrix access token.");
        if (string.IsNullOrWhiteSpace(MatrixRoomBox.Password) && !hasRoom) throw new ArgumentException("Enter the Matrix room ID.");
        var config = JsonSerializer.Serialize(new { homeserverBaseUrl = MatrixHomeserverBox.Text.Trim(), allowPrivateNetwork = AllowPrivateBox.IsChecked == true, accessTokenSecretName = "access_token", roomIdSecretName = "room_id" }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(MatrixTokenBox.Password)) changes["access_token"] = MatrixTokenBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(MatrixRoomBox.Password)) changes["room_id"] = MatrixRoomBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "matrix", ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null && changes.Count > 0) await _profiles.UpdateSecretsAsync(saved.Id, changes);
        MatrixTokenBox.Clear(); MatrixRoomBox.Clear(); return saved;
    }

    private async Task<ProviderProfile> SaveNtfyProviderAsync(ProviderProfile? existing)
    {
        var hasTopic = existing?.SecretNames.Contains("topic", StringComparer.Ordinal) == true;
        var hasToken = existing?.SecretNames.Contains("access_token", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(NtfyServerBox.Text))
            throw new ArgumentException("Enter the ntfy server HTTPS base URL.");
        if (string.IsNullOrWhiteSpace(NtfyTopicBox.Password) && !hasTopic)
            throw new ArgumentException("Enter the ntfy topic.");
        var willHaveToken = !string.IsNullOrWhiteSpace(NtfyTokenBox.Password) ||
                            hasToken && NtfyClearTokenBox.IsChecked != true;
        if (!willHaveToken && NtfyAnonymousBox.IsChecked != true)
            throw new ArgumentException("Enter an ntfy access token or explicitly allow unauthenticated publishing.");
        var config = JsonSerializer.Serialize(new
        {
            serverBaseUrl = NtfyServerBox.Text.Trim(),
            allowPrivateNetwork = AllowPrivateBox.IsChecked == true,
            allowUnauthenticatedTopic = NtfyAnonymousBox.IsChecked == true,
            topicSecretName = "topic",
            accessTokenSecretName = "access_token"
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(NtfyTopicBox.Password)) changes["topic"] = NtfyTopicBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(NtfyTokenBox.Password)) changes["access_token"] = NtfyTokenBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "ntfy",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
        {
            var remove = NtfyClearTokenBox.IsChecked == true ? new[] { "access_token" } : [];
            await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }
        NtfyTopicBox.Clear(); NtfyTokenBox.Clear(); NtfyClearTokenBox.IsChecked = false;
        return saved;
    }

    private async Task<ProviderProfile> SaveGotifyProviderAsync(ProviderProfile? existing)
    {
        var hasToken = existing?.SecretNames.Contains("application_token", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(GotifyServerBox.Text))
            throw new ArgumentException("Enter the Gotify server HTTPS base URL.");
        if (string.IsNullOrWhiteSpace(GotifyTokenBox.Password) && !hasToken)
            throw new ArgumentException("Enter the Gotify application token.");
        var config = JsonSerializer.Serialize(new
        {
            serverBaseUrl = GotifyServerBox.Text.Trim(),
            allowPrivateNetwork = AllowPrivateBox.IsChecked == true,
            applicationTokenSecretName = "application_token"
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(GotifyTokenBox.Password))
            changes["application_token"] = GotifyTokenBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "gotify",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null && changes.Count > 0) await _profiles.UpdateSecretsAsync(saved.Id, changes);
        GotifyTokenBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SavePushoverProviderAsync(ProviderProfile? existing)
    {
        var hasToken = existing?.SecretNames.Contains("application_token", StringComparer.Ordinal) == true;
        var hasUserKey = existing?.SecretNames.Contains("user_key", StringComparer.Ordinal) == true;
        var hasDevice = existing?.SecretNames.Contains("device", StringComparer.Ordinal) == true;
        if (string.IsNullOrWhiteSpace(PushoverTokenBox.Password) && !hasToken)
            throw new ArgumentException("Enter the 30-character Pushover application API token.");
        if (string.IsNullOrWhiteSpace(PushoverUserKeyBox.Password) && !hasUserKey)
            throw new ArgumentException("Enter the 30-character Pushover user or delivery-group key.");
        if (!string.IsNullOrWhiteSpace(PushoverTokenBox.Password) && !IsPushoverKey(PushoverTokenBox.Password.Trim()))
            throw new ArgumentException("The Pushover application token must be 30 alphanumeric characters.");
        if (!string.IsNullOrWhiteSpace(PushoverUserKeyBox.Password) && !IsPushoverKey(PushoverUserKeyBox.Password.Trim()))
            throw new ArgumentException("The Pushover user/group key must be 30 alphanumeric characters.");
        if (!int.TryParse(PushoverRetryBox.Text, out var retry) || retry < 30)
            throw new ArgumentException("Emergency retry must be at least 30 seconds.");
        if (!int.TryParse(PushoverExpireBox.Text, out var expire) || expire is < 1 or > 10_800)
            throw new ArgumentException("Emergency expiry must be between 1 and 10800 seconds.");
        var sound = PushoverSoundBox.Text.Trim();
        if (sound.Length > 64 || sound.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("Pushover sound names may contain only letters, digits, underscore, and hyphen.");
        var enteredDevice = PushoverDeviceBox.Password.Trim();
        if (enteredDevice.Length > 0 && (enteredDevice.Length > 25 || enteredDevice.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')))
            throw new ArgumentException("Pushover device names are at most 25 characters using letters, digits, underscore, and hyphen.");

        var config = JsonSerializer.Serialize(new
        {
            applicationTokenSecretName = "application_token",
            userKeySecretName = "user_key",
            deviceSecretName = "device",
            sound,
            criticalAsEmergency = PushoverEmergencyBox.IsChecked == true,
            emergencyRetrySeconds = retry,
            emergencyExpireSeconds = expire
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(PushoverTokenBox.Password))
            changes["application_token"] = PushoverTokenBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(PushoverUserKeyBox.Password))
            changes["user_key"] = PushoverUserKeyBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(PushoverDeviceBox.Password))
            changes["device"] = PushoverDeviceBox.Password.Trim();
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "pushover",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
        {
            var remove = hasDevice && enteredDevice.Length == 0 && PushoverClearDeviceBox.IsChecked == true
                ? new[] { "device" }
                : [];
            await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }
        PushoverTokenBox.Clear();
        PushoverUserKeyBox.Clear();
        PushoverDeviceBox.Clear();
        PushoverClearDeviceBox.IsChecked = false;
        return saved;
    }

    private static bool IsPushoverKey(string value) =>
        value.Length == 30 && value.All(char.IsAsciiLetterOrDigit);

    private async Task<ProviderProfile> SavePushbulletProviderAsync(ProviderProfile? existing)
    {
        var hasToken = existing?.SecretNames.Contains("access_token", StringComparer.Ordinal) == true;
        var hasTarget = existing?.SecretNames.Contains("target", StringComparer.Ordinal) == true;
        var targetType = (PushbulletTargetTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var previousTargetType = ReadConfigString(existing?.ConfigJson, "targetType", "all");
        var enteredTarget = PushbulletTargetBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(PushbulletTokenBox.Password) && !hasToken)
            throw new ArgumentException("Enter the Pushbullet personal access token.");
        if (!string.IsNullOrWhiteSpace(PushbulletTokenBox.Password) && !IsPushbulletToken(PushbulletTokenBox.Password.Trim()))
            throw new ArgumentException("The Pushbullet token must be 16–256 printable characters without spaces.");
        if (PushbulletQuotaBox.IsChecked != true)
            throw new ArgumentException("Acknowledge the Pushbullet monthly quota before saving.");
        if (targetType != "all" && enteredTarget.Length == 0 &&
            (!hasTarget || !string.Equals(previousTargetType, targetType, StringComparison.Ordinal)))
            throw new ArgumentException("Enter the selected Pushbullet device ID, channel tag, or email target.");
        ValidatePushbulletTarget(targetType, enteredTarget);

        var config = JsonSerializer.Serialize(new
        {
            accessTokenSecretName = "access_token",
            targetType,
            targetSecretName = "target",
            quotaAcknowledged = true
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(PushbulletTokenBox.Password))
            changes["access_token"] = PushbulletTokenBox.Password.Trim();
        if (enteredTarget.Length > 0)
            changes["target"] = enteredTarget;
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "pushbullet",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
        {
            var remove = targetType == "all" && hasTarget ? new[] { "target" } : [];
            await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }
        PushbulletTokenBox.Clear();
        PushbulletTargetBox.Clear();
        return saved;
    }

    private static bool IsPushbulletToken(string value) =>
        value.Length is >= 16 and <= 256 && value.All(character => character is >= '!' and <= '~');

    private static void ValidatePushbulletTarget(string targetType, string value)
    {
        if (targetType == "all" || value.Length == 0) return;
        if (targetType == "email")
        {
            if (value.Length > 254 || value.IndexOfAny(['\r', '\n']) >= 0 ||
                !System.Net.Mail.MailAddress.TryCreate(value, out var address) ||
                !string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Enter one valid email address without a display name.");
            return;
        }
        if (targetType is not ("device" or "channel") || value.Length > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("Device IDs and channel tags may contain only letters, digits, underscore, and hyphen.");
    }

    private static string ReadConfigString(string? configJson, string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return fallback;
        try
        {
            using var document = JsonDocument.Parse(configJson);
            var value = GetJsonString(document.RootElement, name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch (JsonException) { return fallback; }
    }

    private async Task<ProviderProfile> SaveTwilioSmsProviderAsync(ProviderProfile? existing)
    {
        var secretNames = existing?.SecretNames ?? [];
        var mode = (TwilioCredentialModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "api_key";
        var senderType = (TwilioSenderTypeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "messaging_service";
        var minimumPriority = (TwilioMinimumPriorityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "critical";
        var previousMode = ReadConfigString(existing?.ConfigJson, "credentialMode", "api_key");
        var previousSenderType = ReadConfigString(existing?.ConfigJson, "senderType", "messaging_service");
        RequireSecret(TwilioAccountSidBox.Password, secretNames, "account_sid", "Enter the Twilio Account SID.");
        RequireSecret(TwilioCredentialSecretBox.Password, secretNames, "credential_secret", "Enter the Twilio API Key secret or Auth Token.");
        RequireSecret(TwilioRecipientBox.Password, secretNames, "recipient", "Enter the one permitted SMS recipient.");
        RequireSecret(TwilioSenderBox.Password, secretNames, "sender", "Enter the Twilio sender.");
        if (mode == "api_key" && string.IsNullOrWhiteSpace(TwilioCredentialSidBox.Password) &&
            (!secretNames.Contains("credential_sid", StringComparer.Ordinal) || previousMode != "api_key"))
            throw new ArgumentException("Enter the Twilio API Key SID.");
        if (!string.IsNullOrWhiteSpace(TwilioAccountSidBox.Password) && !IsTwilioSid(TwilioAccountSidBox.Password.Trim(), "AC"))
            throw new ArgumentException("The Twilio Account SID must be AC followed by 32 hexadecimal characters.");
        if (!string.IsNullOrWhiteSpace(TwilioCredentialSidBox.Password) && !IsTwilioSid(TwilioCredentialSidBox.Password.Trim(), "SK"))
            throw new ArgumentException("The Twilio API Key SID must be SK followed by 32 hexadecimal characters.");
        if (!string.IsNullOrWhiteSpace(TwilioCredentialSecretBox.Password) && !IsPrintableSecret(TwilioCredentialSecretBox.Password))
            throw new ArgumentException("The Twilio credential secret must be 16–256 printable characters without spaces.");
        if (!string.IsNullOrWhiteSpace(TwilioRecipientBox.Password) && !IsE164(TwilioRecipientBox.Password.Trim()))
            throw new ArgumentException("The SMS recipient must be an E.164 number such as +15551234567.");
        var enteredSender = TwilioSenderBox.Password.Trim();
        if (enteredSender.Length > 0 && !(senderType == "phone" ? IsE164(enteredSender) : IsTwilioSid(enteredSender, "MG")))
            throw new ArgumentException("Enter a matching E.164 Twilio number or MG Messaging Service SID.");
        if (enteredSender.Length == 0 && previousSenderType != senderType)
            throw new ArgumentException("Re-enter the sender after changing its type.");
        if (!int.TryParse(TwilioValidityBox.Text, out var validity) || validity is < 6 or > 36_000)
            throw new ArgumentException("Twilio queue validity must be between 6 and 36000 seconds.");
        if (TwilioPaidConsentBox.IsChecked != true)
            throw new ArgumentException("Authorize paid SMS sends before saving this provider.");

        var config = JsonSerializer.Serialize(new
        {
            accountSidSecretName = "account_sid",
            credentialMode = mode,
            credentialSidSecretName = "credential_sid",
            credentialSecretName = "credential_secret",
            recipientSecretName = "recipient",
            senderType,
            senderSecretName = "sender",
            paidSendConsent = true,
            minimumPriority,
            validityPeriodSeconds = validity
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        AddSecret(changes, "account_sid", TwilioAccountSidBox.Password);
        AddSecret(changes, "credential_sid", TwilioCredentialSidBox.Password);
        AddSecret(changes, "credential_secret", TwilioCredentialSecretBox.Password);
        AddSecret(changes, "recipient", TwilioRecipientBox.Password);
        AddSecret(changes, "sender", TwilioSenderBox.Password);
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "twilio_sms",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
        {
            var remove = mode == "auth_token" && secretNames.Contains("credential_sid", StringComparer.Ordinal)
                ? new[] { "credential_sid" }
                : [];
            await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }
        TwilioAccountSidBox.Clear();
        TwilioCredentialSidBox.Clear();
        TwilioCredentialSecretBox.Clear();
        TwilioRecipientBox.Clear();
        TwilioSenderBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveWhatsAppCloudProviderAsync(ProviderProfile? existing)
    {
        var secretNames = existing?.SecretNames ?? [];
        RequireSecret(WhatsAppPhoneNumberIdBox.Password, secretNames, "phone_number_id",
            "Enter the WhatsApp phone-number ID.");
        RequireSecret(WhatsAppAccessTokenBox.Password, secretNames, "access_token",
            "Enter a Meta system-user access token.");
        RequireSecret(WhatsAppRecipientBox.Password, secretNames, "recipient",
            "Enter the one opted-in WhatsApp recipient.");

        var version = WhatsAppVersionBox.Text.Trim();
        if (!IsMetaGraphVersion(version))
            throw new ArgumentException("The Meta Graph version must look like v25.0 (major 1–99).");
        var phoneNumberId = WhatsAppPhoneNumberIdBox.Password.Trim();
        if (phoneNumberId.Length > 0 && !IsWhatsAppPhoneNumberId(phoneNumberId))
            throw new ArgumentException("The WhatsApp phone-number ID must contain 5–32 digits.");
        var accessToken = WhatsAppAccessTokenBox.Password;
        if (accessToken.Length > 0 && (accessToken.Length is < 16 or > 2048 ||
                                      accessToken.Any(character => character is <= ' ' or > '~')))
            throw new ArgumentException("The Meta access token must be 16–2048 printable characters without spaces.");
        var recipient = WhatsAppRecipientBox.Password.Trim();
        if (recipient.Length > 0 && !IsE164(recipient))
            throw new ArgumentException("The WhatsApp recipient must be an E.164 number such as +15551234567.");
        var templateName = WhatsAppTemplateBox.Text.Trim();
        if (!IsWhatsAppTemplateName(templateName))
            throw new ArgumentException("The approved template name may contain lowercase letters, digits, and underscore only.");
        var languageCode = WhatsAppLanguageBox.Text.Trim();
        if (!IsWhatsAppLanguageCode(languageCode))
            throw new ArgumentException("Enter a language code such as en or en_US.");
        var parameters = WhatsAppParametersBox.Text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var allowedParameters = new HashSet<string>(["title", "message", "priority", "type", "agent", "project"], StringComparer.Ordinal);
        if (parameters.Length > 5 || parameters.Any(value => !allowedParameters.Contains(value)) ||
            parameters.Distinct(StringComparer.Ordinal).Count() != parameters.Length)
            throw new ArgumentException("Use up to five unique allowed template variables in the approved order.");
        if (WhatsAppOptInBox.IsChecked != true)
            throw new ArgumentException("Confirm that the recipient explicitly opted in.");
        if (WhatsAppTemplateApprovedBox.IsChecked != true)
            throw new ArgumentException("Confirm the approved template, language, and variable order.");
        if (WhatsAppPaidConsentBox.IsChecked != true)
            throw new ArgumentException("Authorize paid WhatsApp template sends before saving this provider.");
        var minimumPriority = (WhatsAppMinimumPriorityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "critical";

        var config = JsonSerializer.Serialize(new
        {
            apiVersion = version,
            phoneNumberIdSecretName = "phone_number_id",
            accessTokenSecretName = "access_token",
            recipientSecretName = "recipient",
            templateName,
            languageCode,
            bodyParameters = parameters,
            recipientOptInAcknowledged = true,
            templateApprovedAcknowledged = true,
            paidSendConsent = true,
            minimumPriority
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        AddSecret(changes, "phone_number_id", WhatsAppPhoneNumberIdBox.Password);
        AddSecret(changes, "access_token", WhatsAppAccessTokenBox.Password);
        AddSecret(changes, "recipient", WhatsAppRecipientBox.Password);
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "whatsapp_cloud",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
            await _profiles.UpdateSecretsAsync(saved.Id, changes, []);
        WhatsAppPhoneNumberIdBox.Clear();
        WhatsAppAccessTokenBox.Clear();
        WhatsAppRecipientBox.Clear();
        return saved;
    }

    private static void RequireSecret(string entered, IReadOnlyList<string> stored, string name, string message)
    {
        if (string.IsNullOrWhiteSpace(entered) && !stored.Contains(name, StringComparer.Ordinal))
            throw new ArgumentException(message);
    }

    private static void AddSecret(IDictionary<string, string> changes, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) changes[name] = value.Trim();
    }

    private static bool IsTwilioSid(string value, string prefix) =>
        value.Length == 34 && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value[2..].All(Uri.IsHexDigit);

    private static bool IsE164(string value) =>
        value.Length is >= 9 and <= 16 && value[0] == '+' && value[1] is >= '1' and <= '9' &&
        value[2..].All(char.IsAsciiDigit);

    private static bool IsPrintableSecret(string value) =>
        value.Length is >= 16 and <= 256 && value.All(character => character is >= '!' and <= '~');

    private static bool IsMetaGraphVersion(string value)
    {
        if (!value.StartsWith('v') || !value.EndsWith(".0", StringComparison.Ordinal)) return false;
        return int.TryParse(value.AsSpan(1, value.Length - 3), out var major) && major is >= 1 and <= 99;
    }

    private static bool IsWhatsAppPhoneNumberId(string value) =>
        value.Length is >= 5 and <= 32 && value.All(char.IsAsciiDigit);

    private static bool IsWhatsAppTemplateName(string value) =>
        value.Length is >= 1 and <= 512 &&
        value.All(character => character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '_');

    private static bool IsWhatsAppLanguageCode(string value)
    {
        var parts = value.Split('_');
        return parts.Length is 1 or 2 && parts[0].Length is 2 or 3 &&
               parts[0].All(character => character is >= 'a' and <= 'z') &&
               (parts.Length == 1 || parts[1].Length == 2 &&
                parts[1].All(character => character is >= 'A' and <= 'Z'));
    }

    private Dictionary<string, string> BuildEnteredSecrets()
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(EndpointBox.Password))
            secrets["endpoint_url"] = EndpointBox.Password.Trim();
        if (!string.IsNullOrEmpty(AuthorizationBox.Password))
            secrets["authorization"] = AuthorizationBox.Password;
        if (!string.IsNullOrEmpty(HmacBox.Password))
            secrets["hmac_secret"] = HmacBox.Password;
        return secrets;
    }

    private string SelectedProviderKind =>
        (ProviderKindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "webhook";

    private void SelectProviderKind(string kind)
    {
        ProviderKindBox.SelectedIndex = kind switch
        {
            "smtp" => 1,
            "telegram" => 2,
            "discord" => 3,
            "slack" => 4,
            "teams" => 5,
            "zoho_cliq" => 6,
            "google_chat" => 7,
            "mattermost" => 8,
            "matrix" => 9,
            "ntfy" => 10,
            "gotify" => 11,
            "pushover" => 12,
            "pushbullet" => 13,
            "twilio_sms" => 14,
            "whatsapp_cloud" => 15,
            _ => 0
        };
        UpdateProviderFieldVisibility();
    }

    private void ProviderKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
            return;
        UpdateProviderFieldVisibility();
        if (ProviderList.SelectedItem is null)
            ProviderNameBox.Text = SelectedProviderKind switch
            {
                "smtp" => "Email",
                "telegram" => "Telegram",
                "discord" => "Discord",
                "slack" => "Slack",
                "teams" => "Microsoft Teams",
                "zoho_cliq" => "Zoho Cliq",
                "google_chat" => "Google Chat",
                "mattermost" => "Mattermost",
                "matrix" => "Matrix",
                "ntfy" => "ntfy",
                "gotify" => "Gotify",
                "pushover" => "Pushover",
                "pushbullet" => "Pushbullet",
                "twilio_sms" => "Twilio SMS",
                "whatsapp_cloud" => "WhatsApp Cloud",
                _ => "Webhook"
            };
    }

    private void UpdateProviderFieldVisibility()
    {
        var kind = SelectedProviderKind;
        var smtp = kind == "smtp";
        var telegram = kind == "telegram";
        var discord = kind == "discord";
        var slack = kind == "slack";
        var teams = kind == "teams";
        var zohoCliq = kind == "zoho_cliq";
        var googleChat = kind == "google_chat";
        var mattermost = kind == "mattermost";
        var matrix = kind == "matrix";
        var ntfy = kind == "ntfy";
        var gotify = kind == "gotify";
        var pushover = kind == "pushover";
        var pushbullet = kind == "pushbullet";
        var twilioSms = kind == "twilio_sms";
        var whatsAppCloud = kind == "whatsapp_cloud";
        WebhookFields.Visibility = smtp || telegram || discord || slack || teams || zohoCliq || googleChat || mattermost || matrix || ntfy || gotify || pushover || pushbullet || twilioSms || whatsAppCloud ? Visibility.Collapsed : Visibility.Visible;
        SmtpFields.Visibility = smtp ? Visibility.Visible : Visibility.Collapsed;
        TelegramFields.Visibility = telegram ? Visibility.Visible : Visibility.Collapsed;
        DiscordFields.Visibility = discord ? Visibility.Visible : Visibility.Collapsed;
        SlackFields.Visibility = slack ? Visibility.Visible : Visibility.Collapsed;
        TeamsFields.Visibility = teams ? Visibility.Visible : Visibility.Collapsed;
        ZohoCliqFields.Visibility = zohoCliq ? Visibility.Visible : Visibility.Collapsed;
        GoogleChatFields.Visibility = googleChat ? Visibility.Visible : Visibility.Collapsed;
        MattermostFields.Visibility = mattermost ? Visibility.Visible : Visibility.Collapsed;
        MatrixFields.Visibility = matrix ? Visibility.Visible : Visibility.Collapsed;
        NtfyFields.Visibility = ntfy ? Visibility.Visible : Visibility.Collapsed;
        GotifyFields.Visibility = gotify ? Visibility.Visible : Visibility.Collapsed;
        PushoverFields.Visibility = pushover ? Visibility.Visible : Visibility.Collapsed;
        PushbulletFields.Visibility = pushbullet ? Visibility.Visible : Visibility.Collapsed;
        TwilioSmsFields.Visibility = twilioSms ? Visibility.Visible : Visibility.Collapsed;
        WhatsAppCloudFields.Visibility = whatsAppCloud ? Visibility.Visible : Visibility.Collapsed;
        AllowPrivateBox.Visibility = telegram || discord || slack || teams || zohoCliq || googleChat || pushover || pushbullet || twilioSms || whatsAppCloud ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadSmtpConfiguration(ProviderProfile profile)
    {
        SmtpUsernameBox.Clear();
        SmtpPasswordBox.Clear();
        if (profile.Kind != "smtp")
            return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            SmtpHostBox.Text = GetJsonString(root, "host");
            SmtpPortBox.Text = root.TryGetProperty("port", out var port) && port.TryGetInt32(out var number)
                ? number.ToString()
                : "587";
            var security = GetJsonString(root, "security");
            SmtpSecurityBox.SelectedIndex = security == "tls" ? 1 : 0;
            SmtpFromBox.Text = GetJsonString(root, "fromAddress");
            SmtpFromNameBox.Text = GetJsonString(root, "fromName");
            SmtpSubjectPrefixBox.Text = GetJsonString(root, "subjectPrefix");
            SmtpRecipientsBox.Text = root.TryGetProperty("recipients", out var recipients) &&
                                     recipients.ValueKind == JsonValueKind.Array
                ? string.Join(Environment.NewLine, recipients.EnumerateArray().Select(value => value.GetString()))
                : "";
        }
        catch (JsonException)
        {
            SmtpHostBox.Clear();
        }
    }

    private void LoadTelegramConfiguration(ProviderProfile profile)
    {
        TelegramTokenBox.Clear();
        TelegramChatBox.Clear();
        if (profile.Kind != "telegram")
            return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            TelegramThreadBox.Text = root.TryGetProperty("messageThreadId", out var thread) &&
                                     thread.TryGetInt32(out var threadId)
                ? threadId.ToString()
                : "";
            TelegramSilentBox.IsChecked = root.TryGetProperty("disableNotification", out var silent) &&
                                          silent.ValueKind == JsonValueKind.True;
            TelegramProtectBox.IsChecked = !root.TryGetProperty("protectContent", out var protect) ||
                                           protect.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            TelegramThreadBox.Clear();
            TelegramSilentBox.IsChecked = false;
            TelegramProtectBox.IsChecked = true;
        }
    }

    private void LoadDiscordConfiguration(ProviderProfile profile)
    {
        DiscordWebhookBox.Clear();
        if (profile.Kind != "discord")
            return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            DiscordUsernameBox.Text = GetJsonString(root, "username");
            DiscordThreadBox.Text = GetJsonString(root, "threadId");
        }
        catch (JsonException)
        {
            DiscordUsernameBox.Text = "AgentNotify";
            DiscordThreadBox.Clear();
        }
    }

    private void LoadSlackConfiguration(ProviderProfile profile)
    {
        SlackWebhookBox.Clear();
        if (profile.Kind != "slack")
            return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            SlackThreadBox.Text = GetJsonString(document.RootElement, "threadTimestamp");
        }
        catch (JsonException)
        {
            SlackThreadBox.Clear();
        }
    }

    private void LoadTeamsConfiguration(ProviderProfile profile)
    {
        TeamsWebhookBox.Clear();
    }

    private void LoadZohoCliqConfiguration(ProviderProfile profile)
    {
        ZohoCliqWebhookBox.Clear();
    }

    private void LoadGoogleChatConfiguration(ProviderProfile profile)
    {
        GoogleChatWebhookBox.Clear();
        if (profile.Kind != "google_chat")
            return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            GoogleChatThreadBox.Text = GetJsonString(root, "threadKey");
            GoogleChatReplyPolicyBox.SelectedIndex =
                GetJsonString(root, "threadReplyPolicy") == "fail" ? 1 : 0;
        }
        catch (JsonException)
        {
            GoogleChatThreadBox.Clear();
            GoogleChatReplyPolicyBox.SelectedIndex = 0;
        }
    }

    private void LoadMattermostConfiguration(ProviderProfile profile)
    {
        MattermostWebhookBox.Clear();
        if (profile.Kind != "mattermost")
            return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            MattermostSilentBox.IsChecked = document.RootElement.TryGetProperty("silent", out var silent) &&
                                            silent.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            MattermostSilentBox.IsChecked = false;
        }
    }

    private void LoadMatrixConfiguration(ProviderProfile profile)
    {
        MatrixTokenBox.Clear(); MatrixRoomBox.Clear();
        if (profile.Kind != "matrix") return;
        try { using var document = JsonDocument.Parse(profile.ConfigJson); MatrixHomeserverBox.Text = GetJsonString(document.RootElement, "homeserverBaseUrl"); }
        catch (JsonException) { MatrixHomeserverBox.Clear(); }
    }

    private void LoadNtfyConfiguration(ProviderProfile profile)
    {
        NtfyTopicBox.Clear(); NtfyTokenBox.Clear(); NtfyClearTokenBox.IsChecked = false;
        if (profile.Kind != "ntfy") return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            NtfyServerBox.Text = GetJsonString(root, "serverBaseUrl");
            NtfyAnonymousBox.IsChecked = root.TryGetProperty("allowUnauthenticatedTopic", out var anonymous) &&
                                         anonymous.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            NtfyServerBox.Text = "https://ntfy.sh";
            NtfyAnonymousBox.IsChecked = false;
        }
    }

    private void LoadGotifyConfiguration(ProviderProfile profile)
    {
        GotifyTokenBox.Clear();
        if (profile.Kind != "gotify") return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            GotifyServerBox.Text = GetJsonString(document.RootElement, "serverBaseUrl");
        }
        catch (JsonException) { GotifyServerBox.Clear(); }
    }

    private void LoadPushoverConfiguration(ProviderProfile profile)
    {
        PushoverTokenBox.Clear();
        PushoverUserKeyBox.Clear();
        PushoverDeviceBox.Clear();
        PushoverClearDeviceBox.IsChecked = false;
        if (profile.Kind != "pushover") return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            PushoverSoundBox.Text = GetJsonString(root, "sound");
            PushoverEmergencyBox.IsChecked = root.TryGetProperty("criticalAsEmergency", out var emergency) &&
                                               emergency.ValueKind == JsonValueKind.True;
            PushoverRetryBox.Text = root.TryGetProperty("emergencyRetrySeconds", out var retry) &&
                                    retry.TryGetInt32(out var retrySeconds)
                ? retrySeconds.ToString()
                : "60";
            PushoverExpireBox.Text = root.TryGetProperty("emergencyExpireSeconds", out var expire) &&
                                     expire.TryGetInt32(out var expireSeconds)
                ? expireSeconds.ToString()
                : "3600";
        }
        catch (JsonException)
        {
            PushoverSoundBox.Clear();
            PushoverEmergencyBox.IsChecked = false;
            PushoverRetryBox.Text = "60";
            PushoverExpireBox.Text = "3600";
        }
    }

    private void LoadPushbulletConfiguration(ProviderProfile profile)
    {
        PushbulletTokenBox.Clear();
        PushbulletTargetBox.Clear();
        if (profile.Kind != "pushbullet") return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            var targetType = GetJsonString(root, "targetType");
            PushbulletTargetTypeBox.SelectedIndex = targetType switch
            {
                "device" => 1,
                "channel" => 2,
                "email" => 3,
                _ => 0
            };
            PushbulletQuotaBox.IsChecked = root.TryGetProperty("quotaAcknowledged", out var quota) &&
                                               quota.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            PushbulletTargetTypeBox.SelectedIndex = 0;
            PushbulletQuotaBox.IsChecked = false;
        }
    }

    private void LoadTwilioSmsConfiguration(ProviderProfile profile)
    {
        TwilioAccountSidBox.Clear();
        TwilioCredentialSidBox.Clear();
        TwilioCredentialSecretBox.Clear();
        TwilioRecipientBox.Clear();
        TwilioSenderBox.Clear();
        if (profile.Kind != "twilio_sms") return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            TwilioCredentialModeBox.SelectedIndex = GetJsonString(root, "credentialMode") == "auth_token" ? 1 : 0;
            TwilioSenderTypeBox.SelectedIndex = GetJsonString(root, "senderType") == "phone" ? 1 : 0;
            TwilioMinimumPriorityBox.SelectedIndex = GetJsonString(root, "minimumPriority") switch
            {
                "high" => 1,
                "normal" => 2,
                "low" => 3,
                _ => 0
            };
            TwilioValidityBox.Text = root.TryGetProperty("validityPeriodSeconds", out var validity) &&
                                     validity.TryGetInt32(out var seconds)
                ? seconds.ToString()
                : "300";
            TwilioPaidConsentBox.IsChecked = root.TryGetProperty("paidSendConsent", out var consent) &&
                                              consent.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            TwilioCredentialModeBox.SelectedIndex = 0;
            TwilioSenderTypeBox.SelectedIndex = 0;
            TwilioMinimumPriorityBox.SelectedIndex = 0;
            TwilioValidityBox.Text = "300";
            TwilioPaidConsentBox.IsChecked = false;
        }
    }

    private void LoadWhatsAppCloudConfiguration(ProviderProfile profile)
    {
        WhatsAppPhoneNumberIdBox.Clear();
        WhatsAppAccessTokenBox.Clear();
        WhatsAppRecipientBox.Clear();
        if (profile.Kind != "whatsapp_cloud") return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            WhatsAppVersionBox.Text = GetJsonString(root, "apiVersion");
            if (string.IsNullOrWhiteSpace(WhatsAppVersionBox.Text)) WhatsAppVersionBox.Text = "v25.0";
            WhatsAppTemplateBox.Text = GetJsonString(root, "templateName");
            WhatsAppLanguageBox.Text = GetJsonString(root, "languageCode");
            WhatsAppParametersBox.Text = root.TryGetProperty("bodyParameters", out var parameters) &&
                                             parameters.ValueKind == JsonValueKind.Array
                ? string.Join(',', parameters.EnumerateArray().Select(value => value.GetString()))
                : "";
            WhatsAppMinimumPriorityBox.SelectedIndex = GetJsonString(root, "minimumPriority") switch
            {
                "high" => 1,
                "normal" => 2,
                "low" => 3,
                _ => 0
            };
            WhatsAppOptInBox.IsChecked = root.TryGetProperty("recipientOptInAcknowledged", out var optIn) &&
                                           optIn.ValueKind == JsonValueKind.True;
            WhatsAppTemplateApprovedBox.IsChecked = root.TryGetProperty("templateApprovedAcknowledged", out var approved) &&
                                                      approved.ValueKind == JsonValueKind.True;
            WhatsAppPaidConsentBox.IsChecked = root.TryGetProperty("paidSendConsent", out var paid) &&
                                                paid.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            WhatsAppVersionBox.Text = "v25.0";
            WhatsAppTemplateBox.Text = "agentnotify_alert";
            WhatsAppLanguageBox.Text = "en_US";
            WhatsAppParametersBox.Text = "title,message";
            WhatsAppMinimumPriorityBox.SelectedIndex = 0;
            WhatsAppOptInBox.IsChecked = false;
            WhatsAppTemplateApprovedBox.IsChecked = false;
            WhatsAppPaidConsentBox.IsChecked = false;
        }
    }

    private async void TestProvider_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var profile = await SaveProviderAsync();
            var result = await _dispatcher.TestProviderAsync(profile.Id);
            await ReloadAsync(providerId: profile.Id);
            SetStatus(
                result.Succeeded
                    ? $"Test delivered (provider status {result.StatusCode?.ToString() ?? "ok"})."
                    : $"Test failed: {result.ErrorCode ?? "unspecified"}.",
                result.Succeeded);
        });
    }

    private async void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderList.SelectedItem is not ProviderProfile profile)
            return;
        if (System.Windows.MessageBox.Show(
                $"Delete provider '{profile.Name}' and its routes/delivery history?",
                "Delete provider",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            await _profiles.DeleteAsync(profile.Id);
            NewProvider_Click(sender, e);
            await ReloadAsync();
            SetStatus("Provider deleted.", success: true);
        });
    }

    private void Route_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (RouteList.SelectedItem is not DeliveryRoute route)
            return;
        RouteNameBox.Text = route.Name;
        RouteProviderBox.SelectedItem = RouteProviderBox.Items.Cast<ProviderProfile>()
            .FirstOrDefault(profile => profile.Id == route.ProviderId);
        RouteEnabledBox.IsChecked = route.Enabled;
        RoutePriorityBox.SelectedItem = route.MinimumPriority.ToString();
        RouteTypeBox.Text = route.TypeId ?? "";
        RouteProjectBox.Text = route.Project ?? "";
        RouteAgentBox.Text = route.Agent ?? "";
        IncludeMessageBox.IsChecked = route.IncludeMessage;
    }

    private void NewRoute_Click(object sender, RoutedEventArgs e)
    {
        RouteList.SelectedItem = null;
        RouteNameBox.Text = "Delivery route";
        RouteProviderBox.SelectedIndex = RouteProviderBox.Items.Count > 0 ? 0 : -1;
        RouteEnabledBox.IsChecked = false;
        RoutePriorityBox.SelectedItem = nameof(NotificationPriority.Normal);
        RouteTypeBox.Clear();
        RouteProjectBox.Clear();
        RouteAgentBox.Clear();
        IncludeMessageBox.IsChecked = false;
    }

    private async void SaveRoute_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            if (RouteProviderBox.SelectedItem is not ProviderProfile provider)
                throw new ArgumentException("Select a provider.");
            _ = Enum.TryParse<NotificationPriority>(
                RoutePriorityBox.SelectedItem?.ToString(),
                out var priority);
            var route = await _routes.SaveAsync(
                (RouteList.SelectedItem as DeliveryRoute)?.Id,
                RouteNameBox.Text,
                provider.Id,
                RouteEnabledBox.IsChecked == true,
                priority,
                RouteTypeBox.Text,
                RouteProjectBox.Text,
                RouteAgentBox.Text,
                IncludeMessageBox.IsChecked == true);
            await ReloadAsync(routeId: route.Id);
            SetStatus("Route saved. New matching notifications will be queued.", success: true);
        });
    }

    private async void DeleteRoute_Click(object sender, RoutedEventArgs e)
    {
        if (RouteList.SelectedItem is not DeliveryRoute route)
            return;
        if (System.Windows.MessageBox.Show(
                $"Delete route '{route.Name}' and its delivery history?",
                "Delete route",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            await _routes.DeleteAsync(route.Id);
            NewRoute_Click(sender, e);
            await ReloadAsync();
            SetStatus("Route deleted.", success: true);
        });
    }

    private async void RefreshDiagnostics_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(RefreshDiagnosticsAsync);

    private async Task RefreshDiagnosticsAsync()
    {
        var snapshot = await _dispatcher.GetDiagnosticsAsync();
        DiagnosticsText.Text =
            $"Pending: {snapshot.Pending}   Processing: {snapshot.Processing}   " +
            $"Retry: {snapshot.Retry}   Delivered: {snapshot.Delivered}   " +
            $"Dead-letter: {snapshot.DeadLetter}\n\n" +
            "Registered adapters: " + string.Join(", ", snapshot.RegisteredAdapters);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            IsEnabled = false;
            SetStatus("Working…", success: true);
            await operation();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, success: false);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void SetStatus(string message, bool success)
    {
        ChannelStatusText.Text = message;
        ChannelStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                success ? "#86EFAC" : "#FCA5A5"));
    }

    private static bool ReadAllowPrivate(string configJson)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return document.RootElement.TryGetProperty("allowPrivateNetwork", out var value) &&
                   value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string GetJsonString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
