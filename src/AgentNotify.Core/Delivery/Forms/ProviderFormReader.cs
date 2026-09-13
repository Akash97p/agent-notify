using System.Text.Json;

namespace AgentNotify.Core.Delivery.Forms;

/// <summary>
/// Reads a stored provider profile back into editor values.
/// </summary>
/// <remarks>
/// Only non-secret values are returned. Secret fields are represented by
/// <see cref="ProviderProfile.SecretNames"/> alone; their plaintext never leaves the broker.
/// A malformed stored document yields the catalog defaults rather than an error, so one damaged
/// profile can still be opened, repaired, or deleted.
/// </remarks>
public static class ProviderFormReader
{
    public static IReadOnlyDictionary<string, string> ReadValues(ProviderProfile profile)
    {
        var descriptor = ProviderFormCatalog.Find(profile.Kind);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (descriptor is null)
            return values;

        foreach (var field in descriptor.Fields.Where(field => field.Type != ProviderFieldType.Secret))
            values[field.Key] = field.Default ?? (field.Type == ProviderFieldType.Checkbox ? "false" : "");

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(profile.ConfigJson) ? "{}" : profile.ConfigJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return values;
            if (values.ContainsKey("allow_private_network"))
                values["allow_private_network"] = Bool(
                    JsonConfigReader.GetBoolean(root, "allowPrivateNetwork", false) ||
                    JsonConfigReader.GetBoolean(root, "allow_private_network", false));
            Read(profile.Kind, root, values);
        }
        catch (JsonException)
        {
        }

        return values;
    }

    private static void Read(string kind, JsonElement root, Dictionary<string, string> values)
    {
        switch (kind)
        {
            case "smtp":
                values["host"] = JsonConfigReader.GetString(root, "host");
                values["port"] = IntOr(root, "port", "587");
                values["security"] = JsonConfigReader.GetString(root, "security") == "tls" ? "tls" : "start_tls";
                values["from_address"] = JsonConfigReader.GetString(root, "fromAddress");
                values["from_name"] = JsonConfigReader.GetString(root, "fromName");
                values["subject_prefix"] = JsonConfigReader.GetString(root, "subjectPrefix");
                values["recipients"] = string.Join('\n', JsonConfigReader.GetStringArray(root, "recipients"));
                break;
            case "telegram":
                values["message_thread_id"] = JsonConfigReader.GetInt32Text(root, "messageThreadId");
                values["disable_notification"] = Bool(JsonConfigReader.GetBoolean(root, "disableNotification", false));
                values["protect_content"] = Bool(JsonConfigReader.GetBoolean(root, "protectContent", true));
                break;
            case "discord":
                values["username"] = JsonConfigReader.GetString(root, "username");
                values["thread_id"] = JsonConfigReader.GetString(root, "threadId");
                break;
            case "slack":
                values["thread_timestamp"] = JsonConfigReader.GetString(root, "threadTimestamp");
                break;
            case "google_chat":
                values["thread_key"] = JsonConfigReader.GetString(root, "threadKey");
                values["thread_reply_policy"] = JsonConfigReader.GetString(root, "threadReplyPolicy") == "fail" ? "fail" : "fallback";
                break;
            case "mattermost":
                values["silent"] = Bool(JsonConfigReader.GetBoolean(root, "silent", false));
                break;
            case "matrix":
                values["homeserver_base_url"] = JsonConfigReader.GetString(root, "homeserverBaseUrl");
                break;
            case "ntfy":
                values["server_base_url"] = JsonConfigReader.GetString(root, "serverBaseUrl");
                values["allow_unauthenticated_topic"] = Bool(JsonConfigReader.GetBoolean(root, "allowUnauthenticatedTopic", false));
                break;
            case "gotify":
                values["server_base_url"] = JsonConfigReader.GetString(root, "serverBaseUrl");
                break;
            case "pushover":
                values["sound"] = JsonConfigReader.GetString(root, "sound");
                values["critical_as_emergency"] = Bool(JsonConfigReader.GetBoolean(root, "criticalAsEmergency", false));
                values["emergency_retry_seconds"] = IntOr(root, "emergencyRetrySeconds", "60");
                values["emergency_expire_seconds"] = IntOr(root, "emergencyExpireSeconds", "3600");
                break;
            case "pushbullet":
                values["target_type"] = JsonConfigReader.GetString(root, "targetType") switch
                {
                    "device" => "device",
                    "channel" => "channel",
                    "email" => "email",
                    _ => "all"
                };
                values["quota_acknowledged"] = Bool(JsonConfigReader.GetBoolean(root, "quotaAcknowledged", false));
                break;
            case "twilio_sms":
                values["credential_mode"] = JsonConfigReader.GetString(root, "credentialMode") == "auth_token" ? "auth_token" : "api_key";
                values["sender_type"] = JsonConfigReader.GetString(root, "senderType") == "phone" ? "phone" : "messaging_service";
                values["minimum_priority"] = Priority(root);
                values["validity_period_seconds"] = IntOr(root, "validityPeriodSeconds", "300");
                values["paid_send_consent"] = Bool(JsonConfigReader.GetBoolean(root, "paidSendConsent", false));
                break;
            case "whatsapp_cloud":
                var version = JsonConfigReader.GetString(root, "apiVersion");
                values["api_version"] = string.IsNullOrWhiteSpace(version) ? "v25.0" : version;
                values["template_name"] = JsonConfigReader.GetString(root, "templateName");
                values["language_code"] = JsonConfigReader.GetString(root, "languageCode");
                values["body_parameters"] = string.Join(',', JsonConfigReader.GetStringArray(root, "bodyParameters"));
                values["minimum_priority"] = Priority(root);
                values["recipient_opt_in_acknowledged"] = Bool(JsonConfigReader.GetBoolean(root, "recipientOptInAcknowledged", false));
                values["template_approved_acknowledged"] = Bool(JsonConfigReader.GetBoolean(root, "templateApprovedAcknowledged", false));
                values["paid_send_consent"] = Bool(JsonConfigReader.GetBoolean(root, "paidSendConsent", false));
                break;
            case "twilio_whatsapp":
                values["credential_mode"] = JsonConfigReader.GetString(root, "credentialMode") == "auth_token" ? "auth_token" : "api_key";
                values["content_variables"] = string.Join(',', JsonConfigReader.GetStringArray(root, "contentVariables"));
                values["minimum_priority"] = Priority(root);
                values["validity_period_seconds"] = IntOr(root, "validityPeriodSeconds", "300");
                values["recipient_opt_in_acknowledged"] = Bool(JsonConfigReader.GetBoolean(root, "recipientOptInAcknowledged", false));
                values["template_approved_acknowledged"] = Bool(JsonConfigReader.GetBoolean(root, "templateApprovedAcknowledged", false));
                values["text_only_template_acknowledged"] = Bool(JsonConfigReader.GetBoolean(root, "textOnlyTemplateAcknowledged", false));
                values["paid_send_consent"] = Bool(JsonConfigReader.GetBoolean(root, "paidSendConsent", false));
                break;
            case "mqtt":
                values["broker_host"] = JsonConfigReader.GetString(root, "brokerHost");
                values["port"] = IntOr(root, "port", "8883");
                values["client_id"] = JsonConfigReader.GetString(root, "clientId");
                values["authentication_mode"] = JsonConfigReader.GetString(root, "authenticationMode") switch
                {
                    "client_certificate" => "client_certificate",
                    "username_and_certificate" => "username_and_certificate",
                    "anonymous" => "anonymous",
                    _ => "username_password"
                };
                values["qos"] = JsonConfigReader.TryGetInt32(root, "qos", out var qos) ? Math.Clamp(qos, 0, 2).ToString() : "1";
                values["duplicate_risk_acknowledged"] = Bool(JsonConfigReader.GetBoolean(root, "duplicateRiskAcknowledged", false));
                values["anonymous_acknowledged"] = Bool(JsonConfigReader.GetBoolean(root, "anonymousAcknowledged", false));
                values["message_expiry_seconds"] = IntOr(root, "messageExpirySeconds", "300");
                break;
            case "relay":
                values["relay_url"] = FirstString(root, "relay_url", "relayUrl");
                values["sender_name"] = FirstString(root, "sender_name", "senderName");
                break;
        }
    }

    /// <summary>The Relay identity recorded by the last pairing, if any.</summary>
    public static string? ReadConfigString(ProviderProfile? profile, string property)
    {
        if (profile is null) return null;
        try
        {
            using var document = JsonDocument.Parse(profile.ConfigJson);
            var value = JsonConfigReader.GetString(document.RootElement, property);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FirstString(JsonElement root, string first, string second)
    {
        var value = JsonConfigReader.GetString(root, first);
        return value.Length > 0 ? value : JsonConfigReader.GetString(root, second);
    }

    private static string Priority(JsonElement root) =>
        JsonConfigReader.GetString(root, "minimumPriority") switch
        {
            "high" => "high",
            "normal" => "normal",
            "low" => "low",
            _ => "critical"
        };

    private static string IntOr(JsonElement root, string property, string fallback) =>
        JsonConfigReader.TryGetInt32(root, property, out var value) ? value.ToString() : fallback;

    private static string Bool(bool value) => value ? "true" : "false";
}
