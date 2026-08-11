using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class DiscordChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> DiscordRequest =
        new("AgentNotify.Discord.FixedEndpoint");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public DiscordChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "discord";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        DiscordConfiguration config;
        Uri endpoint;
        string content;
        try
        {
            config = ParseConfiguration(delivery.Profile.ConfigJson, delivery.Secrets);
            endpoint = BuildEndpoint(delivery.Secrets[config.WebhookUrlSecretName], config.ThreadId);
            content = BuildContent(delivery.PayloadJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        var payload = new DiscordWebhookPayload
        {
            Content = content,
            Username = config.Username,
            AllowedMentions = new DiscordAllowedMentions()
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json.Options), Encoding.UTF8, "application/json")
        };
        request.Options.Set(DiscordRequest, true);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 200 and <= 299)
                return DeliveryResult.Success(statusCode);
            if (statusCode is 408 or 425 or 429 || statusCode >= 500)
                return DeliveryResult.Retry($"discord_{statusCode}", statusCode);
            if (statusCode is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("discord_redirect", statusCode);
            return DeliveryResult.PermanentFailure($"discord_{statusCode}", statusCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private static DiscordConfiguration ParseConfiguration(
        string configJson,
        IReadOnlyDictionary<string, string> secrets)
    {
        var config = JsonSerializer.Deserialize<DiscordConfiguration>(configJson, Json.Options) ??
            throw new ArgumentException("Discord configuration is required.");
        if (string.IsNullOrWhiteSpace(config.WebhookUrlSecretName) ||
            !secrets.TryGetValue(config.WebhookUrlSecretName, out var endpoint))
            throw new ArgumentException("An encrypted Discord webhook URL is required.");
        _ = ValidateWebhookUri(endpoint);
        config.Username = config.Username.Trim();
        if (config.Username.Length is 0 or > 80 || config.Username.Any(char.IsControl))
            throw new ArgumentException("Discord webhook username is invalid.");
        if (config.ThreadId is not null && !IsSnowflake(config.ThreadId))
            throw new ArgumentException("Discord thread ID is invalid.");
        return config;
    }

    private static Uri BuildEndpoint(string endpoint, string? threadId)
    {
        var uri = ValidateWebhookUri(endpoint);
        var builder = new UriBuilder(uri)
        {
            Query = threadId is null
                ? "wait=true"
                : $"wait=true&thread_id={threadId}"
        };
        return builder.Uri;
    }

    private static Uri ValidateWebhookUri(string endpoint)
    {
        if (endpoint.Length > 2048 ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use an official Discord HTTPS incoming-webhook URL.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var webhookIndex = segments.Length > 0 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            ? 1
            : -1;
        if (webhookIndex >= 0 && segments.Length == 5 &&
            segments[1].Length is >= 2 and <= 4 && segments[1][0] is 'v' or 'V' &&
            segments[1][1..].All(char.IsAsciiDigit))
            webhookIndex = 2;
        if (webhookIndex < 0 || segments.Length != webhookIndex + 3 ||
            !segments[webhookIndex].Equals("webhooks", StringComparison.OrdinalIgnoreCase) ||
            !IsSnowflake(segments[webhookIndex + 1]) ||
            !IsWebhookToken(segments[webhookIndex + 2]))
            throw new ArgumentException("Discord webhook URL path is invalid.");
        return uri;
    }

    private static string BuildContent(string payloadJson)
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

        var builder = new StringBuilder("**");
        if (!string.IsNullOrWhiteSpace(priority))
            builder.Append('[').Append(EscapeMarkdown(priority.ToUpperInvariant())).Append("] ");
        builder.Append(EscapeMarkdown(title.Trim())).AppendLine("**");
        if (!string.IsNullOrWhiteSpace(message))
            builder.AppendLine().AppendLine(EscapeMarkdown(message.Trim()));
        AppendField(builder, "Type", type);
        AppendField(builder, "Agent", agent);
        AppendField(builder, "Project", project);
        builder.AppendLine().Append("_Sent by AgentNotify._");
        return TruncateContent(builder.ToString().Replace('\0', ' '));
    }

    private static void AppendField(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append("**").Append(label).Append(":** ")
                .AppendLine(EscapeMarkdown(value.Trim()));
    }

    private static string EscapeMarkdown(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '*' or '_' or '~' or '`' or '|' or '>')
                builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string TruncateContent(string content)
    {
        const int maximumLength = 2000;
        if (content.Length <= maximumLength)
            return content;
        var length = maximumLength - 1;
        if (char.IsHighSurrogate(content[length - 1]))
            length--;
        return content[..length] + "…";
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

    private static bool IsSnowflake(string value) =>
        value.Length is >= 5 and <= 20 && value.All(char.IsAsciiDigit) && value.Any(c => c != '0');

    private static bool IsWebhookToken(string value) =>
        value.Length is >= 20 and <= 256 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

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
            ConnectCallback = ConnectDiscordAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectDiscordAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(DiscordRequest, out var isDiscord) ||
            !isDiscord ||
            !context.DnsEndPoint.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("Discord transport refused a non-Discord destination.");

        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            cancellationToken);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Discord destination resolved to a disallowed address.");

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

    private sealed class DiscordConfiguration
    {
        public string WebhookUrlSecretName { get; set; } = "webhook_url";
        public string Username { get; set; } = "AgentNotify";
        public string? ThreadId { get; set; }
    }

    private sealed class DiscordWebhookPayload
    {
        [JsonPropertyName("content")]
        public string Content { get; init; } = "";

        [JsonPropertyName("username")]
        public string Username { get; init; } = "";

        [JsonPropertyName("tts")]
        public bool Tts => false;

        [JsonPropertyName("allowed_mentions")]
        public DiscordAllowedMentions AllowedMentions { get; init; } = new();
    }

    private sealed class DiscordAllowedMentions
    {
        [JsonPropertyName("parse")]
        public IReadOnlyList<string> Parse { get; } = [];

        [JsonPropertyName("replied_user")]
        public bool RepliedUser => false;
    }
}
