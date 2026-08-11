using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class ZohoCliqChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> CliqRequest = new("AgentNotify.ZohoCliq.FixedEndpoint");
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cliq.zoho.com", "cliq.zoho.eu", "cliq.zoho.in", "cliq.zoho.com.au", "cliq.zoho.com.cn",
        "cliq.zoho.jp", "cliq.zoho.sa", "cliq.zoho.uk", "cliq.zohocloud.ca"
    };
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public ZohoCliqChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "zoho_cliq";

    public async Task<DeliveryResult> DeliverAsync(OutboundDelivery delivery, CancellationToken cancellationToken)
    {
        Uri endpoint;
        string text;
        try
        {
            var config = JsonSerializer.Deserialize<CliqConfiguration>(delivery.Profile.ConfigJson, Json.Options) ??
                throw new ArgumentException("Zoho Cliq configuration is required.");
            if (string.IsNullOrWhiteSpace(config.WebhookUrlSecretName) ||
                !delivery.Secrets.TryGetValue(config.WebhookUrlSecretName, out var webhookUrl))
                throw new ArgumentException("An encrypted Zoho Cliq webhook URL is required.");
            endpoint = ValidateWebhookUri(webhookUrl);
            text = BuildText(delivery.PayloadJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(new CliqPayload(text), Json.Options), Encoding.UTF8, "application/json")
        };
        request.Options.Set(CliqRequest, true);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 200 and <= 299) return DeliveryResult.Success(status);
            if (status is 408 or 425 or 429 || status >= 500) return DeliveryResult.Retry($"zoho_cliq_{status}", status);
            if (status is >= 300 and <= 399) return DeliveryResult.PermanentFailure("zoho_cliq_redirect", status);
            return DeliveryResult.PermanentFailure($"zoho_cliq_{status}", status);
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
    }

    private static Uri ValidateWebhookUri(string endpoint)
    {
        if (endpoint.Length > 4096 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.Host) || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use an official Zoho Cliq HTTPS webhook URL.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5 || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("v2", StringComparison.OrdinalIgnoreCase) ||
            !IsSupportedResource(segments[2]) || !IsIdentifier(segments[3]) ||
            !segments[4].Equals("message", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Zoho Cliq webhook URL path is invalid.");
        ValidateQuery(uri.Query);
        return uri;
    }

    private static bool IsSupportedResource(string value) =>
        value.Equals("channelsbyname", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("channels", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("bots", StringComparison.OrdinalIgnoreCase);

    private static void ValidateQuery(string query)
    {
        if (query.Length is < 10 or > 2048 || query[0] != '?')
            throw new ArgumentException("Zoho Cliq webhook token is missing.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || !values.TryAdd(Uri.UnescapeDataString(part[..separator]), Uri.UnescapeDataString(part[(separator + 1)..])))
                throw new ArgumentException("Zoho Cliq webhook query is invalid.");
        }
        if (!values.TryGetValue("zapikey", out var token) || token.Length is < 10 or > 1024 || token.Any(char.IsWhiteSpace) ||
            values.Keys.Any(key => !key.Equals("zapikey", StringComparison.OrdinalIgnoreCase) &&
                                   !key.Equals("bot_unique_name", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Zoho Cliq webhook token is invalid.");
        if (values.TryGetValue("bot_unique_name", out var bot) && !IsIdentifier(bot))
            throw new ArgumentException("Zoho Cliq bot name is invalid.");
    }

    private static string BuildText(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Notification payload is invalid.");
        var title = ReadString(root, "title", true);
        var message = ReadString(root, "message", false);
        var priority = ReadString(root, "priority", false);
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(priority)) builder.Append('[').Append(Escape(priority.ToUpperInvariant())).Append("] ");
        builder.AppendLine(Escape(title));
        if (!string.IsNullOrWhiteSpace(message)) builder.AppendLine().AppendLine(Escape(message));
        Append(builder, "Type", ReadString(root, "type", false));
        Append(builder, "Agent", ReadString(root, "agent", false));
        Append(builder, "Project", ReadString(root, "project", false));
        builder.AppendLine().Append("Sent by AgentNotify.");
        return Truncate(builder.ToString().Replace('\0', ' '), 5000);
    }

    private static void Append(StringBuilder builder, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) builder.Append(name).Append(": ").AppendLine(Escape(value));
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (character is '\\' or '*' or '_' or '~' or '`' or '[' or ']' or '(' or ')') builder.Append('\\');
            builder.Append(character);
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
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"Notification {name} must be text.");
        return value.GetString() ?? "";
    }

    private static bool IsIdentifier(string value) => value.Length is >= 1 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static HttpClient CreateHardenedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 2,
            ConnectCallback = ConnectCliqAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectCliqAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(CliqRequest, out var isCliq) || !isCliq ||
            !AllowedHosts.Contains(context.DnsEndPoint.Host) || context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("Zoho Cliq transport refused a non-Cliq destination.");
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.Unspecified, token);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Zoho Cliq destination resolved to a disallowed address.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, token); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }

    private sealed class CliqConfiguration { public string WebhookUrlSecretName { get; set; } = "webhook_url"; }
    private sealed record CliqPayload([property: JsonPropertyName("text")] string Text);
}
