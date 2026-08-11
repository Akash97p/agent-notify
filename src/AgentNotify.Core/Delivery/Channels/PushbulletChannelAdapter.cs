using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class PushbulletChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly Uri Endpoint = new("https://api.pushbullet.com/v2/pushes");
    private static readonly HttpRequestOptionsKey<bool> PushbulletRequest =
        new("AgentNotify.Pushbullet.ValidatedEndpoint");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public PushbulletChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "pushbullet";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        string accessToken;
        string payload;
        try
        {
            var config = JsonSerializer.Deserialize<PushbulletConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("Pushbullet configuration is required.");
            if (!config.QuotaAcknowledged)
                throw new ArgumentException("Pushbullet quota acknowledgement is required.");
            accessToken = ReadAccessToken(config, delivery.Secrets);
            payload = BuildPayload(config, delivery.Secrets, delivery.PayloadJson, delivery.OutboxId);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Options.Set(PushbulletRequest, true);
        request.Headers.TryAddWithoutValidation("Access-Token", accessToken);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 200 and <= 299)
                return await HasValidPushAsync(response.Content, cancellationToken)
                    ? DeliveryResult.Success(status)
                    : DeliveryResult.Retry("pushbullet_invalid_response", status);
            if (status is 408 or 425 or 429 || status >= 500)
                return DeliveryResult.Retry($"pushbullet_{status}", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("pushbullet_redirect", status);
            return DeliveryResult.PermanentFailure($"pushbullet_{status}", status);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.Retry("pushbullet_invalid_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static string ReadAccessToken(
        PushbulletConfiguration config,
        IReadOnlyDictionary<string, string> secrets)
    {
        if (string.IsNullOrWhiteSpace(config.AccessTokenSecretName) ||
            !secrets.TryGetValue(config.AccessTokenSecretName, out var token) ||
            string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("An encrypted Pushbullet access token is required.");
        token = token.Trim();
        if (token.Length is < 16 or > 256 ||
            token.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Pushbullet access token format is invalid.");
        return token;
    }

    private static string BuildPayload(
        PushbulletConfiguration config,
        IReadOnlyDictionary<string, string> secrets,
        string payloadJson,
        string outboxId)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");
        var title = TruncateUtf8(Read(root, "title", true).Trim().Replace('\0', ' '), 1024);
        if (title.Length == 0) throw new ArgumentException("Notification title is required.");
        var body = BuildMessage(root);
        var push = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["type"] = "note",
            ["title"] = title,
            ["body"] = body,
            ["guid"] = CreateGuid(outboxId)
        };
        AddTarget(push, config, secrets);
        var json = JsonSerializer.Serialize(push, Json.Options);
        if (Encoding.UTF8.GetByteCount(json) > 32 * 1024)
            throw new ArgumentException("Pushbullet payload exceeded the local safety bound.");
        return json;
    }

    private static void AddTarget(
        IDictionary<string, object> push,
        PushbulletConfiguration config,
        IReadOnlyDictionary<string, string> secrets)
    {
        var kind = config.TargetType.Trim().ToLowerInvariant();
        if (kind == "all") return;
        if (kind is not ("device" or "channel" or "email"))
            throw new ArgumentException("Pushbullet target type is invalid.");
        if (string.IsNullOrWhiteSpace(config.TargetSecretName) ||
            !secrets.TryGetValue(config.TargetSecretName, out var value) ||
            string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An encrypted Pushbullet target is required.");
        value = value.Trim();

        if (kind == "email")
        {
            if (value.Length > 254 || value.IndexOfAny(['\r', '\n']) >= 0 ||
                !MailAddress.TryCreate(value, out var parsed) ||
                !string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pushbullet target email is invalid.");
            push["email"] = value;
            return;
        }

        if (value.Length is < 1 or > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("Pushbullet target identifier is invalid.");
        push[kind == "device" ? "device_iden" : "channel_tag"] = value;
    }

    private static string BuildMessage(JsonElement root)
    {
        var builder = new StringBuilder();
        var message = Read(root, "message", false);
        if (!string.IsNullOrWhiteSpace(message)) builder.AppendLine(message.Trim()).AppendLine();
        Append(builder, "Priority", Read(root, "priority", false));
        Append(builder, "Type", Read(root, "type", false));
        Append(builder, "Agent", Read(root, "agent", false));
        Append(builder, "Project", Read(root, "project", false));
        builder.Append("Sent by AgentNotify.");
        // Keep the serialized JSON below 32 KiB even when the default JSON encoder
        // expands every non-ASCII scalar into one or two JSON Unicode escape sequences.
        return TruncateUtf8(builder.ToString().Replace('\0', ' '), 8 * 1024);
    }

    private static string CreateGuid(string outboxId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(outboxId));
        return Convert.ToHexString(digest).ToLowerInvariant()[..32];
    }

    private static void Append(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append(label).Append(": ").AppendLine(value.Trim().Replace('\0', ' '));
    }

    private static string TruncateUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return value;
        const string suffix = "…";
        var available = maximumBytes - Encoding.UTF8.GetByteCount(suffix);
        var builder = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > available) break;
            builder.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        return builder.Append(suffix).ToString();
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

    private static async Task<bool> HasValidPushAsync(
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
                throw new InvalidDataException("Pushbullet response exceeded the allowed size.");
            memory.Write(buffer, 0, read);
        }
        try
        {
            using var json = JsonDocument.Parse(memory.GetBuffer().AsMemory(0, checked((int)memory.Length)));
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("iden", out var iden) &&
                   iden.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(iden.GetString()) &&
                   root.TryGetProperty("type", out var type) &&
                   type.ValueKind == JsonValueKind.String &&
                   type.GetString() == "note" &&
                   (!root.TryGetProperty("active", out var active) || active.ValueKind == JsonValueKind.True);
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
            MaxConnectionsPerServer = 2,
            ConnectCallback = ConnectPushbulletAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectPushbulletAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(PushbulletRequest, out var validated) ||
            !validated ||
            !context.DnsEndPoint.Host.Equals("api.pushbullet.com", StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("Pushbullet transport refused an unvalidated destination.");
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            cancellationToken);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Pushbullet resolved to a disallowed address.");
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

    private sealed class PushbulletConfiguration
    {
        public string AccessTokenSecretName { get; set; } = "access_token";
        public string TargetType { get; set; } = "all";
        public string TargetSecretName { get; set; } = "target";
        public bool QuotaAcknowledged { get; set; }
    }
}
