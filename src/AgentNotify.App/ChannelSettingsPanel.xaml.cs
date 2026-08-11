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
        WebhookFields.Visibility = smtp || telegram || discord || slack || teams || zohoCliq || googleChat || mattermost || matrix ? Visibility.Collapsed : Visibility.Visible;
        SmtpFields.Visibility = smtp ? Visibility.Visible : Visibility.Collapsed;
        TelegramFields.Visibility = telegram ? Visibility.Visible : Visibility.Collapsed;
        DiscordFields.Visibility = discord ? Visibility.Visible : Visibility.Collapsed;
        SlackFields.Visibility = slack ? Visibility.Visible : Visibility.Collapsed;
        TeamsFields.Visibility = teams ? Visibility.Visible : Visibility.Collapsed;
        ZohoCliqFields.Visibility = zohoCliq ? Visibility.Visible : Visibility.Collapsed;
        GoogleChatFields.Visibility = googleChat ? Visibility.Visible : Visibility.Collapsed;
        MattermostFields.Visibility = mattermost ? Visibility.Visible : Visibility.Collapsed;
        MatrixFields.Visibility = matrix ? Visibility.Visible : Visibility.Collapsed;
        AllowPrivateBox.Visibility = telegram || discord || slack || teams || zohoCliq || googleChat ? Visibility.Collapsed : Visibility.Visible;
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
