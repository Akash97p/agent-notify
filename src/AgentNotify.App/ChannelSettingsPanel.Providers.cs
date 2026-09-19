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
            "twilio_whatsapp" => await SaveTwilioWhatsAppProviderAsync(existing),
            "mqtt" => await SaveMqttProviderAsync(existing),
            "relay" => await SaveRelayProviderAsync(existing),
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

}
