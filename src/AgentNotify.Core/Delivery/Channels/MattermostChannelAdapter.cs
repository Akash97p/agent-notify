using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class MattermostChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> MattermostRequest =
        new("AgentNotify.Mattermost.ValidatedEndpoint");
    private static readonly HttpRequestOptionsKey<bool> AllowPrivateNetwork =
        new("AgentNotify.Mattermost.AllowPrivateNetwork");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public MattermostChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "mattermost";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        MattermostConfiguration config;
        Uri endpoint;
        string text;
        try
        {
            config = JsonSerializer.Deserialize<MattermostConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("Mattermost configuration is required.");
            if (string.IsNullOrWhiteSpace(config.WebhookUrlSecretName) ||
                !delivery.Secrets.TryGetValue(config.WebhookUrlSecretName, out var webhookUrl))
                throw new ArgumentException("An encrypted Mattermost webhook URL is required.");
            endpoint = ValidateWebhookUri(webhookUrl, config.AllowPrivateNetwork);
            text = BuildText(delivery.PayloadJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        var payload = JsonSerializer.Serialize(new MattermostPayload(text, config.Silent), Json.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Options.Set(MattermostRequest, true);
        request.Options.Set(AllowPrivateNetwork, config.AllowPrivateNetwork);
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
                var acknowledgement = await ReadBoundedResponseAsync(response.Content, cancellationToken);
                return status == 204 || acknowledgement.Trim().Equals("ok", StringComparison.OrdinalIgnoreCase)
                    ? DeliveryResult.Success(status)
                    : DeliveryResult.Retry("mattermost_invalid_response", status);
            }
            if (status is 408 or 425 or 429 || status >= 500)
                return DeliveryResult.Retry($"mattermost_{status}", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("mattermost_redirect", status);
            return DeliveryResult.PermanentFailure($"mattermost_{status}", status);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.Retry("mattermost_invalid_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static Uri ValidateWebhookUri(string endpoint, bool allowPrivate)
    {
        if (endpoint.Length > 4096 ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use a Mattermost HTTPS incoming-webhook URL.");

        if ((uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) &&
            !allowPrivate)
            throw new ArgumentException("Private Mattermost destinations require explicit consent.");
        var addressHost = uri.Host.TrimStart('[').TrimEnd(']');
        if (IPAddress.TryParse(addressHost, out var literal) &&
            !WebhookChannelAdapter.IsAddressAllowed(literal, allowPrivate))
            throw new ArgumentException("Mattermost destination address is not allowed.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !segments[^2].Equals("hooks", StringComparison.OrdinalIgnoreCase) ||
            !IsSafePathPrefix(segments[..^2]) ||
            !IsWebhookToken(segments[^1]))
            throw new ArgumentException("Mattermost webhook URL path is invalid.");
        return uri;
    }

    private static bool IsSafePathPrefix(IEnumerable<string> segments) =>
        segments.All(segment => segment.Length is >= 1 and <= 128 &&
                                !segment.Contains('%', StringComparison.Ordinal) &&
                                segment.All(character => char.IsAsciiLetterOrDigit(character) ||
                                                         character is '_' or '-' or '.'));

    private static bool IsWebhookToken(string value) =>
        value.Length is >= 10 and <= 256 &&
        !value.Contains('%', StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string BuildText(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");

        var title = ReadString(root, "title", true);
        var message = ReadString(root, "message", false);
        var priority = ReadString(root, "priority", false);
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(priority))
            builder.Append('[').Append(Escape(priority.ToUpperInvariant())).Append("] ");
        builder.AppendLine(Escape(title));
        if (!string.IsNullOrWhiteSpace(message))
            builder.AppendLine().AppendLine(Escape(message));
        Append(builder, "Type", ReadString(root, "type", false));
        Append(builder, "Agent", ReadString(root, "agent", false));
        Append(builder, "Project", ReadString(root, "project", false));
        builder.AppendLine().Append("Sent by AgentNotify.");
        return Truncate(builder.ToString().Replace('\0', ' '), 16_383);
    }

    private static void Append(StringBuilder builder, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append(name).Append(": ").AppendLine(Escape(value));
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (character is '\\' or '*' or '_' or '~' or '`' or '[' or ']' or '(' or ')' or '#' or '|')
                builder.Append('\\');
            builder.Append(character switch
            {
                '@' => '＠',
                '<' => '‹',
                '>' => '›',
                _ => character
            });
        }
        return builder.ToString();
    }

    private static string Truncate(string value, int limit)
    {
        if (value.Length <= limit) return value;
        var length = limit - 1;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + "…";
    }

    private static string ReadString(JsonElement root, string name, bool required)
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

    private static async Task<string> ReadBoundedResponseAsync(
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
                throw new InvalidDataException("Mattermost response exceeded the allowed size.");
            memory.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(memory.GetBuffer(), 0, checked((int)memory.Length));
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
            ConnectCallback = ConnectMattermostAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectMattermostAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(MattermostRequest, out var validated) ||
            !validated)
            throw new HttpRequestException("Mattermost transport refused an unvalidated destination.");
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
            throw new HttpRequestException("Mattermost destination resolved to a disallowed address.");

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

    private sealed class MattermostConfiguration
    {
        public string WebhookUrlSecretName { get; set; } = "webhook_url";
        public bool AllowPrivateNetwork { get; set; }
        public bool Silent { get; set; }
    }

    private sealed record MattermostPayload(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("silent")] bool Silent);
}
