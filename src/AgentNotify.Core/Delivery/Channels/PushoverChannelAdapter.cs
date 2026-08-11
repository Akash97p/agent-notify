using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class PushoverChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly Uri Endpoint = new("https://api.pushover.net/1/messages.json");
    private static readonly HttpRequestOptionsKey<bool> PushoverRequest =
        new("AgentNotify.Pushover.ValidatedEndpoint");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public PushoverChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "pushover";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        PushoverConfiguration config;
        IReadOnlyDictionary<string, string> fields;
        bool emergency;
        try
        {
            config = JsonSerializer.Deserialize<PushoverConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("Pushover configuration is required.");
            fields = BuildFields(config, delivery.Secrets, delivery.PayloadJson, out emergency);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Options.Set(PushoverRequest, true);
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
                var acknowledgement = await ReadAcknowledgementAsync(response.Content, emergency, cancellationToken);
                return acknowledgement switch
                {
                    Acknowledgement.Accepted => DeliveryResult.Success(status),
                    Acknowledgement.Rejected => DeliveryResult.PermanentFailure("pushover_rejected", status),
                    _ => DeliveryResult.Retry("pushover_invalid_response", status)
                };
            }
            // Pushover explicitly says retrying the same request after any 4xx will not work.
            if (status is >= 400 and <= 499)
                return DeliveryResult.PermanentFailure($"pushover_{status}", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("pushover_redirect", status);
            return DeliveryResult.Retry($"pushover_{status}", status);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.Retry("pushover_invalid_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static IReadOnlyDictionary<string, string> BuildFields(
        PushoverConfiguration config,
        IReadOnlyDictionary<string, string> secrets,
        string payloadJson,
        out bool emergency)
    {
        var applicationToken = ReadRequiredSecret(
            secrets,
            config.ApplicationTokenSecretName,
            "Pushover application token");
        var userKey = ReadRequiredSecret(secrets, config.UserKeySecretName, "Pushover user/group key");
        if (!IsKey(applicationToken) || !IsKey(userKey))
            throw new ArgumentException("Pushover keys must be 30 alphanumeric characters.");

        string? device = null;
        if (!string.IsNullOrWhiteSpace(config.DeviceSecretName) &&
            secrets.TryGetValue(config.DeviceSecretName, out var storedDevice) &&
            !string.IsNullOrWhiteSpace(storedDevice))
        {
            device = storedDevice.Trim();
            if (device.Length > 25 ||
                !device.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                throw new ArgumentException("Pushover device name is invalid.");
        }

        var sound = config.Sound?.Trim() ?? "";
        if (sound.Length > 64 || sound.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("Pushover sound name is invalid.");

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");
        var title = TruncateRunes(Read(root, "title", true).Trim().Replace('\0', ' '), 250);
        if (title.Length == 0)
            throw new ArgumentException("Notification title is required.");
        var message = BuildMessage(root);
        var priorityName = Read(root, "priority", false).Trim().ToLowerInvariant();
        emergency = priorityName == "critical" && config.CriticalAsEmergency;
        if (emergency && (config.EmergencyRetrySeconds < 30 ||
                          config.EmergencyExpireSeconds is < 1 or > 10_800))
            throw new ArgumentException("Pushover emergency retry or expiry is invalid.");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["token"] = applicationToken,
            ["user"] = userKey,
            ["title"] = title,
            ["message"] = message,
            ["priority"] = MapPriority(priorityName, emergency).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (device is not null) fields["device"] = device;
        if (sound.Length > 0) fields["sound"] = sound;
        if (emergency)
        {
            fields["retry"] = config.EmergencyRetrySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["expire"] = config.EmergencyExpireSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return fields;
    }

    private static string BuildMessage(JsonElement root)
    {
        var builder = new StringBuilder();
        var message = Read(root, "message", false);
        if (!string.IsNullOrWhiteSpace(message)) builder.AppendLine(message.Trim()).AppendLine();
        Append(builder, "Type", Read(root, "type", false));
        Append(builder, "Agent", Read(root, "agent", false));
        Append(builder, "Project", Read(root, "project", false));
        builder.Append("Sent by AgentNotify.");
        return TruncateRunes(builder.ToString().Replace('\0', ' '), 1024);
    }

    private static int MapPriority(string priority, bool emergency) => priority switch
    {
        "low" => -1,
        "high" => 1,
        "critical" => emergency ? 2 : 1,
        _ => 0
    };

    private static string ReadRequiredSecret(
        IReadOnlyDictionary<string, string> secrets,
        string name,
        string description)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !secrets.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"An encrypted {description} is required.");
        return value.Trim();
    }

    private static bool IsKey(string value) =>
        value.Length == 30 && value.All(char.IsAsciiLetterOrDigit);

    private static void Append(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append(label).Append(": ").AppendLine(value.Trim().Replace('\0', ' '));
    }

    private static string TruncateRunes(string value, int maximumRunes)
    {
        if (value.EnumerateRunes().Count() <= maximumRunes) return value;
        var builder = new StringBuilder();
        foreach (var rune in value.EnumerateRunes().Take(maximumRunes - 1))
            builder.Append(rune.ToString());
        return builder.Append('…').ToString();
    }

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
        bool emergency,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 16 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > maximumBytes)
                throw new InvalidDataException("Pushover response exceeded the allowed size.");
            memory.Write(buffer, 0, read);
        }

        try
        {
            using var json = JsonDocument.Parse(memory.GetBuffer().AsMemory(0, checked((int)memory.Length)));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("status", out var status) ||
                !status.TryGetInt32(out var statusCode))
                return Acknowledgement.Malformed;
            if (statusCode != 1) return Acknowledgement.Rejected;
            if (!root.TryGetProperty("request", out var request) ||
                request.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(request.GetString()))
                return Acknowledgement.Malformed;
            if (emergency && (!root.TryGetProperty("receipt", out var receipt) ||
                              receipt.ValueKind != JsonValueKind.String ||
                              string.IsNullOrWhiteSpace(receipt.GetString())))
                return Acknowledgement.Malformed;
            return Acknowledgement.Accepted;
        }
        catch (JsonException)
        {
            return Acknowledgement.Malformed;
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
            MaxConnectionsPerServer = 2,
            ConnectCallback = ConnectPushoverAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectPushoverAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(PushoverRequest, out var validated) ||
            !validated ||
            !context.DnsEndPoint.Host.Equals("api.pushover.net", StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("Pushover transport refused an unvalidated destination.");
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            cancellationToken);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Pushover resolved to a disallowed address.");
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

    private sealed class PushoverConfiguration
    {
        public string ApplicationTokenSecretName { get; set; } = "application_token";
        public string UserKeySecretName { get; set; } = "user_key";
        public string DeviceSecretName { get; set; } = "device";
        public string Sound { get; set; } = "";
        public bool CriticalAsEmergency { get; set; }
        public int EmergencyRetrySeconds { get; set; } = 60;
        public int EmergencyExpireSeconds { get; set; } = 3600;
    }

    private enum Acknowledgement { Malformed, Rejected, Accepted }
}
