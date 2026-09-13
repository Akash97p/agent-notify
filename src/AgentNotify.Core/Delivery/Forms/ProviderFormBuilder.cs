using System.Text.Json;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery.Forms;

/// <summary>
/// Validates submitted provider editor state and turns it into the stored configuration document
/// and secret changes each adapter expects.
/// </summary>
/// <remarks>
/// This is the portable twin of the Windows Settings window's provider editor: the property names,
/// secret names, and refusal messages are the same, so a profile saved from either surface is read
/// identically by the delivery adapters. Every refusal is an <see cref="ArgumentException"/> whose
/// message is written for the person filling in the form.
/// </remarks>
public static class ProviderFormBuilder
{
    private static readonly HashSet<string> TemplateVariables =
        new(["title", "message", "priority", "type", "agent", "project"], StringComparer.Ordinal);

    public static ProviderFormResult Build(string kind, ProviderFormInput input, ProviderProfile? existing)
    {
        var descriptor = ProviderFormCatalog.Find(kind)
            ?? throw new ArgumentException("Select a supported provider type.");
        if (existing is not null && !string.Equals(existing.Kind, descriptor.Kind, StringComparison.Ordinal))
            throw new ArgumentException("The provider type cannot be changed on a saved profile.");

        var form = new Form(descriptor, input, existing);
        return descriptor.Kind switch
        {
            "webhook" => BuildWebhook(form),
            "smtp" => BuildSmtp(form),
            "telegram" => BuildTelegram(form),
            "discord" => BuildDiscord(form),
            "slack" => BuildSlack(form),
            "teams" => BuildWebhookUrlOnly(form, "Enter the current Microsoft Teams Workflows webhook URL."),
            "zoho_cliq" => BuildWebhookUrlOnly(form, "Enter the Zoho Cliq webhook URL generated from Webhook Tokens."),
            "google_chat" => BuildGoogleChat(form),
            "mattermost" => BuildMattermost(form),
            "matrix" => BuildMatrix(form),
            "ntfy" => BuildNtfy(form),
            "gotify" => BuildGotify(form),
            "pushover" => BuildPushover(form),
            "pushbullet" => BuildPushbullet(form),
            "twilio_sms" => BuildTwilioSms(form),
            "whatsapp_cloud" => BuildWhatsAppCloud(form),
            "twilio_whatsapp" => BuildTwilioWhatsApp(form),
            "mqtt" => BuildMqtt(form),
            "relay" => BuildRelay(form),
            _ => throw new ArgumentException("Select a supported provider type.")
        };
    }

    private static ProviderFormResult BuildWebhook(Form form)
    {
        form.RequireSecret("endpoint_url", "Enter the webhook HTTPS endpoint.");
        var keepAuthorization = form.RawSecret("authorization").Length > 0 ||
                                form.HasStored("authorization") && !form.Clears("authorization");
        var keepHmac = form.RawSecret("hmac_secret").Length > 0 ||
                       form.HasStored("hmac_secret") && !form.Clears("hmac_secret");

        var config = Serialize(new
        {
            urlSecretName = "endpoint_url",
            allowPrivateNetwork = form.AllowPrivate,
            secretHeaders = keepAuthorization
                ? new Dictionary<string, string> { ["Authorization"] = "authorization" }
                : null,
            signature = keepHmac ? new { secretName = "hmac_secret" } : null
        });

        form.AddTrimmedSecret("endpoint_url");
        form.AddRawSecret("authorization");
        form.AddRawSecret("hmac_secret");
        if (form.Clears("authorization")) form.Remove("authorization");
        if (form.Clears("hmac_secret")) form.Remove("hmac_secret");
        return form.Result(config);
    }

    private static ProviderFormResult BuildSmtp(Form form)
    {
        form.RequireSecret("username", "Enter the SMTP username.");
        if (form.RawSecret("password").Length == 0 && !form.HasStored("password"))
            throw new ArgumentException("Enter the SMTP password or app password.");
        if (!int.TryParse(form.Text("port"), out var port) || port is < 1 or > 65535)
            throw new ArgumentException("SMTP port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(form.Text("host")))
            throw new ArgumentException("Enter the SMTP host.");
        if (string.IsNullOrWhiteSpace(form.Text("from_address")))
            throw new ArgumentException("Enter the From email address.");
        var recipients = form.Text("recipients")
            .Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (recipients.Length is 0 or > 10)
            throw new ArgumentException("Enter one to ten recipient email addresses.");

        var config = Serialize(new
        {
            host = form.Text("host").Trim(),
            port,
            security = form.Choice("security", ["start_tls", "tls"]),
            allowPrivateNetwork = form.AllowPrivate,
            fromAddress = form.Text("from_address").Trim(),
            fromName = form.Text("from_name").Trim(),
            recipients,
            subjectPrefix = form.Text("subject_prefix"),
            usernameSecretName = "username",
            passwordSecretName = "password"
        });
        form.AddTrimmedSecret("username");
        form.AddRawSecret("password");
        return form.Result(config);
    }

    private static ProviderFormResult BuildTelegram(Form form)
    {
        form.RequireSecret("bot_token", "Enter the Telegram bot token.");
        form.RequireSecret("chat_id", "Enter the Telegram chat ID or @channel username.");
        int? threadId = null;
        if (!string.IsNullOrWhiteSpace(form.Text("message_thread_id")))
        {
            if (!int.TryParse(form.Text("message_thread_id"), out var parsed) || parsed <= 0)
                throw new ArgumentException("Telegram topic/thread ID must be a positive integer.");
            threadId = parsed;
        }

        var config = Serialize(new
        {
            botTokenSecretName = "bot_token",
            chatIdSecretName = "chat_id",
            messageThreadId = threadId,
            disableNotification = form.Bool("disable_notification"),
            protectContent = form.Bool("protect_content")
        });
        form.AddTrimmedSecret("bot_token");
        form.AddTrimmedSecret("chat_id");
        return form.Result(config);
    }

    private static ProviderFormResult BuildDiscord(Form form)
    {
        form.RequireSecret("webhook_url", "Enter the Discord incoming-webhook URL.");
        var username = form.Text("username").Trim();
        if (username.Length is 0 or > 80 || username.Any(char.IsControl))
            throw new ArgumentException("Discord webhook display name must contain 1 to 80 characters.");
        var threadId = string.IsNullOrWhiteSpace(form.Text("thread_id")) ? null : form.Text("thread_id").Trim();
        if (threadId is not null &&
            (threadId.Length is < 5 or > 20 || !threadId.All(char.IsAsciiDigit) || !threadId.Any(c => c != '0')))
            throw new ArgumentException("Discord thread ID must be a valid numeric snowflake.");

        var config = Serialize(new { webhookUrlSecretName = "webhook_url", username, threadId });
        form.AddTrimmedSecret("webhook_url");
        return form.Result(config);
    }

    private static ProviderFormResult BuildSlack(Form form)
    {
        form.RequireSecret("webhook_url", "Enter the Slack incoming-webhook URL.");
        var threadTimestamp = string.IsNullOrWhiteSpace(form.Text("thread_timestamp")) ? null : form.Text("thread_timestamp").Trim();
        if (threadTimestamp is not null)
        {
            var separator = threadTimestamp.IndexOf('.');
            if (separator is < 10 or > 20 || separator != threadTimestamp.LastIndexOf('.') ||
                !threadTimestamp[..separator].All(char.IsAsciiDigit) ||
                threadTimestamp[(separator + 1)..].Length != 6 ||
                !threadTimestamp[(separator + 1)..].All(char.IsAsciiDigit))
                throw new ArgumentException("Slack thread timestamp must look like 1712345678.123456.");
        }

        var config = Serialize(new { webhookUrlSecretName = "webhook_url", threadTimestamp });
        form.AddTrimmedSecret("webhook_url");
        return form.Result(config);
    }

    private static ProviderFormResult BuildWebhookUrlOnly(Form form, string missingMessage)
    {
        form.RequireSecret("webhook_url", missingMessage);
        form.AddTrimmedSecret("webhook_url");
        return form.Result(Serialize(new { webhookUrlSecretName = "webhook_url" }));
    }

    private static ProviderFormResult BuildGoogleChat(Form form)
    {
        form.RequireSecret("webhook_url", "Enter the incoming-webhook URL copied from Google Chat.");
        var threadKey = string.IsNullOrWhiteSpace(form.Text("thread_key")) ? null : form.Text("thread_key").Trim();
        if (threadKey is not null && (threadKey.Length > 4000 || threadKey.Any(char.IsControl)))
            throw new ArgumentException("Google Chat thread key must contain at most 4000 characters and no control characters.");

        var config = Serialize(new
        {
            webhookUrlSecretName = "webhook_url",
            threadKey,
            threadReplyPolicy = form.Choice("thread_reply_policy", ["fallback", "fail"])
        });
        form.AddTrimmedSecret("webhook_url");
        return form.Result(config);
    }

    private static ProviderFormResult BuildMattermost(Form form)
    {
        form.RequireSecret("webhook_url", "Enter the Mattermost incoming-webhook URL.");
        var config = Serialize(new
        {
            webhookUrlSecretName = "webhook_url",
            allowPrivateNetwork = form.AllowPrivate,
            silent = form.Bool("silent")
        });
        form.AddTrimmedSecret("webhook_url");
        return form.Result(config);
    }

    private static ProviderFormResult BuildMatrix(Form form)
    {
        if (string.IsNullOrWhiteSpace(form.Text("homeserver_base_url")))
            throw new ArgumentException("Enter the Matrix homeserver HTTPS base URL.");
        form.RequireSecret("access_token", "Enter the Matrix access token.");
        form.RequireSecret("room_id", "Enter the Matrix room ID.");
        var config = Serialize(new
        {
            homeserverBaseUrl = form.Text("homeserver_base_url").Trim(),
            allowPrivateNetwork = form.AllowPrivate,
            accessTokenSecretName = "access_token",
            roomIdSecretName = "room_id"
        });
        form.AddTrimmedSecret("access_token");
        form.AddTrimmedSecret("room_id");
        return form.Result(config);
    }

    private static ProviderFormResult BuildNtfy(Form form)
    {
        if (string.IsNullOrWhiteSpace(form.Text("server_base_url")))
            throw new ArgumentException("Enter the ntfy server HTTPS base URL.");
        form.RequireSecret("topic", "Enter the ntfy topic.");
        var willHaveToken = form.TrimmedSecret("access_token").Length > 0 ||
                            form.HasStored("access_token") && !form.Clears("access_token");
        if (!willHaveToken && !form.Bool("allow_unauthenticated_topic"))
            throw new ArgumentException("Enter an ntfy access token or explicitly allow unauthenticated publishing.");

        var config = Serialize(new
        {
            serverBaseUrl = form.Text("server_base_url").Trim(),
            allowPrivateNetwork = form.AllowPrivate,
            allowUnauthenticatedTopic = form.Bool("allow_unauthenticated_topic"),
            topicSecretName = "topic",
            accessTokenSecretName = "access_token"
        });
        form.AddTrimmedSecret("topic");
        form.AddTrimmedSecret("access_token");
        if (form.Clears("access_token")) form.Remove("access_token");
        return form.Result(config);
    }

    private static ProviderFormResult BuildGotify(Form form)
    {
        if (string.IsNullOrWhiteSpace(form.Text("server_base_url")))
            throw new ArgumentException("Enter the Gotify server HTTPS base URL.");
        form.RequireSecret("application_token", "Enter the Gotify application token.");
        var config = Serialize(new
        {
            serverBaseUrl = form.Text("server_base_url").Trim(),
            allowPrivateNetwork = form.AllowPrivate,
            applicationTokenSecretName = "application_token"
        });
        form.AddTrimmedSecret("application_token");
        return form.Result(config);
    }

    private static ProviderFormResult BuildPushover(Form form)
    {
        form.RequireSecret("application_token", "Enter the 30-character Pushover application API token.");
        form.RequireSecret("user_key", "Enter the 30-character Pushover user or delivery-group key.");
        var token = form.TrimmedSecret("application_token");
        if (token.Length > 0 && !IsPushoverKey(token))
            throw new ArgumentException("The Pushover application token must be 30 alphanumeric characters.");
        var userKey = form.TrimmedSecret("user_key");
        if (userKey.Length > 0 && !IsPushoverKey(userKey))
            throw new ArgumentException("The Pushover user/group key must be 30 alphanumeric characters.");
        if (!int.TryParse(form.Text("emergency_retry_seconds"), out var retry) || retry < 30)
            throw new ArgumentException("Emergency retry must be at least 30 seconds.");
        if (!int.TryParse(form.Text("emergency_expire_seconds"), out var expire) || expire is < 1 or > 10_800)
            throw new ArgumentException("Emergency expiry must be between 1 and 10800 seconds.");
        var sound = form.Text("sound").Trim();
        if (sound.Length > 64 || sound.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Pushover sound names may contain only letters, digits, underscore, and hyphen.");
        var device = form.TrimmedSecret("device");
        if (device.Length > 0 && (device.Length > 25 || device.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')))
            throw new ArgumentException("Pushover device names are at most 25 characters using letters, digits, underscore, and hyphen.");

        var config = Serialize(new
        {
            applicationTokenSecretName = "application_token",
            userKeySecretName = "user_key",
            deviceSecretName = "device",
            sound,
            criticalAsEmergency = form.Bool("critical_as_emergency"),
            emergencyRetrySeconds = retry,
            emergencyExpireSeconds = expire
        });
        form.AddTrimmedSecret("application_token");
        form.AddTrimmedSecret("user_key");
        form.AddTrimmedSecret("device");
        if (form.HasStored("device") && device.Length == 0 && form.Clears("device")) form.Remove("device");
        return form.Result(config);
    }

    private static bool IsPushoverKey(string value) =>
        value.Length == 30 && value.All(char.IsAsciiLetterOrDigit);

    private static ProviderFormResult BuildPushbullet(Form form)
    {
        var targetType = form.Choice("target_type", ["all", "device", "channel", "email"]);
        var previousTargetType = form.PreviousConfigString("targetType", "all");
        var target = form.TrimmedSecret("target");
        form.RequireSecret("access_token", "Enter the Pushbullet personal access token.");
        var token = form.TrimmedSecret("access_token");
        if (token.Length > 0 && !IsPrintableSecret(token))
            throw new ArgumentException("The Pushbullet token must be 16–256 printable characters without spaces.");
        if (!form.Bool("quota_acknowledged"))
            throw new ArgumentException("Acknowledge the Pushbullet monthly quota before saving.");
        if (targetType != "all" && target.Length == 0 &&
            (!form.HasStored("target") || !string.Equals(previousTargetType, targetType, StringComparison.Ordinal)))
            throw new ArgumentException("Enter the selected Pushbullet device ID, channel tag, or email target.");
        ValidatePushbulletTarget(targetType, target);

        var config = Serialize(new
        {
            accessTokenSecretName = "access_token",
            targetType,
            targetSecretName = "target",
            quotaAcknowledged = true
        });
        form.AddTrimmedSecret("access_token");
        if (targetType != "all") form.AddTrimmedSecret("target");
        if (targetType == "all" && form.HasStored("target")) form.Remove("target");
        return form.Result(config);
    }

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

        if (value.Length > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Device IDs and channel tags may contain only letters, digits, underscore, and hyphen.");
    }

    private static ProviderFormResult BuildTwilioSms(Form form)
    {
        var mode = form.Choice("credential_mode", ["api_key", "auth_token"]);
        var senderType = form.Choice("sender_type", ["messaging_service", "phone"]);
        var minimumPriority = form.Choice("minimum_priority", ["critical", "high", "normal", "low"]);
        var previousMode = form.PreviousConfigString("credentialMode", "api_key");
        var previousSenderType = form.PreviousConfigString("senderType", "messaging_service");
        form.RequireSecret("account_sid", "Enter the Twilio Account SID.");
        form.RequireSecret("credential_secret", "Enter the Twilio API Key secret or Auth Token.");
        form.RequireSecret("recipient", "Enter the one permitted SMS recipient.");
        form.RequireSecret("sender", "Enter the Twilio sender.");
        RequireApiKeySid(form, mode, previousMode);
        ValidateTwilioCredentials(form);
        var recipient = form.TrimmedSecret("recipient");
        if (recipient.Length > 0 && !IsE164(recipient))
            throw new ArgumentException("The SMS recipient must be an E.164 number such as +15551234567.");
        var sender = form.TrimmedSecret("sender");
        if (sender.Length > 0 && !(senderType == "phone" ? IsE164(sender) : IsTwilioSid(sender, "MG")))
            throw new ArgumentException("Enter a matching E.164 Twilio number or MG Messaging Service SID.");
        if (sender.Length == 0 && previousSenderType != senderType)
            throw new ArgumentException("Re-enter the sender after changing its type.");
        var validity = ValidityPeriod(form);
        if (!form.Bool("paid_send_consent"))
            throw new ArgumentException("Authorize paid SMS sends before saving this provider.");

        var config = Serialize(new
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
        });
        AddTwilioCredentialSecrets(form, mode);
        form.AddTrimmedSecret("recipient");
        form.AddTrimmedSecret("sender");
        return form.Result(config);
    }

    private static ProviderFormResult BuildWhatsAppCloud(Form form)
    {
        form.RequireSecret("phone_number_id", "Enter the WhatsApp phone-number ID.");
        form.RequireSecret("access_token", "Enter a Meta system-user access token.");
        form.RequireSecret("recipient", "Enter the one opted-in WhatsApp recipient.");

        var version = form.Text("api_version").Trim();
        if (!IsMetaGraphVersion(version))
            throw new ArgumentException("The Meta Graph version must look like v25.0 (major 1–99).");
        var phoneNumberId = form.TrimmedSecret("phone_number_id");
        if (phoneNumberId.Length > 0 && !(phoneNumberId.Length is >= 5 and <= 32 && phoneNumberId.All(char.IsAsciiDigit)))
            throw new ArgumentException("The WhatsApp phone-number ID must contain 5–32 digits.");
        var accessToken = form.RawSecret("access_token");
        if (accessToken.Length > 0 && (accessToken.Length is < 16 or > 2048 || accessToken.Any(c => c is <= ' ' or > '~')))
            throw new ArgumentException("The Meta access token must be 16–2048 printable characters without spaces.");
        var recipient = form.TrimmedSecret("recipient");
        if (recipient.Length > 0 && !IsE164(recipient))
            throw new ArgumentException("The WhatsApp recipient must be an E.164 number such as +15551234567.");
        var templateName = form.Text("template_name").Trim();
        if (!(templateName.Length is >= 1 and <= 512 && templateName.All(c => c is >= 'a' and <= 'z' || char.IsAsciiDigit(c) || c == '_')))
            throw new ArgumentException("The approved template name may contain lowercase letters, digits, and underscore only.");
        var languageCode = form.Text("language_code").Trim();
        if (!IsLanguageCode(languageCode))
            throw new ArgumentException("Enter a language code such as en or en_US.");
        var parameters = TemplateVariableList(form.Text("body_parameters"),
            "Use up to five unique allowed template variables in the approved order.");
        if (!form.Bool("recipient_opt_in_acknowledged"))
            throw new ArgumentException("Confirm that the recipient explicitly opted in.");
        if (!form.Bool("template_approved_acknowledged"))
            throw new ArgumentException("Confirm the approved template, language, and variable order.");
        if (!form.Bool("paid_send_consent"))
            throw new ArgumentException("Authorize paid WhatsApp template sends before saving this provider.");

        var config = Serialize(new
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
            minimumPriority = form.Choice("minimum_priority", ["critical", "high", "normal", "low"])
        });
        form.AddTrimmedSecret("phone_number_id");
        form.AddTrimmedSecret("access_token");
        form.AddTrimmedSecret("recipient");
        return form.Result(config);
    }

    private static ProviderFormResult BuildTwilioWhatsApp(Form form)
    {
        var mode = form.Choice("credential_mode", ["api_key", "auth_token"]);
        var previousMode = form.PreviousConfigString("credentialMode", "api_key");
        form.RequireSecret("account_sid", "Enter the Twilio Account SID.");
        form.RequireSecret("credential_secret", "Enter the Twilio API Key secret or Auth Token.");
        form.RequireSecret("recipient", "Enter the one opted-in WhatsApp recipient.");
        form.RequireSecret("messaging_service_sid", "Enter the WhatsApp-enabled Messaging Service SID.");
        form.RequireSecret("content_sid", "Enter the approved Content Template SID.");
        RequireApiKeySid(form, mode, previousMode);
        ValidateTwilioCredentials(form);
        var recipient = form.TrimmedSecret("recipient");
        if (recipient.Length > 0 && !IsE164(recipient))
            throw new ArgumentException("The WhatsApp recipient must be an E.164 number such as +15551234567.");
        var serviceSid = form.TrimmedSecret("messaging_service_sid");
        if (serviceSid.Length > 0 && !IsTwilioSid(serviceSid, "MG"))
            throw new ArgumentException("The Messaging Service SID must be MG followed by 32 hexadecimal characters.");
        var contentSid = form.TrimmedSecret("content_sid");
        if (contentSid.Length > 0 && !IsTwilioSid(contentSid, "HX"))
            throw new ArgumentException("The Content Template SID must be HX followed by 32 hexadecimal characters.");
        var variables = TemplateVariableList(form.Text("content_variables"),
            "Use up to five unique allowed Content variables in the approved numbered order.");
        var validity = ValidityPeriod(form);
        if (!form.Bool("recipient_opt_in_acknowledged"))
            throw new ArgumentException("Confirm that the recipient explicitly opted in.");
        if (!form.Bool("template_approved_acknowledged"))
            throw new ArgumentException("Confirm the approved HX template and exact variable order.");
        if (!form.Bool("text_only_template_acknowledged"))
            throw new ArgumentException("Confirm that the HX template is text-only.");
        if (!form.Bool("paid_send_consent"))
            throw new ArgumentException("Authorize paid Twilio WhatsApp sends before saving this provider.");

        var config = Serialize(new
        {
            accountSidSecretName = "account_sid",
            credentialMode = mode,
            credentialSidSecretName = "credential_sid",
            credentialSecretName = "credential_secret",
            recipientSecretName = "recipient",
            messagingServiceSidSecretName = "messaging_service_sid",
            contentSidSecretName = "content_sid",
            contentVariables = variables,
            recipientOptInAcknowledged = true,
            templateApprovedAcknowledged = true,
            textOnlyTemplateAcknowledged = true,
            paidSendConsent = true,
            minimumPriority = form.Choice("minimum_priority", ["critical", "high", "normal", "low"]),
            validityPeriodSeconds = validity
        });
        AddTwilioCredentialSecrets(form, mode);
        form.AddTrimmedSecret("recipient");
        form.AddTrimmedSecret("messaging_service_sid");
        form.AddTrimmedSecret("content_sid");
        return form.Result(config);
    }

    private static void RequireApiKeySid(Form form, string mode, string previousMode)
    {
        if (mode == "api_key" && form.TrimmedSecret("credential_sid").Length == 0 &&
            (!form.HasStored("credential_sid") || previousMode != "api_key"))
            throw new ArgumentException("Enter the Twilio API Key SID.");
    }

    private static void ValidateTwilioCredentials(Form form)
    {
        var accountSid = form.TrimmedSecret("account_sid");
        if (accountSid.Length > 0 && !IsTwilioSid(accountSid, "AC"))
            throw new ArgumentException("The Twilio Account SID must be AC followed by 32 hexadecimal characters.");
        var credentialSid = form.TrimmedSecret("credential_sid");
        if (credentialSid.Length > 0 && !IsTwilioSid(credentialSid, "SK"))
            throw new ArgumentException("The Twilio API Key SID must be SK followed by 32 hexadecimal characters.");
        var secret = form.RawSecret("credential_secret");
        if (!string.IsNullOrWhiteSpace(secret) && !IsPrintableSecret(secret))
            throw new ArgumentException("The Twilio credential secret must be 16–256 printable characters without spaces.");
    }

    private static void AddTwilioCredentialSecrets(Form form, string mode)
    {
        form.AddTrimmedSecret("account_sid");
        if (mode == "api_key") form.AddTrimmedSecret("credential_sid");
        form.AddTrimmedSecret("credential_secret");
        if (mode == "auth_token" && form.HasStored("credential_sid")) form.Remove("credential_sid");
    }

    private static int ValidityPeriod(Form form)
    {
        if (!int.TryParse(form.Text("validity_period_seconds"), out var validity) || validity is < 6 or > 36_000)
            throw new ArgumentException("Twilio queue validity must be between 6 and 36000 seconds.");
        return validity;
    }

    private static string[] TemplateVariableList(string value, string message)
    {
        var variables = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (variables.Length > 5 || variables.Any(v => !TemplateVariables.Contains(v)) ||
            variables.Distinct(StringComparer.Ordinal).Count() != variables.Length)
            throw new ArgumentException(message);
        return variables;
    }

    private static ProviderFormResult BuildMqtt(Form form)
    {
        var mode = form.Choice("authentication_mode",
            ["username_password", "client_certificate", "username_and_certificate", "anonymous"]);
        var previousMode = form.PreviousConfigString("authenticationMode", "username_password");
        form.RequireSecret("topic", "Enter the exact MQTT publish topic.");
        var usesUsername = mode is "username_password" or "username_and_certificate";
        var previouslyUsedUsername = previousMode is "username_password" or "username_and_certificate";
        var usesCertificate = mode is "client_certificate" or "username_and_certificate";
        var previouslyUsedCertificate = previousMode is "client_certificate" or "username_and_certificate";
        if (usesUsername)
        {
            if (form.TrimmedSecret("username").Length == 0 && (!form.HasStored("username") || !previouslyUsedUsername))
                throw new ArgumentException("Enter the MQTT username after selecting this authentication mode.");
            if (form.RawSecret("password").Length == 0 && (!form.HasStored("password") || !previouslyUsedUsername))
                throw new ArgumentException("Enter the MQTT password after selecting this authentication mode.");
        }
        if (usesCertificate && form.TrimmedSecret("client_certificate_thumbprint").Length == 0 &&
            (!form.HasStored("client_certificate_thumbprint") || !previouslyUsedCertificate))
            throw new ArgumentException("Enter the Current User client-certificate thumbprint.");
        if (mode == "anonymous" && !form.Bool("anonymous_acknowledged"))
            throw new ArgumentException("Explicitly acknowledge anonymous MQTT publishing.");

        var host = form.Text("broker_host").Trim();
        if (!IsMqttHost(host))
            throw new ArgumentException("Enter an ASCII DNS host or IP address without a scheme, path, or trailing dot.");
        if (!int.TryParse(form.Text("port"), out var port) || port is < 1 or > 65_535)
            throw new ArgumentException("MQTT TLS port must be between 1 and 65535.");
        var clientId = form.Text("client_id").Trim();
        if (clientId.Length is < 1 or > 64 || clientId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("MQTT client ID may contain 1–64 letters, digits, underscores, or hyphens.");
        var topic = form.TrimmedSecret("topic");
        if (topic.Length > 0) ValidateMqttTopic(topic);
        var username = form.RawSecret("username");
        if (!string.IsNullOrWhiteSpace(username) && (username.Length > 256 || username.Any(c => c == '\0' || char.IsControl(c))))
            throw new ArgumentException("MQTT username must be at most 256 non-control characters.");
        var password = form.RawSecret("password");
        if (password.Length > 0 && (password.Length > 4096 || password.Contains('\0')))
            throw new ArgumentException("MQTT password must be at most 4096 characters and cannot contain NUL.");
        var thumbprint = form.RawSecret("client_certificate_thumbprint");
        if (!string.IsNullOrWhiteSpace(thumbprint) && !IsCertificateThumbprint(thumbprint))
            throw new ArgumentException("The certificate thumbprint must contain 40 or 64 hexadecimal characters.");
        if (!int.TryParse(form.Text("qos"), out var qos) || qos is < 0 or > 2)
            throw new ArgumentException("Select a valid MQTT QoS.");
        if (qos > 0 && !form.Bool("duplicate_risk_acknowledged"))
            throw new ArgumentException("Acknowledge possible application-level duplicates for QoS 1/2.");
        if (!int.TryParse(form.Text("message_expiry_seconds"), out var expiry) || expiry is < 5 or > 86_400)
            throw new ArgumentException("MQTT message expiry must be between 5 and 86400 seconds.");

        var config = Serialize(new
        {
            brokerHost = host.ToLowerInvariant(),
            port,
            allowPrivateNetwork = form.AllowPrivate,
            clientId,
            topicSecretName = "topic",
            authenticationMode = mode,
            usernameSecretName = "username",
            passwordSecretName = "password",
            clientCertificateThumbprintSecretName = "client_certificate_thumbprint",
            anonymousAcknowledged = mode == "anonymous",
            qos,
            duplicateRiskAcknowledged = qos > 0,
            messageExpirySeconds = expiry
        });
        form.AddTrimmedSecret("topic");
        if (usesUsername)
        {
            form.AddTrimmedSecret("username");
            form.AddRawSecret("password");
        }
        if (usesCertificate && !string.IsNullOrWhiteSpace(thumbprint))
            form.SetSecret("client_certificate_thumbprint", NormalizeCertificateThumbprint(thumbprint));
        if (!usesUsername)
        {
            form.Remove("username");
            form.Remove("password");
        }
        if (!usesCertificate) form.Remove("client_certificate_thumbprint");
        return form.Result(config);
    }

    private static ProviderFormResult BuildRelay(Form form)
    {
        var pairing = form.Pairing;
        var entered = form.TrimmedSecret("installation_token");
        var selectedToken = pairing?.InstallationToken ?? (entered.Length == 0 ? null : entered);
        if (selectedToken is null && !form.HasStored("installation_token"))
            throw new ArgumentException("Press Connect to link this computer to your relay, or enter a token under Advanced.");
        if (selectedToken is not null && !RelayChannelAdapter.IsInstallationToken(selectedToken))
            throw new ArgumentException("The Relay installation token must start with inst_ and contain base64url characters.");
        var removesStoredToken = selectedToken is null && form.Clears("installation_token") && form.HasStored("installation_token");

        var relayUrl = form.Text("relay_url").Trim();
        if (relayUrl.Length == 0)
            throw new ArgumentException("Enter the Relay server base URL.");
        if (relayUrl.Length > 2048)
            throw new ArgumentException("Relay base URL is too long.");
        RelayChannelAdapter.ValidateRelayUrl(relayUrl, form.AllowPrivate);
        var senderName = form.Text("sender_name").Trim();
        if (senderName.Length > 100)
            throw new ArgumentException("Relay sender name must be at most 100 characters.");
        if (senderName.Any(char.IsControl))
            throw new ArgumentException("Relay sender name contains invalid characters.");

        var config = Serialize(new
        {
            deployment = "custom",
            relay_url = relayUrl,
            sender_name = senderName.Length == 0 ? null : senderName,
            allowPrivateNetwork = form.AllowPrivate,
            installation_id = pairing?.InstallationId ?? form.PreviousConfigValue("installation_id"),
            relay_name = pairing?.RelayName ?? form.PreviousConfigValue("relay_name"),
            // Kept across saves so the relay recognises this machine when it reconnects.
            install_id = pairing?.InstallId ?? form.PreviousConfigValue("install_id")
        });
        if (selectedToken is not null) form.SetSecret("installation_token", selectedToken);
        if (removesStoredToken) form.Remove("installation_token");
        return form.Result(config);
    }

    internal static bool IsMqttHost(string value)
    {
        if (value.Length is < 1 or > 253 || value.EndsWith('.') || value.Any(c => c > 127)) return false;
        if (System.Net.IPAddress.TryParse(value, out _)) return true;
        return Uri.CheckHostName(value) == UriHostNameType.Dns && value.Split('.').All(label =>
            label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private static void ValidateMqttTopic(string value)
    {
        if (value.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(value) > 512 ||
            value[0] is '/' or '$' || value[^1] == '/' || value.Contains("//", StringComparison.Ordinal) ||
            value.Any(c => c is '\0' or '+' or '#' || char.IsControl(c)))
            throw new ArgumentException("MQTT topic must be a fixed non-system topic without wildcards, empty levels, or controls.");
    }

    private static bool IsCertificateThumbprint(string value)
    {
        var normalized = NormalizeCertificateThumbprint(value);
        return normalized.Length is 40 or 64 && normalized.All(Uri.IsHexDigit);
    }

    private static string NormalizeCertificateThumbprint(string value) =>
        new(value.Where(c => !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());

    private static bool IsTwilioSid(string value, string prefix) =>
        value.Length == 34 && value.StartsWith(prefix, StringComparison.Ordinal) && value[2..].All(Uri.IsHexDigit);

    private static bool IsE164(string value) =>
        value.Length is >= 9 and <= 16 && value[0] == '+' && value[1] is >= '1' and <= '9' && value[2..].All(char.IsAsciiDigit);

    private static bool IsPrintableSecret(string value) =>
        value.Length is >= 16 and <= 256 && value.All(c => c is >= '!' and <= '~');

    private static bool IsMetaGraphVersion(string value)
    {
        if (!value.StartsWith('v') || !value.EndsWith(".0", StringComparison.Ordinal)) return false;
        return int.TryParse(value.AsSpan(1, value.Length - 3), out var major) && major is >= 1 and <= 99;
    }

    private static bool IsLanguageCode(string value)
    {
        var parts = value.Split('_');
        return parts.Length is 1 or 2 && parts[0].Length is 2 or 3 &&
               parts[0].All(c => c is >= 'a' and <= 'z') &&
               (parts.Length == 1 || parts[1].Length == 2 && parts[1].All(c => c is >= 'A' and <= 'Z'));
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json.Options);

    /// <summary>Submitted values with catalog defaults applied, plus the secret changes being collected.</summary>
    private sealed class Form
    {
        private readonly ProviderKindDescriptor _descriptor;
        private readonly ProviderFormInput _input;
        private readonly ProviderProfile? _existing;
        private readonly Dictionary<string, string> _changes = new(StringComparer.Ordinal);
        private readonly List<string> _removals = [];

        public Form(ProviderKindDescriptor descriptor, ProviderFormInput input, ProviderProfile? existing)
        {
            _descriptor = descriptor;
            _input = input;
            _existing = existing;
        }

        public RelayPairingOutcome? Pairing => _input.Pairing;

        public bool AllowPrivate => Bool("allow_private_network");

        public string Text(string key)
        {
            if (_input.Values.TryGetValue(key, out var value))
                return value ?? "";
            return _descriptor.Fields.FirstOrDefault(field => field.Key == key)?.Default ?? "";
        }

        public bool Bool(string key) =>
            Text(key).Trim().ToLowerInvariant() is "true" or "on" or "1" or "yes";

        /// <summary>The submitted value when it is one of <paramref name="allowed"/>, otherwise the field default.</summary>
        public string Choice(string key, IReadOnlyList<string> allowed)
        {
            var value = Text(key).Trim();
            if (allowed.Contains(value, StringComparer.Ordinal)) return value;
            var fallback = _descriptor.Fields.FirstOrDefault(field => field.Key == key)?.Default;
            if (fallback is not null && allowed.Contains(fallback, StringComparer.Ordinal) && value.Length == 0) return fallback;
            throw new ArgumentException($"Select a valid value for {Label(key)}.");
        }

        public string RawSecret(string key) =>
            _input.Secrets.TryGetValue(key, out var value) ? value ?? "" : "";

        public string TrimmedSecret(string key) => RawSecret(key).Trim();

        public bool HasStored(string name) =>
            _existing?.SecretNames.Contains(name, StringComparer.Ordinal) == true;

        public bool Clears(string name) => _input.ClearSecrets.Contains(name, StringComparer.Ordinal);

        public void RequireSecret(string name, string message)
        {
            if (TrimmedSecret(name).Length == 0 && !HasStored(name))
                throw new ArgumentException(message);
        }

        public void AddTrimmedSecret(string name)
        {
            var value = TrimmedSecret(name);
            if (value.Length > 0) _changes[name] = value;
        }

        public void AddRawSecret(string name)
        {
            var value = RawSecret(name);
            if (value.Length > 0) _changes[name] = value;
        }

        public void SetSecret(string name, string value) => _changes[name] = value;

        public void Remove(string name)
        {
            if (!_changes.ContainsKey(name) && !_removals.Contains(name, StringComparer.Ordinal))
                _removals.Add(name);
        }

        public string? PreviousConfigValue(string property)
        {
            var value = PreviousConfigString(property, "");
            return value.Length == 0 ? null : value;
        }

        public string PreviousConfigString(string property, string fallback)
        {
            if (_existing is null || string.IsNullOrWhiteSpace(_existing.ConfigJson)) return fallback;
            try
            {
                using var document = JsonDocument.Parse(_existing.ConfigJson);
                var value = JsonConfigReader.GetString(document.RootElement, property);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch (JsonException)
            {
                return fallback;
            }
        }

        public ProviderFormResult Result(string configJson) =>
            new(configJson, _changes, _existing is null ? [] : _removals);

        private string Label(string key) =>
            _descriptor.Fields.FirstOrDefault(field => field.Key == key)?.Label ?? key;
    }
}
