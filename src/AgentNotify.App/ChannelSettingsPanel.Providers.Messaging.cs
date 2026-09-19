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

    private async Task<ProviderProfile> SaveTwilioWhatsAppProviderAsync(ProviderProfile? existing)
    {
        var secretNames = existing?.SecretNames ?? [];
        var mode = (TwilioWhatsAppCredentialModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "api_key";
        var previousMode = ReadConfigString(existing?.ConfigJson, "credentialMode", "api_key");
        RequireSecret(TwilioWhatsAppAccountSidBox.Password, secretNames, "account_sid", "Enter the Twilio Account SID.");
        RequireSecret(TwilioWhatsAppCredentialSecretBox.Password, secretNames, "credential_secret", "Enter the Twilio API Key secret or Auth Token.");
        RequireSecret(TwilioWhatsAppRecipientBox.Password, secretNames, "recipient", "Enter the one opted-in WhatsApp recipient.");
        RequireSecret(TwilioWhatsAppServiceSidBox.Password, secretNames, "messaging_service_sid", "Enter the WhatsApp-enabled Messaging Service SID.");
        RequireSecret(TwilioWhatsAppContentSidBox.Password, secretNames, "content_sid", "Enter the approved Content Template SID.");
        if (mode == "api_key" && string.IsNullOrWhiteSpace(TwilioWhatsAppCredentialSidBox.Password) &&
            (!secretNames.Contains("credential_sid", StringComparer.Ordinal) || previousMode != "api_key"))
            throw new ArgumentException("Enter the Twilio API Key SID.");
        if (!string.IsNullOrWhiteSpace(TwilioWhatsAppAccountSidBox.Password) && !IsTwilioSid(TwilioWhatsAppAccountSidBox.Password.Trim(), "AC"))
            throw new ArgumentException("The Twilio Account SID must be AC followed by 32 hexadecimal characters.");
        if (!string.IsNullOrWhiteSpace(TwilioWhatsAppCredentialSidBox.Password) && !IsTwilioSid(TwilioWhatsAppCredentialSidBox.Password.Trim(), "SK"))
            throw new ArgumentException("The Twilio API Key SID must be SK followed by 32 hexadecimal characters.");
        if (!string.IsNullOrWhiteSpace(TwilioWhatsAppCredentialSecretBox.Password) && !IsPrintableSecret(TwilioWhatsAppCredentialSecretBox.Password))
            throw new ArgumentException("The Twilio credential secret must be 16–256 printable characters without spaces.");
        if (!string.IsNullOrWhiteSpace(TwilioWhatsAppRecipientBox.Password) && !IsE164(TwilioWhatsAppRecipientBox.Password.Trim()))
            throw new ArgumentException("The WhatsApp recipient must be an E.164 number such as +15551234567.");
        if (!string.IsNullOrWhiteSpace(TwilioWhatsAppServiceSidBox.Password) && !IsTwilioSid(TwilioWhatsAppServiceSidBox.Password.Trim(), "MG"))
            throw new ArgumentException("The Messaging Service SID must be MG followed by 32 hexadecimal characters.");
        if (!string.IsNullOrWhiteSpace(TwilioWhatsAppContentSidBox.Password) && !IsTwilioSid(TwilioWhatsAppContentSidBox.Password.Trim(), "HX"))
            throw new ArgumentException("The Content Template SID must be HX followed by 32 hexadecimal characters.");
        var variables = TwilioWhatsAppVariablesBox.Text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var allowedVariables = new HashSet<string>(["title", "message", "priority", "type", "agent", "project"], StringComparer.Ordinal);
        if (variables.Length > 5 || variables.Any(value => !allowedVariables.Contains(value)) ||
            variables.Distinct(StringComparer.Ordinal).Count() != variables.Length)
            throw new ArgumentException("Use up to five unique allowed Content variables in the approved numbered order.");
        if (!int.TryParse(TwilioWhatsAppValidityBox.Text, out var validity) || validity is < 6 or > 36_000)
            throw new ArgumentException("Twilio queue validity must be between 6 and 36000 seconds.");
        if (TwilioWhatsAppOptInBox.IsChecked != true)
            throw new ArgumentException("Confirm that the recipient explicitly opted in.");
        if (TwilioWhatsAppTemplateApprovedBox.IsChecked != true)
            throw new ArgumentException("Confirm the approved HX template and exact variable order.");
        if (TwilioWhatsAppTextOnlyBox.IsChecked != true)
            throw new ArgumentException("Confirm that the HX template is text-only.");
        if (TwilioWhatsAppPaidConsentBox.IsChecked != true)
            throw new ArgumentException("Authorize paid Twilio WhatsApp sends before saving this provider.");
        var minimumPriority = (TwilioWhatsAppMinimumPriorityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "critical";

        var config = JsonSerializer.Serialize(new
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
            minimumPriority,
            validityPeriodSeconds = validity
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        AddSecret(changes, "account_sid", TwilioWhatsAppAccountSidBox.Password);
        AddSecret(changes, "credential_sid", TwilioWhatsAppCredentialSidBox.Password);
        AddSecret(changes, "credential_secret", TwilioWhatsAppCredentialSecretBox.Password);
        AddSecret(changes, "recipient", TwilioWhatsAppRecipientBox.Password);
        AddSecret(changes, "messaging_service_sid", TwilioWhatsAppServiceSidBox.Password);
        AddSecret(changes, "content_sid", TwilioWhatsAppContentSidBox.Password);
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "twilio_whatsapp",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
        {
            var remove = mode == "auth_token" && secretNames.Contains("credential_sid", StringComparer.Ordinal)
                ? new[] { "credential_sid" }
                : [];
            await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }
        TwilioWhatsAppAccountSidBox.Clear();
        TwilioWhatsAppCredentialSidBox.Clear();
        TwilioWhatsAppCredentialSecretBox.Clear();
        TwilioWhatsAppRecipientBox.Clear();
        TwilioWhatsAppServiceSidBox.Clear();
        TwilioWhatsAppContentSidBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveMqttProviderAsync(ProviderProfile? existing)
    {
        var secretNames = existing?.SecretNames ?? [];
        var mode = (MqttAuthenticationModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "username_password";
        var previousMode = ReadConfigString(existing?.ConfigJson, "authenticationMode", "username_password");
        RequireSecret(MqttTopicBox.Password, secretNames, "topic", "Enter the exact MQTT publish topic.");
        var usesUsername = mode is "username_password" or "username_and_certificate";
        var previouslyUsedUsername = previousMode is "username_password" or "username_and_certificate";
        var usesCertificate = mode is "client_certificate" or "username_and_certificate";
        var previouslyUsedCertificate = previousMode is "client_certificate" or "username_and_certificate";
        if (usesUsername)
        {
            if (string.IsNullOrWhiteSpace(MqttUsernameBox.Password) &&
                (!secretNames.Contains("username", StringComparer.Ordinal) || !previouslyUsedUsername))
                throw new ArgumentException("Enter the MQTT username after selecting this authentication mode.");
            if (string.IsNullOrEmpty(MqttPasswordBox.Password) &&
                (!secretNames.Contains("password", StringComparer.Ordinal) || !previouslyUsedUsername))
                throw new ArgumentException("Enter the MQTT password after selecting this authentication mode.");
        }
        if (usesCertificate && string.IsNullOrWhiteSpace(MqttCertificateThumbprintBox.Password) &&
            (!secretNames.Contains("client_certificate_thumbprint", StringComparer.Ordinal) || !previouslyUsedCertificate))
            throw new ArgumentException("Enter the Current User client-certificate thumbprint.");
        if (mode == "anonymous" && MqttAnonymousBox.IsChecked != true)
            throw new ArgumentException("Explicitly acknowledge anonymous MQTT publishing.");
        if (mode is not ("anonymous" or "username_password" or "client_certificate" or "username_and_certificate"))
            throw new ArgumentException("Select a supported MQTT authentication mode.");

        var host = MqttHostBox.Text.Trim();
        if (!IsMqttHost(host))
            throw new ArgumentException("Enter an ASCII DNS host or IP address without a scheme, path, or trailing dot.");
        if (!int.TryParse(MqttPortBox.Text, out var port) || port is < 1 or > 65_535)
            throw new ArgumentException("MQTT TLS port must be between 1 and 65535.");
        var clientId = MqttClientIdBox.Text.Trim();
        if (clientId.Length is < 1 or > 64 || clientId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("MQTT client ID may contain 1–64 letters, digits, underscores, or hyphens.");
        if (!string.IsNullOrWhiteSpace(MqttTopicBox.Password)) ValidateMqttTopic(MqttTopicBox.Password.Trim());
        if (!string.IsNullOrWhiteSpace(MqttUsernameBox.Password) &&
            (MqttUsernameBox.Password.Length > 256 || MqttUsernameBox.Password.Any(character => character == '\0' || char.IsControl(character))))
            throw new ArgumentException("MQTT username must be at most 256 non-control characters.");
        if (!string.IsNullOrEmpty(MqttPasswordBox.Password) &&
            (MqttPasswordBox.Password.Length > 4096 || MqttPasswordBox.Password.Contains('\0')))
            throw new ArgumentException("MQTT password must be at most 4096 characters and cannot contain NUL.");
        if (!string.IsNullOrWhiteSpace(MqttCertificateThumbprintBox.Password) &&
            !IsCertificateThumbprint(MqttCertificateThumbprintBox.Password))
            throw new ArgumentException("The certificate thumbprint must contain 40 or 64 hexadecimal characters.");
        var qosText = (MqttQosBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1";
        if (!int.TryParse(qosText, out var qos) || qos is < 0 or > 2)
            throw new ArgumentException("Select a valid MQTT QoS.");
        if (qos > 0 && MqttDuplicateRiskBox.IsChecked != true)
            throw new ArgumentException("Acknowledge possible application-level duplicates for QoS 1/2.");
        if (!int.TryParse(MqttExpiryBox.Text, out var expiry) || expiry is < 5 or > 86_400)
            throw new ArgumentException("MQTT message expiry must be between 5 and 86400 seconds.");

        var config = JsonSerializer.Serialize(new
        {
            brokerHost = host.ToLowerInvariant(),
            port,
            allowPrivateNetwork = AllowPrivateBox.IsChecked == true,
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
        }, Json.Options);
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        AddSecret(changes, "topic", MqttTopicBox.Password);
        if (usesUsername) AddSecret(changes, "username", MqttUsernameBox.Password);
        if (usesUsername && !string.IsNullOrEmpty(MqttPasswordBox.Password))
            changes["password"] = MqttPasswordBox.Password;
        if (usesCertificate && !string.IsNullOrWhiteSpace(MqttCertificateThumbprintBox.Password))
            changes["client_certificate_thumbprint"] = NormalizeCertificateThumbprint(MqttCertificateThumbprintBox.Password);
        var saved = await _profiles.SaveAsync(existing?.Id, ProviderNameBox.Text, "mqtt",
            ProviderEnabledBox.IsChecked == true, config, existing is null ? changes : null);
        if (existing is not null)
        {
            var remove = new List<string>();
            if (!usesUsername) { remove.Add("username"); remove.Add("password"); }
            if (!usesCertificate) remove.Add("client_certificate_thumbprint");
            await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }
        MqttTopicBox.Clear();
        MqttUsernameBox.Clear();
        MqttPasswordBox.Clear();
        MqttCertificateThumbprintBox.Clear();
        return saved;
    }

    private async Task<ProviderProfile> SaveRelayProviderAsync(ProviderProfile? existing)
    {
        var secretNames = existing?.SecretNames ?? [];
        var hasToken = secretNames.Contains("installation_token", StringComparer.Ordinal);
        var enteredToken = RelayInstallationTokenBox.Password.Trim();
        var selectedToken = _pendingRelayInstallationToken ??
                            (string.IsNullOrWhiteSpace(enteredToken) ? null : enteredToken);
        if (selectedToken is null && !hasToken)
            throw new ArgumentException("Press Connect to link this computer to your relay, or enter a token under Advanced.");
        if (selectedToken is not null && !IsRelayInstallationToken(selectedToken))
            throw new ArgumentException("The Relay installation token must start with inst_ and contain base64url characters.");
        var removesStoredToken = selectedToken is null &&
                                 ClearRelayTokenBox.IsChecked == true &&
                                 hasToken;

        var senderName = RelaySenderNameBox.Text.Trim();
        if (senderName.Length > 100)
            throw new ArgumentException("Relay sender name must be at most 100 characters.");
        if (senderName.Any(char.IsControl))
            throw new ArgumentException("Relay sender name contains invalid characters.");

        var config = JsonSerializer.Serialize(new
        {
            // Relay is hosted-only: the endpoint is not a user choice.
            relay_url = RelayChannelAdapter.HostedBaseUrl,
            sender_name = string.IsNullOrWhiteSpace(senderName) ? null : senderName,
            installation_id = _pendingRelayInstallationId ?? ReadRelayConfigValue(existing, "installation_id"),
            relay_name = _pendingRelayName ?? ReadRelayConfigValue(existing, "relay_name"),
            // Written back on every save so it outlives the credential it was
            // paired with. Losing it does not break anything; it only costs the
            // relay its ability to recognise this machine next time.
            install_id = _pendingRelayInstallId ?? ReadRelayConfigValue(existing, "install_id")
        }, Json.Options);

        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (selectedToken is not null)
            changes["installation_token"] = selectedToken;

        var saved = await _profiles.SaveAsync(
            existing?.Id,
            ProviderNameBox.Text,
            "relay",
            ProviderEnabledBox.IsChecked == true,
            config,
            existing is null ? changes : null);

        if (existing is not null)
        {
            var remove = removesStoredToken
                ? new[] { "installation_token" }
                : [];
            // If user typed a new token, update; if they checked remove, delete.
            if (changes.Count > 0 || remove.Length > 0)
                await _profiles.UpdateSecretsAsync(saved.Id, changes, remove);
        }

        RelayInstallationTokenBox.Clear();
        ClearRelayTokenBox.IsChecked = false;
        _pendingRelayInstallationToken = null;
        _pendingRelayInstallationId = null;
        _pendingRelayName = null;
        _pendingRelayInstallId = null;
        ResetRelayConnectionPresentation(hasStoredCredential: !removesStoredToken);
        return saved;
    }

    private static bool IsRelayInstallationToken(string value) =>
        RelayChannelAdapter.IsInstallationToken(value);

    private static string? ReadRelayConfigValue(ProviderProfile? profile, string propertyName)
    {
        if (profile is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var value = GetJsonString(document.RootElement, propertyName);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsMqttHost(string value)
    {
        if (value.Length is < 1 or > 253 || value.EndsWith('.') || value.Any(character => character > 127)) return false;
        if (System.Net.IPAddress.TryParse(value, out _)) return true;
        return Uri.CheckHostName(value) == UriHostNameType.Dns && value.Split('.').All(label =>
            label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static void ValidateMqttTopic(string value)
    {
        if (value.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(value) > 512 ||
            value[0] is '/' or '$' || value[^1] == '/' || value.Contains("//", StringComparison.Ordinal) ||
            value.Any(character => character is '\0' or '+' or '#' || char.IsControl(character)))
            throw new ArgumentException("MQTT topic must be a fixed non-system topic without wildcards, empty levels, or controls.");
    }

    private static bool IsCertificateThumbprint(string value)
    {
        var normalized = NormalizeCertificateThumbprint(value);
        return normalized.Length is 40 or 64 && normalized.All(Uri.IsHexDigit);
    }

    private static string NormalizeCertificateThumbprint(string value) =>
        new(value.Where(character => !char.IsWhiteSpace(character)).Select(char.ToUpperInvariant).ToArray());

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

}
