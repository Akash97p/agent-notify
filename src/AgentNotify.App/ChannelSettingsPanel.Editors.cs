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
            "twilio_whatsapp" => 16,
            "mqtt" => 17,
            "relay" => 18,
            _ => 0
        };
        UpdateProviderFieldVisibility();
    }

    private void ProviderKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
            return;
        if (SelectedProviderKind != "relay")
            CancelRelayPairing(clearPending: true);
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
                "twilio_whatsapp" => "Twilio WhatsApp",
                "mqtt" => "MQTT",
                "relay" => "AgentNotify Relay",
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
        var twilioWhatsApp = kind == "twilio_whatsapp";
        var mqtt = kind == "mqtt";
        var relay = kind == "relay";
        WebhookFields.Visibility = smtp || telegram || discord || slack || teams || zohoCliq || googleChat || mattermost || matrix || ntfy || gotify || pushover || pushbullet || twilioSms || whatsAppCloud || twilioWhatsApp || mqtt || relay ? Visibility.Collapsed : Visibility.Visible;
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
        TwilioWhatsAppFields.Visibility = twilioWhatsApp ? Visibility.Visible : Visibility.Collapsed;
        MqttFields.Visibility = mqtt ? Visibility.Visible : Visibility.Collapsed;
        RelayFields.Visibility = relay ? Visibility.Visible : Visibility.Collapsed;
        // Relay is hosted-only, so a private/loopback destination is never a choice there either.
        AllowPrivateBox.Visibility = telegram || discord || slack || teams || zohoCliq || googleChat || pushover || pushbullet || twilioSms || whatsAppCloud || twilioWhatsApp || relay ? Visibility.Collapsed : Visibility.Visible;
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
            SmtpPortBox.Text = JsonConfigReader.TryGetInt32(root, "port", out var number)
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
            TelegramThreadBox.Text = JsonConfigReader.GetInt32Text(root, "messageThreadId");
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
            PushoverRetryBox.Text = JsonConfigReader.TryGetInt32(root, "emergencyRetrySeconds", out var retrySeconds)
                ? retrySeconds.ToString()
                : "60";
            PushoverExpireBox.Text = JsonConfigReader.TryGetInt32(root, "emergencyExpireSeconds", out var expireSeconds)
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
            TwilioValidityBox.Text = JsonConfigReader.TryGetInt32(root, "validityPeriodSeconds", out var seconds)
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

    private void LoadTwilioWhatsAppConfiguration(ProviderProfile profile)
    {
        TwilioWhatsAppAccountSidBox.Clear();
        TwilioWhatsAppCredentialSidBox.Clear();
        TwilioWhatsAppCredentialSecretBox.Clear();
        TwilioWhatsAppRecipientBox.Clear();
        TwilioWhatsAppServiceSidBox.Clear();
        TwilioWhatsAppContentSidBox.Clear();
        if (profile.Kind != "twilio_whatsapp") return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            TwilioWhatsAppCredentialModeBox.SelectedIndex = GetJsonString(root, "credentialMode") == "auth_token" ? 1 : 0;
            TwilioWhatsAppVariablesBox.Text = root.TryGetProperty("contentVariables", out var variables) &&
                                                variables.ValueKind == JsonValueKind.Array
                ? string.Join(',', variables.EnumerateArray().Select(value => value.GetString()))
                : "";
            TwilioWhatsAppMinimumPriorityBox.SelectedIndex = GetJsonString(root, "minimumPriority") switch
            {
                "high" => 1,
                "normal" => 2,
                "low" => 3,
                _ => 0
            };
            TwilioWhatsAppValidityBox.Text = JsonConfigReader.TryGetInt32(root, "validityPeriodSeconds", out var seconds)
                ? seconds.ToString()
                : "300";
            TwilioWhatsAppOptInBox.IsChecked = root.TryGetProperty("recipientOptInAcknowledged", out var optIn) &&
                                                 optIn.ValueKind == JsonValueKind.True;
            TwilioWhatsAppTemplateApprovedBox.IsChecked = root.TryGetProperty("templateApprovedAcknowledged", out var approved) &&
                                                            approved.ValueKind == JsonValueKind.True;
            TwilioWhatsAppTextOnlyBox.IsChecked = root.TryGetProperty("textOnlyTemplateAcknowledged", out var textOnly) &&
                                                   textOnly.ValueKind == JsonValueKind.True;
            TwilioWhatsAppPaidConsentBox.IsChecked = root.TryGetProperty("paidSendConsent", out var paid) &&
                                                      paid.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            TwilioWhatsAppCredentialModeBox.SelectedIndex = 0;
            TwilioWhatsAppVariablesBox.Text = "title,message";
            TwilioWhatsAppMinimumPriorityBox.SelectedIndex = 0;
            TwilioWhatsAppValidityBox.Text = "300";
            TwilioWhatsAppOptInBox.IsChecked = false;
            TwilioWhatsAppTemplateApprovedBox.IsChecked = false;
            TwilioWhatsAppTextOnlyBox.IsChecked = false;
            TwilioWhatsAppPaidConsentBox.IsChecked = false;
        }
    }

    private void LoadMqttConfiguration(ProviderProfile profile)
    {
        MqttTopicBox.Clear();
        MqttUsernameBox.Clear();
        MqttPasswordBox.Clear();
        MqttCertificateThumbprintBox.Clear();
        if (profile.Kind != "mqtt") return;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var root = document.RootElement;
            MqttHostBox.Text = GetJsonString(root, "brokerHost");
            MqttPortBox.Text = JsonConfigReader.TryGetInt32(root, "port", out var portNumber)
                ? portNumber.ToString()
                : "8883";
            MqttClientIdBox.Text = GetJsonString(root, "clientId");
            MqttAuthenticationModeBox.SelectedIndex = GetJsonString(root, "authenticationMode") switch
            {
                "client_certificate" => 1,
                "username_and_certificate" => 2,
                "anonymous" => 3,
                _ => 0
            };
            MqttQosBox.SelectedIndex = JsonConfigReader.TryGetInt32(root, "qos", out var qosNumber)
                ? Math.Clamp(qosNumber, 0, 2)
                : 1;
            MqttDuplicateRiskBox.IsChecked = root.TryGetProperty("duplicateRiskAcknowledged", out var duplicate) &&
                                              duplicate.ValueKind == JsonValueKind.True;
            MqttAnonymousBox.IsChecked = root.TryGetProperty("anonymousAcknowledged", out var anonymous) &&
                                         anonymous.ValueKind == JsonValueKind.True;
            MqttExpiryBox.Text = JsonConfigReader.TryGetInt32(root, "messageExpirySeconds", out var expirySeconds)
                ? expirySeconds.ToString()
                : "300";
        }
        catch (JsonException)
        {
            MqttHostBox.Clear();
            MqttPortBox.Text = "8883";
            MqttClientIdBox.Text = "agentnotify";
            MqttAuthenticationModeBox.SelectedIndex = 0;
            MqttQosBox.SelectedIndex = 1;
            MqttDuplicateRiskBox.IsChecked = false;
            MqttAnonymousBox.IsChecked = false;
            MqttExpiryBox.Text = "300";
        }
    }

}
