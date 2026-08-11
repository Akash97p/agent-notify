using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using AgentNotify.Contracts;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace AgentNotify.Core.Delivery.Channels;

public sealed record SmtpSendRequest(
    string Host,
    int Port,
    bool UseTlsOnConnect,
    bool AllowPrivateNetwork,
    string Username,
    string Password,
    string FromAddress,
    string FromName,
    IReadOnlyList<string> Recipients,
    string Subject,
    string TextBody,
    string DeliveryId);

public interface ISmtpSender
{
    Task<DeliveryResult> SendAsync(SmtpSendRequest request, CancellationToken cancellationToken);
}

public sealed class SmtpChannelAdapter : IOutboundChannelAdapter
{
    private readonly ISmtpSender _sender;

    public SmtpChannelAdapter(ISmtpSender? sender = null) =>
        _sender = sender ?? new MailKitSmtpSender();

    public string Kind => "smtp";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = ParseConfiguration(delivery.Profile.ConfigJson, delivery.Secrets);
            var content = ParsePayload(delivery.PayloadJson);
            var subject = SanitizeSubject($"{config.SubjectPrefix}{content.Title}");
            var body = BuildBody(content);
            return await _sender.SendAsync(new SmtpSendRequest(
                config.Host,
                config.Port,
                config.Security == "tls",
                config.AllowPrivateNetwork,
                delivery.Secrets[config.UsernameSecretName],
                delivery.Secrets[config.PasswordSecretName],
                config.FromAddress,
                config.FromName,
                config.Recipients,
                subject,
                body,
                delivery.OutboxId), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }
    }

    private static SmtpConfiguration ParseConfiguration(
        string configJson,
        IReadOnlyDictionary<string, string> secrets)
    {
        var config = JsonSerializer.Deserialize<SmtpConfiguration>(configJson, Json.Options) ??
            throw new ArgumentException("SMTP configuration is required.");
        config.Host = config.Host.Trim();
        if (config.Host.Length is 0 or > 253 || config.Host.ContainsAny('\r', '\n', ' ', '/', '\\'))
            throw new ArgumentException("SMTP host is invalid.");
        if (config.Port is < 1 or > 65535)
            throw new ArgumentException("SMTP port is invalid.");
        config.Security = config.Security.Trim().ToLowerInvariant().Replace('-', '_');
        if (config.Security is not ("start_tls" or "tls"))
            throw new ArgumentException("SMTP security must require STARTTLS or TLS-on-connect.");
        if (IPAddress.TryParse(config.Host, out var literal) &&
            !WebhookChannelAdapter.IsAddressAllowed(literal, config.AllowPrivateNetwork))
            throw new ArgumentException("SMTP destination address is not allowed.");
        config.FromAddress = NormalizeMailbox(config.FromAddress);
        if (config.FromName.Length > 100 || ContainsNewLine(config.FromName))
            throw new ArgumentException("SMTP sender name is invalid.");
        if (config.Recipients is null || config.Recipients.Count is 0 or > 10)
            throw new ArgumentException("SMTP requires one to ten valid allowlisted recipients.");
        config.Recipients = config.Recipients
            .Select(NormalizeMailbox)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (config.SubjectPrefix.Length > 80 || ContainsNewLine(config.SubjectPrefix))
            throw new ArgumentException("SMTP subject prefix is invalid.");
        if (!secrets.TryGetValue(config.UsernameSecretName, out var username) ||
            string.IsNullOrWhiteSpace(username) || ContainsNewLine(username) ||
            !secrets.TryGetValue(config.PasswordSecretName, out var password) ||
            string.IsNullOrEmpty(password))
            throw new ArgumentException("SMTP encrypted username and password are required.");
        return config;
    }

    private static NotificationEmailContent ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");
        return new NotificationEmailContent(
            ReadString(root, "title", required: true),
            ReadString(root, "message", required: false),
            ReadString(root, "type", required: false),
            ReadString(root, "priority", required: false),
            ReadString(root, "agent", required: false),
            ReadString(root, "project", required: false));
    }

    private static string BuildBody(NotificationEmailContent content)
    {
        var builder = new StringBuilder();
        builder.AppendLine(content.Title).AppendLine();
        if (!string.IsNullOrWhiteSpace(content.Message))
            builder.AppendLine(content.Message).AppendLine();
        AppendField(builder, "Priority", content.Priority);
        AppendField(builder, "Type", content.Type);
        AppendField(builder, "Agent", content.Agent);
        AppendField(builder, "Project", content.Project);
        builder.AppendLine().Append("Sent by AgentNotify.");
        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append(label).Append(": ").AppendLine(value);
    }

    private static string SanitizeSubject(string subject)
    {
        var sanitized = subject.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return sanitized.Length <= 180 ? sanitized : sanitized[..177] + "…";
    }

    private static string ReadString(JsonElement root, string name, bool required)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new ArgumentException($"Notification {name} is required.");
            return "";
        }
        if (element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Notification {name} must be text.");
        return element.GetString() ?? "";
    }

    private static bool ContainsNewLine(string value) => value.Contains('\r') || value.Contains('\n');

    private static string NormalizeMailbox(string value)
    {
        if (ContainsNewLine(value) || !MailboxAddress.TryParse(value, out var mailbox))
            throw new ArgumentException("SMTP mailbox address is invalid.");
        var address = mailbox.Address;
        var separator = address.LastIndexOf('@');
        if (address.Length > 254 || separator <= 0 || separator == address.Length - 1)
            throw new ArgumentException("SMTP mailbox address must contain a local part and domain.");
        return address;
    }

    private sealed class SmtpConfiguration
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 587;
        public string Security { get; set; } = "start_tls";
        public bool AllowPrivateNetwork { get; set; }
        public string FromAddress { get; set; } = "";
        public string FromName { get; set; } = "AgentNotify";
        public List<string> Recipients { get; set; } = [];
        public string SubjectPrefix { get; set; } = "[AgentNotify] ";
        public string UsernameSecretName { get; set; } = "username";
        public string PasswordSecretName { get; set; } = "password";
    }

    private sealed record NotificationEmailContent(
        string Title,
        string Message,
        string Type,
        string Priority,
        string Agent,
        string Project);
}

public sealed class MailKitSmtpSender : ISmtpSender
{
    public async Task<DeliveryResult> SendAsync(
        SmtpSendRequest request,
        CancellationToken cancellationToken)
    {
        Socket? socket = null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(
                request.Host,
                AddressFamily.Unspecified,
                cancellationToken);
            var allowed = addresses
                .Where(address => WebhookChannelAdapter.IsAddressAllowed(address, request.AllowPrivateNetwork))
                .ToArray();
            if (allowed.Length == 0 || allowed.Length != addresses.Length)
                return DeliveryResult.PermanentFailure("destination_blocked");

            socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(allowed, request.Port, cancellationToken);
            using var client = new MailKit.Net.Smtp.SmtpClient
            {
                CheckCertificateRevocation = true,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                Timeout = 15000
            };
            await client.ConnectAsync(
                socket,
                request.Host,
                request.Port,
                request.UseTlsOnConnect ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                cancellationToken);
            socket = null; // MailKit now owns the connected socket.
            if (!client.IsSecure)
                return DeliveryResult.PermanentFailure("tls_required");
            await client.AuthenticateAsync(request.Username, request.Password, cancellationToken);

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(request.FromName, request.FromAddress));
            foreach (var recipient in request.Recipients)
                message.To.Add(MailboxAddress.Parse(recipient));
            message.Subject = request.Subject;
            message.MessageId = $"{request.DeliveryId}@agentnotify.local";
            message.Body = new TextPart("plain") { Text = request.TextBody };
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return DeliveryResult.Success(250);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is MailKit.Security.AuthenticationException or SaslException)
        {
            return DeliveryResult.PermanentFailure("authentication_failed");
        }
        catch (Exception exception) when (exception is SslHandshakeException or NotSupportedException)
        {
            return DeliveryResult.PermanentFailure("tls_failed");
        }
        catch (SmtpCommandException exception)
        {
            var status = (int)exception.StatusCode;
            return status is >= 400 and < 500
                ? DeliveryResult.Retry($"smtp_{status}", status)
                : DeliveryResult.PermanentFailure($"smtp_{status}", status);
        }
        catch (Exception exception) when (exception is SocketException or IOException or SmtpProtocolException)
        {
            return DeliveryResult.Retry("network_error");
        }
        finally
        {
            socket?.Dispose();
        }
    }
}

internal static class SmtpStringExtensions
{
    public static bool ContainsAny(this string value, params char[] characters) =>
        value.IndexOfAny(characters) >= 0;
}
