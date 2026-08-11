using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class TeamsChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> TeamsRequest = new("AgentNotify.Teams.FixedEndpoint");
    private const string HostSuffix = ".environment.api.powerplatform.com";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public TeamsChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "teams";

    public async Task<DeliveryResult> DeliverAsync(OutboundDelivery delivery, CancellationToken cancellationToken)
    {
        Uri endpoint;
        TeamsPayload payload;
        try
        {
            var config = JsonSerializer.Deserialize<TeamsConfiguration>(delivery.Profile.ConfigJson, Json.Options) ??
                throw new ArgumentException("Teams configuration is required.");
            if (string.IsNullOrWhiteSpace(config.WebhookUrlSecretName) ||
                !delivery.Secrets.TryGetValue(config.WebhookUrlSecretName, out var webhookUrl))
                throw new ArgumentException("An encrypted Teams Workflows URL is required.");
            endpoint = ValidateWebhookUri(webhookUrl);
            payload = BuildPayload(delivery.PayloadJson);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json.Options), Encoding.UTF8, "application/json")
        };
        request.Options.Set(TeamsRequest, true);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 200 and <= 299) return DeliveryResult.Success(status);
            if (status is 408 or 425 or 429 || status >= 500) return DeliveryResult.Retry($"teams_{status}", status);
            if (status is >= 300 and <= 399) return DeliveryResult.PermanentFailure("teams_redirect", status);
            return DeliveryResult.PermanentFailure($"teams_{status}", status);
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
        if (endpoint.Length > 8192 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Length <= HostSuffix.Length || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use a current Microsoft Teams Workflows HTTPS URL.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 9 ||
            !segments[0].Equals("powerautomate", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("automations", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("direct", StringComparison.OrdinalIgnoreCase) ||
            !segments[3].Equals("workflows", StringComparison.OrdinalIgnoreCase) ||
            !IsIdentifier(segments[4]) ||
            !segments[5].Equals("triggers", StringComparison.OrdinalIgnoreCase) ||
            !segments[6].Equals("manual", StringComparison.OrdinalIgnoreCase) ||
            !segments[7].Equals("paths", StringComparison.OrdinalIgnoreCase) ||
            !segments[8].Equals("invoke", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Teams Workflows URL path is invalid.");
        ValidateSignedQuery(uri.Query);
        return uri;
    }

    private static void ValidateSignedQuery(string query)
    {
        if (query.Length is < 20 or > 4096 || query[0] != '?')
            throw new ArgumentException("Teams Workflows URL signature is missing.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || !values.TryAdd(
                    Uri.UnescapeDataString(part[..separator]),
                    Uri.UnescapeDataString(part[(separator + 1)..])))
                throw new ArgumentException("Teams Workflows URL query is invalid.");
        }
        foreach (var required in new[] { "api-version", "sp", "sv", "sig" })
            if (!values.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value) || value.Length > 1024)
                throw new ArgumentException("Teams Workflows URL signature is incomplete.");
    }

    private static TeamsPayload BuildPayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Notification payload is invalid.");
        var title = ReadString(root, "title", true);
        var message = ReadString(root, "message", false);
        var priority = ReadString(root, "priority", false);
        var facts = new List<TeamsFact>();
        AddFact(facts, "Priority", priority);
        AddFact(facts, "Type", ReadString(root, "type", false));
        AddFact(facts, "Agent", ReadString(root, "agent", false));
        AddFact(facts, "Project", ReadString(root, "project", false));
        var body = new List<TeamsElement>
        {
            new() { Type = "TextBlock", Text = Truncate(EscapeText(title), 300), Weight = "Bolder", Size = "Medium", Wrap = true }
        };
        if (!string.IsNullOrWhiteSpace(message))
            body.Add(new TeamsElement { Type = "TextBlock", Text = Truncate(EscapeText(message), 6000), Wrap = true, Spacing = "Medium" });
        if (facts.Count > 0) body.Add(new TeamsElement { Type = "FactSet", Facts = facts });
        body.Add(new TeamsElement { Type = "TextBlock", Text = "Sent by AgentNotify.", IsSubtle = true, Size = "Small", Wrap = true });
        return new TeamsPayload
        {
            Attachments = [new TeamsAttachment { Content = new TeamsCard { Body = body } }]
        };
    }

    private static void AddFact(List<TeamsFact> facts, string title, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) facts.Add(new TeamsFact(title, Truncate(EscapeText(value), 500)));
    }

    private static string EscapeText(string value) => value.Trim()
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("~", "\\~", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("<at>", "&lt;at&gt;", StringComparison.OrdinalIgnoreCase)
        .Replace("</at>", "&lt;/at&gt;", StringComparison.OrdinalIgnoreCase)
        .Replace("\0", " ", StringComparison.Ordinal);

    private static string Truncate(string value, int limit)
    {
        if (value.Length <= limit) return value;
        var length = limit - 1;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + "…";
    }

    private static string ReadString(JsonElement root, string name, bool required)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required) throw new ArgumentException($"Notification {name} is required.");
            return "";
        }
        if (element.ValueKind != JsonValueKind.String) throw new ArgumentException($"Notification {name} must be text.");
        return element.GetString() ?? "";
    }

    private static bool IsIdentifier(string value) => value.Length is >= 16 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static HttpClient CreateHardenedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 2,
            ConnectCallback = ConnectTeamsAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectTeamsAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(TeamsRequest, out var isTeams) || !isTeams ||
            !context.DnsEndPoint.Host.EndsWith(HostSuffix, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("Teams transport refused a non-Microsoft destination.");
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.Unspecified, token);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Teams destination resolved to a disallowed address.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, token); return new NetworkStream(socket, true); }
        catch { socket.Dispose(); throw; }
    }

    private sealed class TeamsConfiguration { public string WebhookUrlSecretName { get; set; } = "webhook_url"; }
    private sealed class TeamsPayload
    {
        [JsonPropertyName("type")] public string Type => "message";
        [JsonPropertyName("attachments")] public IReadOnlyList<TeamsAttachment> Attachments { get; init; } = [];
    }
    private sealed class TeamsAttachment
    {
        [JsonPropertyName("contentType")] public string ContentType => "application/vnd.microsoft.card.adaptive";
        [JsonPropertyName("contentUrl")] public object? ContentUrl => null;
        [JsonPropertyName("content")] public TeamsCard Content { get; init; } = new();
    }
    private sealed class TeamsCard
    {
        [JsonPropertyName("$schema")] public string Schema => "http://adaptivecards.io/schemas/adaptive-card.json";
        [JsonPropertyName("type")] public string Type => "AdaptiveCard";
        [JsonPropertyName("version")] public string Version => "1.2";
        [JsonPropertyName("body")] public IReadOnlyList<TeamsElement> Body { get; init; } = [];
    }
    private sealed class TeamsElement
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "";
        [JsonPropertyName("text")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; init; }
        [JsonPropertyName("weight")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Weight { get; init; }
        [JsonPropertyName("size")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Size { get; init; }
        [JsonPropertyName("spacing")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Spacing { get; init; }
        [JsonPropertyName("wrap")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Wrap { get; init; }
        [JsonPropertyName("isSubtle")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool IsSubtle { get; init; }
        [JsonPropertyName("facts")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public IReadOnlyList<TeamsFact>? Facts { get; init; }
    }
    private sealed record TeamsFact([property: JsonPropertyName("title")] string Title, [property: JsonPropertyName("value")] string Value);
}
