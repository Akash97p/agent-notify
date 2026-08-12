using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class TwilioSmsChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> TwilioRequest =
        new("AgentNotify.TwilioSms.ValidatedEndpoint");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public TwilioSmsChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "twilio_sms";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        Uri endpoint;
        AuthenticationHeaderValue authorization;
        IReadOnlyDictionary<string, string> fields;
        try
        {
            var config = JsonSerializer.Deserialize<TwilioSmsConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("Twilio SMS configuration is required.");
            if (!config.PaidSendConsent)
                throw new ArgumentException("Explicit paid-send consent is required.");
            var accountSid = ReadSid(delivery.Secrets, config.AccountSidSecretName, "AC");
            endpoint = new Uri($"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Messages.json");
            authorization = BuildAuthorization(config, delivery.Secrets, accountSid);
            fields = BuildFields(config, delivery.Secrets, delivery.PayloadJson, delivery.NotificationId);
        }
        catch (CostPolicyException)
        {
            return DeliveryResult.PermanentFailure("cost_policy_blocked");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Options.Set(TwilioRequest, true);
        request.Headers.Authorization = authorization;
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 200 and <= 299)
            {
                var acknowledgement = await ReadAcknowledgementAsync(response.Content, cancellationToken);
                return acknowledgement switch
                {
                    Acknowledgement.Accepted => DeliveryResult.Success(status),
                    Acknowledgement.Rejected => DeliveryResult.PermanentFailure("twilio_rejected", status),
                    _ => DeliveryResult.PermanentFailure("twilio_ambiguous_response", status)
                };
            }
            if (status == 429)
                return DeliveryResult.Retry("twilio_429", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("twilio_redirect", status);
            if (status >= 500 || status is 408 or 425)
                return DeliveryResult.PermanentFailure("twilio_ambiguous_failure", status);
            return DeliveryResult.PermanentFailure($"twilio_{status}", status);
        }
        catch (OperationCanceledException)
        {
            // The caller's timeout may race with server acceptance. Returning a terminal
            // result prevents the dispatcher from replaying and billing a second SMS.
            return DeliveryResult.PermanentFailure("twilio_ambiguous_failure");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            // Twilio does not document create-message idempotency. Once bytes may have
            // left the process, replay could create and bill a second SMS.
            return DeliveryResult.PermanentFailure("twilio_ambiguous_failure");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.PermanentFailure("twilio_ambiguous_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static AuthenticationHeaderValue BuildAuthorization(
        TwilioSmsConfiguration config,
        IReadOnlyDictionary<string, string> secrets,
        string accountSid)
    {
        var mode = config.CredentialMode.Trim().ToLowerInvariant();
        var username = mode switch
        {
            "api_key" => ReadSid(secrets, config.CredentialSidSecretName, "SK"),
            "auth_token" => accountSid,
            _ => throw new ArgumentException("Twilio credential mode is invalid.")
        };
        var secret = ReadSecret(secrets, config.CredentialSecretName, "Twilio credential secret");
        if (secret.Length is < 16 or > 256 || secret.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Twilio credential secret format is invalid.");
        var bytes = Encoding.ASCII.GetBytes($"{username}:{secret}");
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
    }

    private static IReadOnlyDictionary<string, string> BuildFields(
        TwilioSmsConfiguration config,
        IReadOnlyDictionary<string, string> secrets,
        string payloadJson,
        string notificationId)
    {
        var recipient = ReadSecret(secrets, config.RecipientSecretName, "Twilio SMS recipient").Trim();
        if (!IsE164(recipient)) throw new ArgumentException("Twilio recipient must use E.164 format.");
        var sender = ReadSecret(secrets, config.SenderSecretName, "Twilio SMS sender").Trim();
        var senderType = config.SenderType.Trim().ToLowerInvariant();
        if (senderType == "phone" && !IsE164(sender))
            throw new ArgumentException("Twilio sender must use E.164 format.");
        if (senderType == "messaging_service" && !IsSid(sender, "MG"))
            throw new ArgumentException("Twilio Messaging Service SID is invalid.");
        if (senderType is not ("phone" or "messaging_service"))
            throw new ArgumentException("Twilio sender type is invalid.");
        if (config.ValidityPeriodSeconds is < 6 or > 36_000)
            throw new ArgumentException("Twilio validity period is invalid.");

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");
        var priority = Read(root, "priority", false).Trim().ToLowerInvariant();
        if (!notificationId.Equals("test", StringComparison.Ordinal) &&
            PriorityRank(priority) < PriorityRank(config.MinimumPriority))
            throw new CostPolicyException();
        var body = BuildSingleSegmentBody(root, priority);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["To"] = recipient,
            [senderType == "phone" ? "From" : "MessagingServiceSid"] = sender,
            ["Body"] = body,
            ["ValidityPeriod"] = config.ValidityPeriodSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SmartEncoded"] = "true",
            ["ContentRetention"] = "discard",
            ["AddressRetention"] = "obfuscate"
        };
        return fields;
    }

    private static string BuildSingleSegmentBody(JsonElement root, string priority)
    {
        var title = Read(root, "title", true).Trim().Replace('\0', ' ');
        if (title.Length == 0) throw new ArgumentException("Notification title is required.");
        var message = Read(root, "message", false).Trim().Replace('\0', ' ');
        var prefix = priority switch
        {
            "critical" => "[CRITICAL] ",
            "high" => "[HIGH] ",
            _ => ""
        };
        var value = prefix + title + (message.Length == 0 ? "" : $": {message}");
        return FitsGsm7(value) ? TruncateGsm7(value, 160) : TruncateUcs2(value, 70);
    }

    private static bool FitsGsm7(string value) => value.All(character =>
        GsmBasicCharacters.Contains(character, StringComparison.Ordinal) ||
        GsmExtendedCharacters.Contains(character, StringComparison.Ordinal));

    private static string TruncateGsm7(string value, int maximumSeptets)
    {
        var total = value.Sum(character =>
            GsmExtendedCharacters.Contains(character, StringComparison.Ordinal) ? 2 : 1);
        if (total <= maximumSeptets) return value;
        const string suffix = "...";
        var builder = new StringBuilder();
        var used = 0;
        foreach (var character in value)
        {
            var cost = GsmExtendedCharacters.Contains(character, StringComparison.Ordinal) ? 2 : 1;
            if (used + cost > maximumSeptets - suffix.Length) break;
            builder.Append(character);
            used += cost;
        }
        return builder.Append(suffix).ToString();
    }

    private static string TruncateUcs2(string value, int maximumCodeUnits)
    {
        if (value.Length <= maximumCodeUnits) return value;
        const string suffix = "...";
        var length = maximumCodeUnits - suffix.Length;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + suffix;
    }

    private static int PriorityRank(string value) => value.Trim().ToLowerInvariant() switch
    {
        "low" => 0,
        "normal" or "" => 1,
        "high" => 2,
        "critical" => 3,
        _ => throw new ArgumentException("Notification priority is invalid.")
    };

    private static string ReadSid(
        IReadOnlyDictionary<string, string> secrets,
        string secretName,
        string prefix)
    {
        var value = ReadSecret(secrets, secretName, $"Twilio {prefix} SID").Trim();
        if (!IsSid(value, prefix)) throw new ArgumentException($"Twilio {prefix} SID is invalid.");
        return value;
    }

    private static string ReadSecret(
        IReadOnlyDictionary<string, string> secrets,
        string secretName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(secretName) ||
            !secrets.TryGetValue(secretName, out var value) ||
            string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"An encrypted {description} is required.");
        return value;
    }

    private static bool IsSid(string value, string prefix) =>
        value.Length == 34 && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(2).ToArray().All(Uri.IsHexDigit);

    private static bool IsE164(string value) =>
        value.Length is >= 9 and <= 16 && value[0] == '+' && value[1] is >= '1' and <= '9' &&
        value.AsSpan(2).ToArray().All(char.IsAsciiDigit);

    private static string Read(JsonElement root, string name, bool required)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new ArgumentException($"Notification {name} is required.");
            return "";
        }
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Notification {name} must be text.");
        return value.GetString() ?? "";
    }

    private static async Task<Acknowledgement> ReadAcknowledgementAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 32 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > maximumBytes)
                throw new InvalidDataException("Twilio response exceeded the allowed size.");
            memory.Write(buffer, 0, read);
        }
        try
        {
            using var json = JsonDocument.Parse(memory.GetBuffer().AsMemory(0, checked((int)memory.Length)));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("sid", out var sid) || sid.ValueKind != JsonValueKind.String ||
                !IsMessageSid(sid.GetString() ?? "") ||
                !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String)
                return Acknowledgement.Malformed;
            var statusValue = status.GetString();
            if (statusValue is "failed" or "undelivered" or "canceled") return Acknowledgement.Rejected;
            if (statusValue is not ("accepted" or "queued" or "sending" or "sent" or "delivered"))
                return Acknowledgement.Malformed;
            if (!root.TryGetProperty("num_segments", out var segments) ||
                segments.ValueKind != JsonValueKind.String ||
                segments.GetString() is not ("0" or "1"))
                return Acknowledgement.Malformed;
            return Acknowledgement.Accepted;
        }
        catch (JsonException)
        {
            return Acknowledgement.Malformed;
        }
    }

    private static bool IsMessageSid(string value) =>
        value.Length == 34 && (value.StartsWith("SM", StringComparison.Ordinal) ||
                               value.StartsWith("MM", StringComparison.Ordinal)) &&
        value.AsSpan(2).ToArray().All(Uri.IsHexDigit);

    private static HttpClient CreateHardenedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 1,
            ConnectCallback = ConnectTwilioAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectTwilioAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(TwilioRequest, out var validated) ||
            !validated ||
            !context.DnsEndPoint.Host.Equals("api.twilio.com", StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("Twilio transport refused an unvalidated destination.");
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            cancellationToken);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Twilio resolved to a disallowed address.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private const string GsmBasicCharacters =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string GsmExtendedCharacters = "^{}\\[~]|€\f";

    private sealed class TwilioSmsConfiguration
    {
        public string AccountSidSecretName { get; set; } = "account_sid";
        public string CredentialMode { get; set; } = "api_key";
        public string CredentialSidSecretName { get; set; } = "credential_sid";
        public string CredentialSecretName { get; set; } = "credential_secret";
        public string RecipientSecretName { get; set; } = "recipient";
        public string SenderType { get; set; } = "messaging_service";
        public string SenderSecretName { get; set; } = "sender";
        public bool PaidSendConsent { get; set; }
        public string MinimumPriority { get; set; } = "critical";
        public int ValidityPeriodSeconds { get; set; } = 300;
    }

    private enum Acknowledgement { Malformed, Rejected, Accepted }
    private sealed class CostPolicyException : Exception { }
}
