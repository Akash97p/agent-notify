using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class TwilioWhatsAppChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> TwilioRequest =
        new("AgentNotify.TwilioWhatsApp.ValidatedEndpoint");
    private static readonly HashSet<string> AllowedVariables =
        ["title", "message", "priority", "type", "agent", "project"];
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public TwilioWhatsAppChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "twilio_whatsapp";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        Uri endpoint;
        AuthenticationHeaderValue authorization;
        IReadOnlyDictionary<string, string> fields;
        try
        {
            var config = JsonSerializer.Deserialize<TwilioWhatsAppConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("Twilio WhatsApp configuration is required.");
            ValidateAcknowledgements(config);
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
                    Acknowledgement.Rejected => DeliveryResult.PermanentFailure("twilio_whatsapp_rejected", status),
                    _ => DeliveryResult.PermanentFailure("twilio_whatsapp_ambiguous_response", status)
                };
            }
            if (status == 429) return DeliveryResult.Retry("twilio_whatsapp_429", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("twilio_whatsapp_redirect", status);
            if (status >= 500 || status is 408 or 425)
                return DeliveryResult.PermanentFailure("twilio_whatsapp_ambiguous_failure", status);
            return DeliveryResult.PermanentFailure($"twilio_whatsapp_{status}", status);
        }
        catch (OperationCanceledException)
        {
            return DeliveryResult.PermanentFailure("twilio_whatsapp_ambiguous_failure");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.PermanentFailure("twilio_whatsapp_ambiguous_failure");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.PermanentFailure("twilio_whatsapp_ambiguous_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static void ValidateAcknowledgements(TwilioWhatsAppConfiguration config)
    {
        if (!config.RecipientOptInAcknowledged)
            throw new ArgumentException("Recipient opt-in acknowledgement is required.");
        if (!config.TemplateApprovedAcknowledged)
            throw new ArgumentException("Approved-template acknowledgement is required.");
        if (!config.TextOnlyTemplateAcknowledged)
            throw new ArgumentException("Text-only template acknowledgement is required.");
        if (!config.PaidSendConsent)
            throw new ArgumentException("Explicit paid-send consent is required.");
    }

    private static AuthenticationHeaderValue BuildAuthorization(
        TwilioWhatsAppConfiguration config,
        IReadOnlyDictionary<string, string> secrets,
        string accountSid)
    {
        var mode = (config.CredentialMode ?? "").Trim().ToLowerInvariant();
        var username = mode switch
        {
            "api_key" => ReadSid(secrets, config.CredentialSidSecretName, "SK"),
            "auth_token" => accountSid,
            _ => throw new ArgumentException("Twilio credential mode is invalid.")
        };
        var secret = ReadSecret(secrets, config.CredentialSecretName, "credential secret");
        if (secret.Length is < 16 or > 256 || secret.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Twilio credential secret format is invalid.");
        return new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{secret}")));
    }

    private static IReadOnlyDictionary<string, string> BuildFields(
        TwilioWhatsAppConfiguration config,
        IReadOnlyDictionary<string, string> secrets,
        string payloadJson,
        string notificationId)
    {
        var recipient = ReadSecret(secrets, config.RecipientSecretName, "WhatsApp recipient").Trim();
        if (!IsE164(recipient)) throw new ArgumentException("Twilio WhatsApp recipient must use E.164 format.");
        var messagingServiceSid = ReadSid(secrets, config.MessagingServiceSidSecretName, "MG");
        var contentSid = ReadSid(secrets, config.ContentSidSecretName, "HX");
        if (config.ContentVariables is null)
            throw new ArgumentException("Twilio Content variable mapping is required.");
        var variables = config.ContentVariables;
        if (variables.Count > 5 || variables.Any(value => string.IsNullOrEmpty(value) || !AllowedVariables.Contains(value)) ||
            variables.Distinct(StringComparer.Ordinal).Count() != variables.Count)
            throw new ArgumentException("Twilio Content variable mapping is invalid.");

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");
        var priority = Read(root, "priority", false).Trim().ToLowerInvariant();
        if (!notificationId.Equals("test", StringComparison.Ordinal) &&
            PriorityRank(priority) < PriorityRank(config.MinimumPriority ?? ""))
            throw new CostPolicyException();

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["To"] = $"whatsapp:{recipient}",
            ["MessagingServiceSid"] = messagingServiceSid,
            ["ContentSid"] = contentSid,
            ["ValidityPeriod"] = ValidateValidity(config.ValidityPeriodSeconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["ContentRetention"] = "discard",
            ["AddressRetention"] = "obfuscate"
        };
        if (variables.Count > 0)
        {
            var contentVariables = variables
                .Select((name, index) => new KeyValuePair<string, string>(
                    (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ReadVariable(root, name)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            fields["ContentVariables"] = JsonSerializer.Serialize(contentVariables, Json.Options);
        }
        if (fields.Sum(pair => Encoding.UTF8.GetByteCount(pair.Key) + Encoding.UTF8.GetByteCount(pair.Value)) > 32 * 1024)
            throw new ArgumentException("Twilio WhatsApp payload exceeded the local safety bound.");
        return fields;
    }

    private static int ValidateValidity(int value)
    {
        if (value is < 6 or > 36_000)
            throw new ArgumentException("Twilio validity period is invalid.");
        return value;
    }

    private static string ReadVariable(JsonElement root, string name)
    {
        var value = Read(root, name, name == "title").Trim().Replace('\0', ' ');
        if (value.Length == 0) value = name == "message" ? "Details withheld" : "Not provided";
        return TruncateScalars(value, name == "message" ? 1024 : 250);
    }

    private static string TruncateScalars(string value, int maximumScalars)
    {
        var runes = value.EnumerateRunes().ToArray();
        if (runes.Length <= maximumScalars) return value;
        var builder = new StringBuilder();
        foreach (var rune in runes.Take(maximumScalars - 3)) builder.Append(rune);
        return builder.Append("...").ToString();
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
        IReadOnlyDictionary<string, string> deliverySecrets,
        string secretName,
        string prefix)
    {
        var value = ReadSecret(deliverySecrets, secretName, $"{prefix} SID").Trim();
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
            throw new ArgumentException($"An encrypted Twilio {description} is required.");
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
                throw new InvalidDataException("Twilio WhatsApp response exceeded the allowed size.");
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
            return statusValue is "accepted" or "queued" or "sending" or "sent" or "delivered" or "read"
                ? Acknowledgement.Accepted
                : Acknowledgement.Malformed;
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
            throw new HttpRequestException("Twilio WhatsApp transport refused an unvalidated destination.");
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

    private sealed class TwilioWhatsAppConfiguration
    {
        public string AccountSidSecretName { get; set; } = "account_sid";
        public string CredentialMode { get; set; } = "api_key";
        public string CredentialSidSecretName { get; set; } = "credential_sid";
        public string CredentialSecretName { get; set; } = "credential_secret";
        public string RecipientSecretName { get; set; } = "recipient";
        public string MessagingServiceSidSecretName { get; set; } = "messaging_service_sid";
        public string ContentSidSecretName { get; set; } = "content_sid";
        public List<string>? ContentVariables { get; set; } = [];
        public bool RecipientOptInAcknowledged { get; set; }
        public bool TemplateApprovedAcknowledged { get; set; }
        public bool TextOnlyTemplateAcknowledged { get; set; }
        public bool PaidSendConsent { get; set; }
        public string MinimumPriority { get; set; } = "critical";
        public int ValidityPeriodSeconds { get; set; } = 300;
    }

    private enum Acknowledgement { Malformed, Rejected, Accepted }
    private sealed class CostPolicyException : Exception { }
}
