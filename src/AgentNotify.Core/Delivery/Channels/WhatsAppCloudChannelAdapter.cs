using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class WhatsAppCloudChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> WhatsAppRequest =
        new("AgentNotify.WhatsAppCloud.ValidatedEndpoint");
    private static readonly Regex VersionPattern = new("^v([1-9][0-9]?)\\.0$", RegexOptions.CultureInvariant);
    private static readonly Regex TemplatePattern = new("^[a-z0-9_]{1,512}$", RegexOptions.CultureInvariant);
    private static readonly Regex LanguagePattern = new("^[a-z]{2,3}(?:_[A-Z]{2})?$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedParameters =
        ["title", "message", "priority", "type", "agent", "project"];
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public WhatsAppCloudChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "whatsapp_cloud";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        Uri endpoint;
        string accessToken;
        string body;
        try
        {
            var config = JsonSerializer.Deserialize<WhatsAppCloudConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("WhatsApp Cloud configuration is required.");
            ValidateAcknowledgements(config);
            var version = ValidateVersion(config.ApiVersion ?? "");
            var phoneNumberId = ReadSecret(delivery.Secrets, config.PhoneNumberIdSecretName, "phone-number ID").Trim();
            if (phoneNumberId.Length is < 5 or > 32 || !phoneNumberId.All(char.IsAsciiDigit))
                throw new ArgumentException("WhatsApp phone-number ID is invalid.");
            endpoint = new Uri($"https://graph.facebook.com/{version}/{phoneNumberId}/messages");
            accessToken = ReadSecret(delivery.Secrets, config.AccessTokenSecretName, "access token");
            if (accessToken.Length is < 16 or > 2048 || accessToken.Any(character => character is <= ' ' or > '~'))
                throw new ArgumentException("WhatsApp access token format is invalid.");
            body = BuildBody(config, delivery.Secrets, delivery.PayloadJson, delivery.NotificationId);
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
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Options.Set(WhatsAppRequest, true);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
                var accepted = await ReadAcknowledgementAsync(response.Content, cancellationToken);
                return accepted
                    ? DeliveryResult.Success(status)
                    : DeliveryResult.PermanentFailure("whatsapp_ambiguous_response", status);
            }
            if (status == 429) return DeliveryResult.Retry("whatsapp_429", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("whatsapp_redirect", status);
            if (status >= 500 || status is 408 or 425)
                return DeliveryResult.PermanentFailure("whatsapp_ambiguous_failure", status);
            return DeliveryResult.PermanentFailure($"whatsapp_{status}", status);
        }
        catch (OperationCanceledException)
        {
            return DeliveryResult.PermanentFailure("whatsapp_ambiguous_failure");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.PermanentFailure("whatsapp_ambiguous_failure");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.PermanentFailure("whatsapp_ambiguous_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static void ValidateAcknowledgements(WhatsAppCloudConfiguration config)
    {
        if (!config.RecipientOptInAcknowledged)
            throw new ArgumentException("Recipient opt-in acknowledgement is required.");
        if (!config.TemplateApprovedAcknowledged)
            throw new ArgumentException("Approved-template acknowledgement is required.");
        if (!config.PaidSendConsent)
            throw new ArgumentException("Explicit paid-send consent is required.");
    }

    private static string ValidateVersion(string value)
    {
        value = value.Trim();
        if (!VersionPattern.IsMatch(value))
            throw new ArgumentException("Meta Graph API version is invalid.");
        return value;
    }

    private static string BuildBody(
        WhatsAppCloudConfiguration config,
        IReadOnlyDictionary<string, string> secrets,
        string payloadJson,
        string notificationId)
    {
        var recipient = ReadSecret(secrets, config.RecipientSecretName, "recipient").Trim();
        if (!IsE164(recipient)) throw new ArgumentException("WhatsApp recipient must use E.164 format.");
        var templateName = (config.TemplateName ?? "").Trim();
        if (!TemplatePattern.IsMatch(templateName))
            throw new ArgumentException("WhatsApp template name is invalid.");
        var languageCode = (config.LanguageCode ?? "").Trim();
        if (!LanguagePattern.IsMatch(languageCode))
            throw new ArgumentException("WhatsApp template language code is invalid.");
        if (config.BodyParameters is null)
            throw new ArgumentException("WhatsApp template parameter mapping is required.");
        var bodyParameters = config.BodyParameters;
        if (bodyParameters.Count > 5 || bodyParameters.Any(value => string.IsNullOrEmpty(value) || !AllowedParameters.Contains(value)))
            throw new ArgumentException("WhatsApp template parameter mapping is invalid.");
        if (bodyParameters.Distinct(StringComparer.Ordinal).Count() != bodyParameters.Count)
            throw new ArgumentException("WhatsApp template parameter mapping contains duplicates.");

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");
        var priority = Read(root, "priority", false).Trim().ToLowerInvariant();
        if (!notificationId.Equals("test", StringComparison.Ordinal) &&
            PriorityRank(priority) < PriorityRank(config.MinimumPriority ?? ""))
            throw new CostPolicyException();

        var template = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = templateName,
            ["language"] = new { code = languageCode }
        };
        if (bodyParameters.Count > 0)
        {
            var parameters = bodyParameters.Select(name => new
            {
                type = "text",
                text = ReadParameter(root, name)
            }).ToArray();
            template["components"] = new[] { new { type = "body", parameters } };
        }
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["messaging_product"] = "whatsapp",
            ["recipient_type"] = "individual",
            ["to"] = recipient[1..],
            ["type"] = "template",
            ["template"] = template
        };
        var json = JsonSerializer.Serialize(payload, Json.Options);
        if (Encoding.UTF8.GetByteCount(json) > 32 * 1024)
            throw new ArgumentException("WhatsApp payload exceeded the local safety bound.");
        return json;
    }

    private static string ReadParameter(JsonElement root, string name)
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

    private static string ReadSecret(
        IReadOnlyDictionary<string, string> secrets,
        string secretName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(secretName) ||
            !secrets.TryGetValue(secretName, out var value) ||
            string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"An encrypted WhatsApp {description} is required.");
        return value;
    }

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

    private static async Task<bool> ReadAcknowledgementAsync(
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
                throw new InvalidDataException("WhatsApp response exceeded the allowed size.");
            memory.Write(buffer, 0, read);
        }
        try
        {
            using var json = JsonDocument.Parse(memory.GetBuffer().AsMemory(0, checked((int)memory.Length)));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("messaging_product", out var product) ||
                product.ValueKind != JsonValueKind.String || product.GetString() != "whatsapp" ||
                !root.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() != 1)
                return false;
            var message = messages[0];
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                return false;
            var value = id.GetString() ?? "";
            return value.Length is >= 12 and <= 512 && value.StartsWith("wamid.", StringComparison.Ordinal) &&
                   value.All(character => character is >= '!' and <= '~');
        }
        catch (JsonException)
        {
            return false;
        }
    }

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
            ConnectCallback = ConnectWhatsAppAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectWhatsAppAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(WhatsAppRequest, out var validated) ||
            !validated ||
            !context.DnsEndPoint.Host.Equals("graph.facebook.com", StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("WhatsApp transport refused an unvalidated destination.");
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            cancellationToken);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("WhatsApp resolved to a disallowed address.");
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

    private sealed class WhatsAppCloudConfiguration
    {
        public string ApiVersion { get; set; } = "v25.0";
        public string PhoneNumberIdSecretName { get; set; } = "phone_number_id";
        public string AccessTokenSecretName { get; set; } = "access_token";
        public string RecipientSecretName { get; set; } = "recipient";
        public string TemplateName { get; set; } = "hello_world";
        public string LanguageCode { get; set; } = "en_US";
        public List<string>? BodyParameters { get; set; } = [];
        public bool RecipientOptInAcknowledged { get; set; }
        public bool TemplateApprovedAcknowledged { get; set; }
        public bool PaidSendConsent { get; set; }
        public string MinimumPriority { get; set; } = "critical";
    }

    private sealed class CostPolicyException : Exception { }
}
