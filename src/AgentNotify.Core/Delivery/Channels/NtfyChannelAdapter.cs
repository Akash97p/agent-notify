using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class NtfyChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private const int MaximumMessageBytes = 4096;
    private static readonly HttpRequestOptionsKey<bool> NtfyRequest =
        new("AgentNotify.Ntfy.ValidatedEndpoint");
    private static readonly HttpRequestOptionsKey<bool> AllowPrivateNetwork =
        new("AgentNotify.Ntfy.AllowPrivateNetwork");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public NtfyChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "ntfy";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        NtfyConfiguration config;
        Uri endpoint;
        string topic;
        string? accessToken;
        string payload;
        try
        {
            config = JsonSerializer.Deserialize<NtfyConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("ntfy configuration is required.");
            endpoint = ValidateServer(config.ServerBaseUrl, config.AllowPrivateNetwork);
            if (string.IsNullOrWhiteSpace(config.TopicSecretName) ||
                !delivery.Secrets.TryGetValue(config.TopicSecretName, out topic!) ||
                !IsTopic(topic))
                throw new ArgumentException("An encrypted ntfy topic is required.");
            accessToken = null;
            if (!string.IsNullOrWhiteSpace(config.AccessTokenSecretName) &&
                delivery.Secrets.TryGetValue(config.AccessTokenSecretName, out var configuredToken))
            {
                if (!IsAccessToken(configuredToken))
                    throw new ArgumentException("ntfy access token is invalid.");
                accessToken = configuredToken;
            }
            if (accessToken is null && !config.AllowUnauthenticatedTopic)
                throw new ArgumentException("Unauthenticated ntfy publishing requires explicit consent.");
            payload = BuildPayload(delivery, topic);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Options.Set(NtfyRequest, true);
        request.Options.Set(AllowPrivateNetwork, config.AllowPrivateNetwork);
        if (accessToken is not null)
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
                return await HasValidAcknowledgementAsync(response.Content, cancellationToken)
                    ? DeliveryResult.Success(status)
                    : DeliveryResult.Retry("ntfy_invalid_response", status);
            if (status is 408 or 425 or 429 || status >= 500)
                return DeliveryResult.Retry($"ntfy_{status}", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("ntfy_redirect", status);
            return DeliveryResult.PermanentFailure($"ntfy_{status}", status);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.Retry("ntfy_invalid_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static Uri ValidateServer(string value, bool allowPrivate)
    {
        if (value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("ntfy server must be an HTTPS base URL.");

        if ((uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) &&
            !allowPrivate)
            throw new ArgumentException("Private ntfy servers require explicit consent.");
        var addressHost = uri.Host.TrimStart('[').TrimEnd(']');
        if (IPAddress.TryParse(addressHost, out var address) &&
            !WebhookChannelAdapter.IsAddressAllowed(address, allowPrivate))
            throw new ArgumentException("ntfy server address is not allowed.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.Length > 128 || segment.Contains('%', StringComparison.Ordinal) ||
                                   !segment.All(character => char.IsAsciiLetterOrDigit(character) ||
                                                             character is '_' or '-' or '.')))
            throw new ArgumentException("ntfy server base path is invalid.");

        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static string BuildPayload(OutboundDelivery delivery, string topic)
    {
        using var document = JsonDocument.Parse(delivery.PayloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");

        var title = Sanitize(Read(root, "title", true));
        title = TruncateUtf8(title, 256);
        var message = Read(root, "message", false);
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message)) builder.AppendLine(Sanitize(message)).AppendLine();
        Append(builder, "Type", Read(root, "type", false));
        Append(builder, "Agent", Read(root, "agent", false));
        Append(builder, "Project", Read(root, "project", false));
        builder.Append("Sent by AgentNotify.");
        var priority = ParsePriority(Read(root, "priority", false));
        var body = TruncateUtf8(builder.ToString().Replace('\0', ' '), MaximumMessageBytes);
        var sequenceId = "an-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(delivery.OutboxId)))[..24].ToLowerInvariant();
        var payload = new NtfyPayload(
            topic,
            body,
            title,
            priority,
            Tags(priority),
            false,
            sequenceId);
        return JsonSerializer.Serialize(payload, Json.Options);
    }

    private static int ParsePriority(string value) => value.Trim().ToLowerInvariant() switch
    {
        "low" => 2,
        "high" => 4,
        "critical" => 5,
        _ => 3
    };

    private static string[] Tags(int priority) => priority switch
    {
        5 => ["rotating_light", "robot"],
        4 => ["warning", "robot"],
        2 => ["information_source", "robot"],
        _ => ["robot"]
    };

    private static void Append(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append(label).Append(": ").AppendLine(Sanitize(value));
    }

    private static string Sanitize(string value) => value.Trim().Replace('\0', ' ');

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

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return value;
        const string suffix = "…";
        var allowed = maximumBytes - Encoding.UTF8.GetByteCount(suffix);
        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var length = SafeLength(value, middle);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) <= allowed) low = middle;
            else high = middle - 1;
        }
        return value[..SafeLength(value, low)] + suffix;
    }

    private static int SafeLength(string value, int length)
    {
        var result = Math.Min(value.Length, length);
        if (result > 0 && result < value.Length && char.IsHighSurrogate(value[result - 1])) result--;
        return result;
    }

    private static bool IsTopic(string value) =>
        value.Length is >= 1 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsAccessToken(string value) =>
        value.Length == 32 && value.StartsWith("tk_", StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static async Task<bool> HasValidAcknowledgementAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 8 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (memory.Length + read > maximumBytes)
                throw new InvalidDataException("ntfy response exceeded the allowed size.");
            memory.Write(buffer, 0, read);
        }
        using var json = JsonDocument.Parse(memory.GetBuffer().AsMemory(0, checked((int)memory.Length)));
        var root = json.RootElement;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
               id.GetString() is { Length: >= 5 and <= 64 } &&
               (!root.TryGetProperty("event", out var eventName) ||
                eventName.ValueKind == JsonValueKind.String && eventName.GetString() == "message");
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
            ConnectCallback = ConnectNtfyAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectNtfyAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(NtfyRequest, out var validated) || !validated)
            throw new HttpRequestException("ntfy transport refused an unvalidated destination.");
        var allowPrivate = context.InitialRequestMessage.Options.TryGetValue(
            AllowPrivateNetwork,
            out var configured) && configured;
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            cancellationToken);
        var allowed = addresses
            .Where(address => WebhookChannelAdapter.IsAddressAllowed(address, allowPrivate))
            .ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("ntfy server resolved to a disallowed address.");
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

    private sealed class NtfyConfiguration
    {
        public string ServerBaseUrl { get; set; } = "https://ntfy.sh";
        public bool AllowPrivateNetwork { get; set; }
        public bool AllowUnauthenticatedTopic { get; set; }
        public string TopicSecretName { get; set; } = "topic";
        public string AccessTokenSecretName { get; set; } = "access_token";
    }

    private sealed record NtfyPayload(
        [property: JsonPropertyName("topic")] string Topic,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("priority")] int Priority,
        [property: JsonPropertyName("tags")] string[] Tags,
        [property: JsonPropertyName("markdown")] bool Markdown,
        [property: JsonPropertyName("sequence_id")] string SequenceId);
}
