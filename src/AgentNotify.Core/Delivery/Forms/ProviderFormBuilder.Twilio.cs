using System.Text.Json;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery.Forms;

public static partial class ProviderFormBuilder
{
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

}
