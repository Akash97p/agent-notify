using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class GoogleChatChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private const string Host = "chat.googleapis.com";
    private const int MaximumPayloadBytes = 31_500;
    private static readonly HttpRequestOptionsKey<bool> GoogleChatRequest =
        new("AgentNotify.GoogleChat.FixedEndpoint");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private DateTimeOffset _nextSendAt;

    public GoogleChatChannelAdapter(HttpClient? client = null, TimeProvider? timeProvider = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Kind => "google_chat";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        Uri endpoint;
        string payload;
        try
        {
            var config = JsonSerializer.Deserialize<GoogleChatConfiguration>(
                delivery.Profile.ConfigJson,
                Json.Options) ?? throw new ArgumentException("Google Chat configuration is required.");
            if (string.IsNullOrWhiteSpace(config.WebhookUrlSecretName) ||
                !delivery.Secrets.TryGetValue(config.WebhookUrlSecretName, out var webhookUrl))
                throw new ArgumentException("An encrypted Google Chat webhook URL is required.");

            endpoint = ValidateWebhookUri(webhookUrl);
            var threadKey = ValidateThreadKey(config.ThreadKey);
            endpoint = AddReplyOption(endpoint, threadKey, config.ThreadReplyPolicy);
            payload = BuildPayload(delivery.PayloadJson, threadKey);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Options.Set(GoogleChatRequest, true);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));

        try
        {
            using var response = await SendRateLimitedAsync(request, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 200 and <= 299) return DeliveryResult.Success(status);
            if (status is 408 or 425 or 429 || status >= 500)
                return DeliveryResult.Retry($"google_chat_{status}", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("google_chat_redirect", status);
            return DeliveryResult.PermanentFailure($"google_chat_{status}", status);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
        _sendGate.Dispose();
    }

    private async Task<HttpResponseMessage> SendRateLimitedAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_nextSendAt > now)
                await Task.Delay(_nextSendAt - now, _timeProvider, cancellationToken);
            _nextSendAt = _timeProvider.GetUtcNow().AddSeconds(1);
            return await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static Uri ValidateWebhookUri(string endpoint)
    {
        if (endpoint.Length > 4096 ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use an official Google Chat HTTPS webhook URL.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4 ||
            !segments[0].Equals("v1", StringComparison.Ordinal) ||
            !segments[1].Equals("spaces", StringComparison.Ordinal) ||
            !IsResourceIdentifier(segments[2]) ||
            !segments[3].Equals("messages", StringComparison.Ordinal))
            throw new ArgumentException("Google Chat webhook URL path is invalid.");

        ValidateQuery(uri.Query);
        return uri;
    }

    private static void ValidateQuery(string query)
    {
        if (query.Length is < 10 or > 3072 || query[0] != '?')
            throw new ArgumentException("Google Chat webhook credentials are missing.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                throw new ArgumentException("Google Chat webhook query is invalid.");
            var name = Uri.UnescapeDataString(part[..separator]);
            var value = Uri.UnescapeDataString(part[(separator + 1)..]);
            if (!values.TryAdd(name, value))
                throw new ArgumentException("Google Chat webhook query contains duplicates.");
        }

        if (values.Count != 2 ||
            !values.TryGetValue("key", out var key) ||
            !values.TryGetValue("token", out var token) ||
            !IsCredential(key) ||
            !IsCredential(token))
            throw new ArgumentException("Google Chat webhook credentials are invalid.");
    }

    private static string? ValidateThreadKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = value.Trim();
        if (result.Length > 4000 || result.Any(char.IsControl))
            throw new ArgumentException("Google Chat thread key is invalid.");
        return result;
    }

    private static Uri AddReplyOption(Uri endpoint, string? threadKey, string? policy)
    {
        if (threadKey is null)
        {
            if (!string.IsNullOrWhiteSpace(policy) && policy != "fallback")
                throw new ArgumentException("Google Chat reply policy requires a thread key.");
            return endpoint;
        }

        var option = policy switch
        {
            null or "" or "fallback" => "REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD",
            "fail" => "REPLY_MESSAGE_OR_FAIL",
            _ => throw new ArgumentException("Google Chat reply policy is invalid.")
        };
        return new Uri(endpoint.AbsoluteUri + "&messageReplyOption=" + option, UriKind.Absolute);
    }

    private static string BuildPayload(string payloadJson, string? threadKey)
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

        return SerializeBounded(builder.ToString().Replace('\0', ' '), threadKey);
    }

    private static string SerializeBounded(string text, string? threadKey)
    {
        var payload = Serialize(text, threadKey);
        if (Encoding.UTF8.GetByteCount(payload) <= MaximumPayloadBytes) return payload;

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var length = SafeLength(text, middle);
            var candidate = Serialize(text[..length] + "…", threadKey);
            if (Encoding.UTF8.GetByteCount(candidate) <= MaximumPayloadBytes) low = middle;
            else high = middle - 1;
        }

        var finalLength = SafeLength(text, low);
        return Serialize(text[..finalLength] + "…", threadKey);
    }

    private static string Serialize(string text, string? threadKey) =>
        JsonSerializer.Serialize(
            new GoogleChatPayload(
                text,
                threadKey is null ? null : new GoogleChatThread(threadKey)),
            Json.Options);

    private static int SafeLength(string value, int length)
    {
        var result = Math.Min(length, value.Length);
        if (result > 0 && result < value.Length && char.IsHighSurrogate(value[result - 1])) result--;
        return result;
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
            if (character is '\\' or '*' or '_' or '~' or '`') builder.Append('\\');
            builder.Append(character switch
            {
                '<' => '‹',
                '>' => '›',
                _ => character
            });
        }
        return builder.ToString();
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

    private static bool IsResourceIdentifier(string value) =>
        value.Length is >= 1 and <= 256 &&
        !value.Contains('%', StringComparison.Ordinal) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsCredential(string value) =>
        value.Length is >= 10 and <= 2048 &&
        value.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character));

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
            ConnectCallback = ConnectGoogleChatAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectGoogleChatAsync(
        SocketsHttpConnectionContext context,
        CancellationToken token)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(GoogleChatRequest, out var isGoogleChat) ||
            !isGoogleChat ||
            !context.DnsEndPoint.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("Google Chat transport refused a non-Google destination.");

        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            token);
        var allowed = addresses
            .Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false))
            .ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Google Chat destination resolved to a disallowed address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, token);
            return new NetworkStream(socket, true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class GoogleChatConfiguration
    {
        public string WebhookUrlSecretName { get; set; } = "webhook_url";
        public string? ThreadKey { get; set; }
        public string? ThreadReplyPolicy { get; set; } = "fallback";
    }

    private sealed record GoogleChatPayload(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("thread"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        GoogleChatThread? Thread);

    private sealed record GoogleChatThread(
        [property: JsonPropertyName("threadKey")] string ThreadKey);
}
