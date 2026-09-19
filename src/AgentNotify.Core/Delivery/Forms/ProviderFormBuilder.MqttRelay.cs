using System.Text.Json;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery.Forms;

public static partial class ProviderFormBuilder
{
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

        var senderName = form.Text("sender_name").Trim();
        if (senderName.Length > 100)
            throw new ArgumentException("Relay sender name must be at most 100 characters.");
        if (senderName.Any(char.IsControl))
            throw new ArgumentException("Relay sender name contains invalid characters.");

        var config = Serialize(new
        {
            // Relay is hosted-only: the endpoint is not a user choice.
            relay_url = RelayChannelAdapter.HostedBaseUrl,
            sender_name = senderName.Length == 0 ? null : senderName,
            installation_id = pairing?.InstallationId ?? form.PreviousConfigValue("installation_id"),
            relay_name = pairing?.RelayName ?? form.PreviousConfigValue("relay_name"),
            // Kept across saves so the relay recognises this machine when it reconnects.
            install_id = pairing?.InstallId ?? form.PreviousConfigValue("install_id")
        });
        if (selectedToken is not null) form.SetSecret("installation_token", selectedToken);
        if (removesStoredToken) form.Remove("installation_token");
        return form.Result(config);
    }

}
