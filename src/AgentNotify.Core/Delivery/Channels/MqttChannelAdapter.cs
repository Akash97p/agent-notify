using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AgentNotify.Contracts;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class MqttChannelAdapter : IOutboundChannelAdapter
{
    private readonly IMqttPublisher _publisher;

    public MqttChannelAdapter(IMqttPublisher? publisher = null)
    {
        _publisher = publisher ?? new MqttNetPublisher();
    }

    public string Kind => "mqtt";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        MqttPublishRequest publish;
        try
        {
            var config = JsonSerializer.Deserialize<MqttConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("MQTT configuration is required.");
            publish = BuildRequest(config, delivery);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        MqttPublishOutcome outcome;
        try
        {
            outcome = await _publisher.PublishAsync(publish, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return publish.Qos == 0
                ? DeliveryResult.PermanentFailure("mqtt_ambiguous_failure")
                : DeliveryResult.Retry("mqtt_timeout");
        }
        catch (Exception exception) when (exception is IOException or AuthenticationException or HttpRequestException)
        {
            return publish.Qos == 0
                ? DeliveryResult.PermanentFailure("mqtt_ambiguous_failure")
                : DeliveryResult.Retry("mqtt_network_error");
        }

        if (outcome.Succeeded) return DeliveryResult.Success();
        var code = SanitizeOutcomeCode(outcome.ErrorCode);
        return outcome.Retryable ? DeliveryResult.Retry(code) : DeliveryResult.PermanentFailure(code);
    }

    private static string SanitizeOutcomeCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
            ? value
            : "mqtt_failure";

    private static MqttPublishRequest BuildRequest(MqttConfiguration config, OutboundDelivery delivery)
    {
        var host = ValidateHost(config.BrokerHost ?? "");
        if (config.Port is < 1 or > 65_535)
            throw new ArgumentException("MQTT broker port is invalid.");
        var clientId = (config.ClientId ?? "").Trim();
        if (!IsClientId(clientId)) throw new ArgumentException("MQTT client ID is invalid.");
        if (config.Qos is < 0 or > 2) throw new ArgumentException("MQTT QoS is invalid.");
        if (config.Qos > 0 && !config.DuplicateRiskAcknowledged)
            throw new ArgumentException("MQTT duplicate-risk acknowledgement is required for QoS 1/2.");
        if (config.MessageExpirySeconds is < 5 or > 86_400)
            throw new ArgumentException("MQTT message expiry is invalid.");

        var topic = ReadSecret(delivery.Secrets, config.TopicSecretName, "topic").Trim();
        ValidateTopic(topic);
        var authentication = ReadAuthentication(config, delivery.Secrets);

        using var document = JsonDocument.Parse(delivery.PayloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("MQTT notification payload must be a JSON object.");
        var payloadBytes = Encoding.UTF8.GetByteCount(delivery.PayloadJson);
        if (payloadBytes is < 2 or > 16 * 1024)
            throw new ArgumentException("MQTT payload exceeded the local safety bound.");
        if (!IsOpaqueId(delivery.OutboxId) || !IsOpaqueId(delivery.NotificationId))
            throw new ArgumentException("MQTT delivery identifiers are invalid.");

        return new MqttPublishRequest(
            host,
            config.Port,
            config.AllowPrivateNetwork,
            clientId,
            topic,
            delivery.PayloadJson,
            config.Qos,
            config.MessageExpirySeconds,
            delivery.OutboxId,
            delivery.NotificationId,
            authentication.Username,
            authentication.Password,
            authentication.ClientCertificateThumbprint);
    }

    private static MqttAuthentication ReadAuthentication(
        MqttConfiguration config,
        IReadOnlyDictionary<string, string> secrets)
    {
        var mode = (config.AuthenticationMode ?? "").Trim().ToLowerInvariant();
        string? username = null;
        string? password = null;
        string? thumbprint = null;
        if (mode is "username_password" or "username_and_certificate")
        {
            username = ReadSecret(secrets, config.UsernameSecretName, "username");
            password = ReadSecret(secrets, config.PasswordSecretName, "password");
            if (username.Length is < 1 or > 256 || username.Any(character => character == '\0' || char.IsControl(character)))
                throw new ArgumentException("MQTT username is invalid.");
            if (password.Length is < 1 or > 4096 || password.Contains('\0'))
                throw new ArgumentException("MQTT password is invalid.");
        }
        if (mode is "client_certificate" or "username_and_certificate")
        {
            thumbprint = NormalizeThumbprint(ReadSecret(
                secrets,
                config.ClientCertificateThumbprintSecretName,
                "client-certificate thumbprint"));
        }
        if (mode == "anonymous" && !config.AnonymousAcknowledged)
            throw new ArgumentException("Anonymous MQTT acknowledgement is required.");
        if (mode is not ("anonymous" or "username_password" or "client_certificate" or "username_and_certificate"))
            throw new ArgumentException("MQTT authentication mode is invalid.");
        return new MqttAuthentication(username, password, thumbprint);
    }

    private static string ValidateHost(string value)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 253 || value.EndsWith('.') || value.Any(character => character > 127))
            throw new ArgumentException("MQTT broker host is invalid.");
        if (IPAddress.TryParse(value, out _)) return value;
        if (Uri.CheckHostName(value) != UriHostNameType.Dns || value.Split('.').Any(label =>
                label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')))
            throw new ArgumentException("MQTT broker host is invalid.");
        return value.ToLowerInvariant();
    }

    private static bool IsClientId(string value) =>
        value.Length is >= 1 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static void ValidateTopic(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (value.Length == 0 || bytes > 512 || value[0] is '/' or '$' || value[^1] == '/' ||
            value.Contains("//", StringComparison.Ordinal) || value.Any(character =>
                character is '\0' or '+' or '#' || char.IsControl(character)))
            throw new ArgumentException("MQTT topic is invalid.");
    }

    private static string NormalizeThumbprint(string value)
    {
        var normalized = new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray()).ToUpperInvariant();
        if (normalized.Length is not (40 or 64) || !normalized.All(Uri.IsHexDigit))
            throw new ArgumentException("MQTT client-certificate thumbprint is invalid.");
        return normalized;
    }

    private static bool IsOpaqueId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string ReadSecret(
        IReadOnlyDictionary<string, string> secrets,
        string secretName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(secretName) ||
            !secrets.TryGetValue(secretName, out var value) ||
            string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"An encrypted MQTT {description} is required.");
        return value;
    }

    private sealed class MqttConfiguration
    {
        public string BrokerHost { get; set; } = "";
        public int Port { get; set; } = 8883;
        public bool AllowPrivateNetwork { get; set; }
        public string ClientId { get; set; } = "agentnotify";
        public string TopicSecretName { get; set; } = "topic";
        public string AuthenticationMode { get; set; } = "username_password";
        public string UsernameSecretName { get; set; } = "username";
        public string PasswordSecretName { get; set; } = "password";
        public string ClientCertificateThumbprintSecretName { get; set; } = "client_certificate_thumbprint";
        public bool AnonymousAcknowledged { get; set; }
        public int Qos { get; set; } = 1;
        public bool DuplicateRiskAcknowledged { get; set; }
        public int MessageExpirySeconds { get; set; } = 300;
    }

    private sealed record MqttAuthentication(string? Username, string? Password, string? ClientCertificateThumbprint);
}

public sealed record MqttPublishRequest(
    string BrokerHost,
    int Port,
    bool AllowPrivateNetwork,
    string ClientId,
    string Topic,
    string PayloadJson,
    int Qos,
    int MessageExpirySeconds,
    string DeliveryId,
    string NotificationId,
    string? Username,
    string? Password,
    string? ClientCertificateThumbprint);

public sealed record MqttPublishOutcome(bool Succeeded, bool Retryable, string? ErrorCode = null)
{
    public static MqttPublishOutcome Success() => new(true, false);
    public static MqttPublishOutcome Retry(string code) => new(false, true, code);
    public static MqttPublishOutcome Permanent(string code) => new(false, false, code);
}

public interface IMqttPublisher
{
    Task<MqttPublishOutcome> PublishAsync(MqttPublishRequest request, CancellationToken cancellationToken);
}

public sealed class MqttNetPublisher : IMqttPublisher
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public MqttNetPublisher(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        _resolve = resolve ?? ((host, cancellationToken) => Dns.GetHostAddressesAsync(
            host,
            System.Net.Sockets.AddressFamily.Unspecified,
            cancellationToken));
    }

    public async Task<MqttPublishOutcome> PublishAsync(
        MqttPublishRequest request,
        CancellationToken cancellationToken)
    {
        var addresses = await _resolve(request.BrokerHost, cancellationToken);
        var allowed = addresses.Where(address =>
            WebhookChannelAdapter.IsAddressAllowed(address, request.AllowPrivateNetwork)).ToArray();
        if (addresses.Length == 0 || allowed.Length != addresses.Length)
            return MqttPublishOutcome.Permanent("mqtt_destination_blocked");

        X509Certificate2? clientCertificate = null;
        try
        {
            if (request.ClientCertificateThumbprint is not null)
            {
                clientCertificate = FindClientCertificate(request.ClientCertificateThumbprint);
                if (clientCertificate is null)
                    return MqttPublishOutcome.Permanent("mqtt_client_certificate_missing");
            }

            var factory = new MqttClientFactory();
            using var client = factory.CreateMqttClient();
            var connect = await client.ConnectAsync(
                BuildClientOptions(request, allowed[0], clientCertificate),
                cancellationToken);
            if (connect.ResultCode != MqttClientConnectResultCode.Success)
                return ClassifyConnect(connect.ResultCode);

            var qos = request.Qos switch
            {
                0 => MqttQualityOfServiceLevel.AtMostOnce,
                1 => MqttQualityOfServiceLevel.AtLeastOnce,
                2 => MqttQualityOfServiceLevel.ExactlyOnce,
                _ => throw new ArgumentOutOfRangeException(nameof(request.Qos))
            };
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(request.Topic)
                .WithPayload(request.PayloadJson)
                .WithQualityOfServiceLevel(qos)
                .WithRetainFlag(false)
                .WithContentType("application/json")
                .WithPayloadFormatIndicator(MqttPayloadFormatIndicator.CharacterData)
                .WithMessageExpiryInterval((uint)request.MessageExpirySeconds)
                .WithUserProperty("agentnotify-delivery-id", Encoding.UTF8.GetBytes(request.DeliveryId).AsMemory())
                .WithUserProperty("agentnotify-notification-id", Encoding.UTF8.GetBytes(request.NotificationId).AsMemory())
                .Build();
            var result = await client.PublishAsync(message, cancellationToken);
            try { await client.DisconnectAsync(cancellationToken: cancellationToken); }
            catch { /* A successful PUBACK/PUBCOMP remains authoritative. */ }
            return result.IsSuccess
                ? MqttPublishOutcome.Success()
                : ClassifyPublish(result.ReasonCode);
        }
        catch (OperationCanceledException)
        {
            return request.Qos == 0
                ? MqttPublishOutcome.Permanent("mqtt_ambiguous_failure")
                : MqttPublishOutcome.Retry("mqtt_timeout");
        }
        catch (AuthenticationException)
        {
            return MqttPublishOutcome.Permanent("mqtt_tls_failed");
        }
        catch (MQTTnet.Exceptions.MqttCommunicationException exception)
            when (ContainsAuthenticationException(exception))
        {
            return MqttPublishOutcome.Permanent("mqtt_tls_failed");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return MqttPublishOutcome.Permanent("mqtt_client_certificate_failed");
        }
        catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or MQTTnet.Exceptions.MqttCommunicationException)
        {
            return request.Qos == 0
                ? MqttPublishOutcome.Permanent("mqtt_ambiguous_failure")
                : MqttPublishOutcome.Retry("mqtt_network_error");
        }
        finally
        {
            clientCertificate?.Dispose();
        }
    }

    internal static MqttClientOptions BuildClientOptions(
        MqttPublishRequest request,
        IPAddress address,
        X509Certificate2? clientCertificate)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(request.ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithCleanStart()
            .WithSessionExpiryInterval(0)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(15))
            .WithMaximumPacketSize(32 * 1024)
            .WithEndPoint(new IPEndPoint(address, request.Port))
            .WithTlsOptions(options =>
            {
                options.UseTls()
                    .WithTargetHost(request.BrokerHost)
                    .WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13)
                    .WithRevocationMode(X509RevocationMode.Online)
                    .WithAllowUntrustedCertificates(false)
                    .WithIgnoreCertificateChainErrors(false)
                    .WithIgnoreCertificateRevocationErrors(false)
                    .WithCertificateValidationHandler(args => args.SslPolicyErrors == SslPolicyErrors.None);
                if (clientCertificate is not null)
                    options.WithClientCertificates([clientCertificate]);
            });
        if (request.Username is not null)
            builder.WithCredentials(request.Username, Encoding.UTF8.GetBytes(request.Password!));
        return builder.Build();
    }

    private static X509Certificate2? FindClientCertificate(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: true);
        return matches.OfType<X509Certificate2>().FirstOrDefault(certificate =>
            certificate.HasPrivateKey && SupportsClientAuthentication(certificate));
    }

    private static bool SupportsClientAuthentication(X509Certificate2 certificate)
    {
        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (keyUsage is not null && !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)) return false;
        var enhanced = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return enhanced is null || enhanced.EnhancedKeyUsages
            .OfType<Oid>()
            .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2");
    }

    private static bool ContainsAuthenticationException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is AuthenticationException) return true;
        return false;
    }

    private static MqttPublishOutcome ClassifyConnect(MqttClientConnectResultCode code) => code switch
    {
        MqttClientConnectResultCode.ServerUnavailable or
        MqttClientConnectResultCode.ServerBusy or
        MqttClientConnectResultCode.QuotaExceeded or
        MqttClientConnectResultCode.ConnectionRateExceeded => MqttPublishOutcome.Retry("mqtt_broker_unavailable"),
        MqttClientConnectResultCode.BadUserNameOrPassword or
        MqttClientConnectResultCode.NotAuthorized or
        MqttClientConnectResultCode.Banned => MqttPublishOutcome.Permanent("mqtt_authentication_failed"),
        _ => MqttPublishOutcome.Permanent("mqtt_connect_rejected")
    };

    private static MqttPublishOutcome ClassifyPublish(MqttClientPublishReasonCode code) => code switch
    {
        MqttClientPublishReasonCode.QuotaExceeded or
        MqttClientPublishReasonCode.ImplementationSpecificError or
        MqttClientPublishReasonCode.UnspecifiedError => MqttPublishOutcome.Retry("mqtt_publish_rejected"),
        MqttClientPublishReasonCode.NotAuthorized => MqttPublishOutcome.Permanent("mqtt_not_authorized"),
        MqttClientPublishReasonCode.TopicNameInvalid => MqttPublishOutcome.Permanent("mqtt_topic_rejected"),
        MqttClientPublishReasonCode.PayloadFormatInvalid => MqttPublishOutcome.Permanent("mqtt_payload_rejected"),
        _ => MqttPublishOutcome.Permanent("mqtt_publish_rejected")
    };
}
