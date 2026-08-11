using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class GotifyChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> GotifyRequest =
        new("AgentNotify.Gotify.ValidatedEndpoint");
    private static readonly HttpRequestOptionsKey<bool> AllowPrivateNetwork =
        new("AgentNotify.Gotify.AllowPrivateNetwork");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public GotifyChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "gotify";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        GotifyConfiguration config;
        Uri endpoint;
        string token;
        string payload;
        try
        {
            config = JsonSerializer.Deserialize<GotifyConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("Gotify configuration is required.");
            endpoint = BuildEndpoint(ValidateServer(config.ServerBaseUrl, config.AllowPrivateNetwork));
            if (string.IsNullOrWhiteSpace(config.ApplicationTokenSecretName) ||
                !delivery.Secrets.TryGetValue(config.ApplicationTokenSecretName, out token!) ||
                !IsApplicationToken(token))
                throw new ArgumentException("An encrypted Gotify application token is required.");
            payload = BuildPayload(delivery.PayloadJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Options.Set(GotifyRequest, true);
        request.Options.Set(AllowPrivateNetwork, config.AllowPrivateNetwork);
        request.Headers.TryAddWithoutValidation("X-Gotify-Key", token);
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
                    : DeliveryResult.Retry("gotify_invalid_response", status);
            if (status is 408 or 425 or 429 || status >= 500)
                return DeliveryResult.Retry($"gotify_{status}", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("gotify_redirect", status);
            return DeliveryResult.PermanentFailure($"gotify_{status}", status);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.Retry("gotify_invalid_response");
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
            throw new ArgumentException("Gotify server must be an HTTPS base URL.");
        if ((uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) &&
            !allowPrivate)
            throw new ArgumentException("Private Gotify servers require explicit consent.");
        var addressHost = uri.Host.TrimStart('[').TrimEnd(']');
        if (IPAddress.TryParse(addressHost, out var address) &&
            !WebhookChannelAdapter.IsAddressAllowed(address, allowPrivate))
            throw new ArgumentException("Gotify server address is not allowed.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.Length > 128 || segment.Contains('%', StringComparison.Ordinal) ||
                                   !segment.All(character => char.IsAsciiLetterOrDigit(character) ||
                                                             character is '_' or '-' or '.')) ||
            segments.LastOrDefault()?.Equals("message", StringComparison.OrdinalIgnoreCase) == true)
            throw new ArgumentException("Enter the Gotify server base URL without /message.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static Uri BuildEndpoint(Uri server) => new(server, "message");

    private static bool IsApplicationToken(string value) =>
        value.Length is >= 10 and <= 256 &&
        value.All(character => character is >= '!' and <= '~' && character != ':');

    private static string BuildPayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");
        var title = Truncate(Read(root, "title", true).Trim(), 256);
        var message = Read(root, "message", false);
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message)) builder.AppendLine(message.Trim()).AppendLine();
        Append(builder, "Type", Read(root, "type", false));
        Append(builder, "Agent", Read(root, "agent", false));
        Append(builder, "Project", Read(root, "project", false));
        builder.Append("Sent by AgentNotify.");
        var payload = new GotifyPayload(
            Truncate(builder.ToString().Replace('\0', ' '), 16_384),
            title.Replace('\0', ' '),
            MapPriority(Read(root, "priority", false)),
            new Dictionary<string, object>
            {
                ["client::display"] = new Dictionary<string, string> { ["contentType"] = "text/plain" }
            });
        return JsonSerializer.Serialize(payload, Json.Options);
    }

    private static int MapPriority(string value) => value.Trim().ToLowerInvariant() switch
    {
        "low" => 2,
        "high" => 7,
        "critical" => 10,
        _ => 5
    };

    private static void Append(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append(label).Append(": ").AppendLine(value.Trim().Replace('\0', ' '));
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (value.Length <= maximumLength) return value;
        var length = maximumLength - 1;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + "…";
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

    private static async Task<bool> HasValidAcknowledgementAsync(
        HttpContent content,
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
                throw new InvalidDataException("Gotify response exceeded the allowed size.");
            memory.Write(buffer, 0, read);
        }
        using var json = JsonDocument.Parse(memory.GetBuffer().AsMemory(0, checked((int)memory.Length)));
        return json.RootElement.ValueKind == JsonValueKind.Object &&
               json.RootElement.TryGetProperty("id", out var id) &&
               id.TryGetInt64(out var number) && number > 0;
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
            ConnectCallback = ConnectGotifyAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectGotifyAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(GotifyRequest, out var validated) || !validated)
            throw new HttpRequestException("Gotify transport refused an unvalidated destination.");
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
            throw new HttpRequestException("Gotify server resolved to a disallowed address.");
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

    private sealed class GotifyConfiguration
    {
        public string ServerBaseUrl { get; set; } = "";
        public bool AllowPrivateNetwork { get; set; }
        public string ApplicationTokenSecretName { get; set; } = "application_token";
    }

    private sealed record GotifyPayload(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("priority")] int Priority,
        [property: JsonPropertyName("extras")] Dictionary<string, object> Extras);
}
