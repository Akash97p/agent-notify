using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class SlackChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> SlackRequest =
        new("AgentNotify.Slack.FixedEndpoint");
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "hooks.slack.com",
        "hooks.slack-gov.com"
    };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public SlackChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "slack";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        SlackConfiguration config;
        Uri endpoint;
        string text;
        try
        {
            config = ParseConfiguration(delivery.Profile.ConfigJson, delivery.Secrets);
            endpoint = ValidateWebhookUri(delivery.Secrets[config.WebhookUrlSecretName]);
            text = BuildText(delivery.PayloadJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        var payload = new SlackWebhookPayload
        {
            Text = text,
            ThreadTimestamp = config.ThreadTimestamp
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json.Options), Encoding.UTF8, "application/json")
        };
        request.Options.Set(SlackRequest, true);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 200 and <= 299)
            {
                var body = await ReadBoundedResponseAsync(response.Content, cancellationToken);
                return statusCode == 204 || body.Trim().Equals("ok", StringComparison.OrdinalIgnoreCase)
                    ? DeliveryResult.Success(statusCode)
                    : DeliveryResult.Retry("slack_invalid_response", statusCode);
            }
            if (statusCode is 408 or 425 or 429 || statusCode >= 500)
                return DeliveryResult.Retry($"slack_{statusCode}", statusCode);
            if (statusCode is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("slack_redirect", statusCode);
            return DeliveryResult.PermanentFailure($"slack_{statusCode}", statusCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.Retry("slack_invalid_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private static SlackConfiguration ParseConfiguration(
        string configJson,
        IReadOnlyDictionary<string, string> secrets)
    {
        var config = JsonSerializer.Deserialize<SlackConfiguration>(configJson, Json.Options) ??
            throw new ArgumentException("Slack configuration is required.");
        if (string.IsNullOrWhiteSpace(config.WebhookUrlSecretName) ||
            !secrets.TryGetValue(config.WebhookUrlSecretName, out var endpoint))
            throw new ArgumentException("An encrypted Slack webhook URL is required.");
        _ = ValidateWebhookUri(endpoint);
        if (config.ThreadTimestamp is not null && !IsThreadTimestamp(config.ThreadTimestamp))
            throw new ArgumentException("Slack thread timestamp is invalid.");
        return config;
    }

    private static Uri ValidateWebhookUri(string endpoint)
    {
        if (endpoint.Length > 2048 ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !AllowedHosts.Contains(uri.Host) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use an official Slack HTTPS incoming-webhook URL.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 4 ||
            !segments[0].Equals("services", StringComparison.OrdinalIgnoreCase) ||
            !IsWebhookIdentifier(segments[1]) ||
            !IsWebhookIdentifier(segments[2]) ||
            !IsWebhookToken(segments[3]))
            throw new ArgumentException("Slack webhook URL path is invalid.");
        return uri;
    }

    private static string BuildText(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload is invalid.");
        var title = ReadString(root, "title", required: true);
        var message = ReadString(root, "message", required: false);
        var priority = ReadString(root, "priority", required: false);
        var type = ReadString(root, "type", required: false);
        var agent = ReadString(root, "agent", required: false);
        var project = ReadString(root, "project", required: false);

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(priority))
            builder.Append('[').Append(EscapeSlackText(priority.ToUpperInvariant())).Append("] ");
        builder.AppendLine(EscapeSlackText(title.Trim()));
        if (!string.IsNullOrWhiteSpace(message))
            builder.AppendLine().AppendLine(EscapeSlackText(message.Trim()));
        AppendField(builder, "Type", type);
        AppendField(builder, "Agent", agent);
        AppendField(builder, "Project", project);
        builder.AppendLine().Append("Sent by AgentNotify.");
        return TruncateText(builder.ToString().Replace('\0', ' '));
    }

    private static void AppendField(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append(label).Append(": ").AppendLine(EscapeSlackText(value.Trim()));
    }

    private static string EscapeSlackText(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string TruncateText(string text)
    {
        const int maximumLength = 4000;
        if (text.Length <= maximumLength)
            return text;
        var length = maximumLength - 1;
        if (char.IsHighSurrogate(text[length - 1]))
            length--;
        return text[..length] + "…";
    }

    private static string ReadString(JsonElement root, string name, bool required)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new ArgumentException($"Notification {name} is required.");
            return "";
        }
        if (element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Notification {name} must be text.");
        return element.GetString() ?? "";
    }

    private static bool IsThreadTimestamp(string value)
    {
        var separator = value.IndexOf('.');
        return separator is >= 10 and <= 20 && separator == value.LastIndexOf('.') &&
               value[..separator].All(char.IsAsciiDigit) &&
               value[(separator + 1)..].Length == 6 &&
               value[(separator + 1)..].All(char.IsAsciiDigit);
    }

    private static bool IsWebhookIdentifier(string value) =>
        value.Length is >= 5 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool IsWebhookToken(string value) =>
        value.Length is >= 20 and <= 256 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

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
            if (read == 0)
                break;
            if (memory.Length + read > maximumBytes)
                throw new InvalidDataException("Slack response exceeded the allowed size.");
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
            ConnectCallback = ConnectSlackAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectSlackAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(SlackRequest, out var isSlack) ||
            !isSlack ||
            !AllowedHosts.Contains(context.DnsEndPoint.Host) ||
            context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("Slack transport refused a non-Slack destination.");

        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            cancellationToken);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Slack destination resolved to a disallowed address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class SlackConfiguration
    {
        public string WebhookUrlSecretName { get; set; } = "webhook_url";
        public string? ThreadTimestamp { get; set; }
    }

    private sealed class SlackWebhookPayload
    {
        [JsonPropertyName("text")]
        public string Text { get; init; } = "";

        [JsonPropertyName("mrkdwn")]
        public bool Markdown => false;

        [JsonPropertyName("link_names")]
        public int LinkNames => 0;

        [JsonPropertyName("thread_ts")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ThreadTimestamp { get; init; }
    }
}
