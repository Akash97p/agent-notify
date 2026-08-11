using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class MatrixChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private const int MaximumPayloadBytes = 48 * 1024;
    private static readonly HttpRequestOptionsKey<bool> MatrixRequest = new("AgentNotify.Matrix.ValidatedEndpoint");
    private static readonly HttpRequestOptionsKey<bool> AllowPrivateNetwork = new("AgentNotify.Matrix.AllowPrivateNetwork");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public MatrixChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "matrix";

    public async Task<DeliveryResult> DeliverAsync(OutboundDelivery delivery, CancellationToken cancellationToken)
    {
        MatrixConfiguration config;
        Uri endpoint;
        string token;
        string payload;
        try
        {
            config = JsonSerializer.Deserialize<MatrixConfiguration>(delivery.Profile.ConfigJson, Json.Options) ??
                     throw new ArgumentException("Matrix configuration is required.");
            if (!delivery.Secrets.TryGetValue(config.AccessTokenSecretName, out token!) || !IsToken(token) ||
                !delivery.Secrets.TryGetValue(config.RoomIdSecretName, out var roomId) || !IsRoomId(roomId))
                throw new ArgumentException("Encrypted Matrix access token and room ID are required.");
            var homeserver = ValidateHomeserver(config.HomeserverBaseUrl, config.AllowPrivateNetwork);
            endpoint = BuildEndpoint(homeserver, roomId, delivery.OutboxId);
            payload = BuildPayload(delivery.PayloadJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Options.Set(MatrixRequest, true);
        request.Options.Set(AllowPrivateNetwork, config.AllowPrivateNetwork);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 200 and <= 299)
                return await HasValidAcknowledgementAsync(response.Content, cancellationToken)
                    ? DeliveryResult.Success(status)
                    : DeliveryResult.Retry("matrix_invalid_response", status);
            if (status is 408 or 425 or 429 || status >= 500) return DeliveryResult.Retry($"matrix_{status}", status);
            if (status is >= 300 and <= 399) return DeliveryResult.PermanentFailure("matrix_redirect", status);
            return DeliveryResult.PermanentFailure($"matrix_{status}", status);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (InvalidDataException) { return DeliveryResult.Retry("matrix_invalid_response"); }
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }

    private static Uri ValidateHomeserver(string value, bool allowPrivate)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Matrix homeserver must be an HTTPS base URL.");
        if ((uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) && !allowPrivate)
            throw new ArgumentException("Private Matrix homeservers require explicit consent.");
        var addressHost = uri.Host.TrimStart('[').TrimEnd(']');
        if (IPAddress.TryParse(addressHost, out var address) &&
            !WebhookChannelAdapter.IsAddressAllowed(address, allowPrivate))
            throw new ArgumentException("Matrix homeserver address is not allowed.");
        if (uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment =>
                segment.Length > 128 || segment.Contains('%', StringComparison.Ordinal) ||
                !segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
            throw new ArgumentException("Matrix homeserver base path is invalid.");
        return uri;
    }

    private static Uri BuildEndpoint(Uri homeserver, string roomId, string outboxId)
    {
        var prefix = homeserver.AbsoluteUri.TrimEnd('/');
        var transaction = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(outboxId)))[..32].ToLowerInvariant();
        return new Uri($"{prefix}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/send/m.room.message/{transaction}");
    }

    private static bool IsRoomId(string value) => value.StartsWith('!') && value.Length > 1 &&
        Encoding.UTF8.GetByteCount(value) <= 255 && !value.Any(c => c == '\0' || char.IsControl(c) || char.IsSurrogate(c));

    private static bool IsToken(string value) => value.Length is >= 10 and <= 4096 &&
        value.All(c => c is >= '!' and <= '~');

    private static string BuildPayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Notification payload is invalid.");
        var builder = new StringBuilder();
        var priority = Read(root, "priority", false);
        if (!string.IsNullOrWhiteSpace(priority)) builder.Append('[').Append(Neutralize(priority.ToUpperInvariant())).Append("] ");
        builder.AppendLine(Neutralize(Read(root, "title", true)));
        var message = Read(root, "message", false);
        if (!string.IsNullOrWhiteSpace(message)) builder.AppendLine().AppendLine(Neutralize(message));
        Append(builder, "Type", Read(root, "type", false));
        Append(builder, "Agent", Read(root, "agent", false));
        Append(builder, "Project", Read(root, "project", false));
        builder.AppendLine().Append("Sent by AgentNotify.");
        return SerializeBounded(builder.ToString().Replace('\0', ' '));
    }

    private static string SerializeBounded(string text)
    {
        var result = Serialize(text);
        if (Encoding.UTF8.GetByteCount(result) <= MaximumPayloadBytes) return result;
        var low = 0; var high = text.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var length = SafeLength(text, middle);
            if (Encoding.UTF8.GetByteCount(Serialize(text[..length] + "…")) <= MaximumPayloadBytes) low = middle;
            else high = middle - 1;
        }
        return Serialize(text[..SafeLength(text, low)] + "…");
    }

    private static string Serialize(string body) => JsonSerializer.Serialize(new MatrixPayload(body), Json.Options);
    private static int SafeLength(string value, int length)
    {
        var result = Math.Min(value.Length, length);
        if (result > 0 && result < value.Length && char.IsHighSurrogate(value[result - 1])) result--;
        return result;
    }
    private static string Neutralize(string value) => value.Trim().Replace("@", "＠", StringComparison.Ordinal);
    private static void Append(StringBuilder builder, string label, string value)
    { if (!string.IsNullOrWhiteSpace(value)) builder.Append(label).Append(": ").AppendLine(Neutralize(value)); }
    private static string Read(JsonElement root, string name, bool required)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        { if (required) throw new ArgumentException($"Notification {name} is required."); return ""; }
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"Notification {name} must be text.");
        return value.GetString() ?? "";
    }

    private static async Task<bool> HasValidAcknowledgementAsync(HttpContent content, CancellationToken token)
    {
        const int maximumBytes = 8 * 1024;
        await using var stream = await content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (memory.Length + read > maximumBytes) throw new InvalidDataException("Matrix response exceeded limit.");
            memory.Write(buffer, 0, read);
        }
        using var json = JsonDocument.Parse(memory.GetBuffer().AsMemory(0, checked((int)memory.Length)));
        return json.RootElement.ValueKind == JsonValueKind.Object &&
               json.RootElement.TryGetProperty("event_id", out var eventId) && eventId.ValueKind == JsonValueKind.String &&
               eventId.GetString() is { Length: > 1 and <= 255 } id && id[0] == '$';
    }

    private static HttpClient CreateHardenedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 2,
            ConnectCallback = ConnectMatrixAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectMatrixAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(MatrixRequest, out var validated) || !validated)
            throw new HttpRequestException("Matrix transport refused an unvalidated destination.");
        var allowPrivate = context.InitialRequestMessage.Options.TryGetValue(AllowPrivateNetwork, out var configured) && configured;
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.Unspecified, token);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, allowPrivate)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Matrix homeserver resolved to a disallowed address.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, token); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }

    private sealed class MatrixConfiguration
    {
        public string HomeserverBaseUrl { get; set; } = "";
        public bool AllowPrivateNetwork { get; set; }
        public string AccessTokenSecretName { get; set; } = "access_token";
        public string RoomIdSecretName { get; set; } = "room_id";
    }
    private sealed record MatrixPayload(
        [property: JsonPropertyName("msgtype")] string MessageType,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("m.mentions")] object Mentions)
    {
        public MatrixPayload(string body) : this("m.text", body, new { }) { }
    }
}
