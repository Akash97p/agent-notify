using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed class WebhookChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> AllowPrivateNetwork =
        new("AgentNotify.Webhook.AllowPrivateNetwork");
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Cookie",
        "Idempotency-Key",
        "Proxy-Authorization", "Proxy-Connection", "Trailer", "Upgrade"
    };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public WebhookChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public string Kind => "webhook";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        WebhookConfiguration config;
        try
        {
            config = ParseAndValidateConfiguration(delivery.Profile.ConfigJson, delivery.Secrets);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        string body;
        try
        {
            body = RenderBody(config.BodyTemplate, delivery);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("template_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, config.Uri);
        request.Options.Set(AllowPrivateNetwork, config.AllowPrivateNetwork);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", delivery.OutboxId);

        try
        {
            AddHeaders(request, config.Headers!);
            AddSecretHeaders(request, config.SecretHeaders!, delivery.Secrets);
            AddSignature(request, config.Signature, delivery.Secrets, body);
        }
        catch (ArgumentException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

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
                return DeliveryResult.Retry($"http_{statusCode}", statusCode);
            if (statusCode is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("http_redirect", statusCode);
            return DeliveryResult.PermanentFailure($"http_{statusCode}", statusCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    public static bool IsAddressAllowed(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6Multicast || IsLinkLocal(address))
            return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 0 || bytes[0] >= 224 ||
                bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2 ||
                bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
                bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                return false;
            if (allowPrivate)
                return true;
            return !(bytes[0] == 10 ||
                     bytes[0] == 127 ||
                     bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                     bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                     bytes[0] == 192 && bytes[1] == 168 ||
                     bytes[0] == 198 && bytes[1] is 18 or 19);
        }

        if (bytes.Length == 16 && bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            return false;
        if (allowPrivate)
            return true;
        return !IPAddress.IsLoopback(address) &&
               !address.IsIPv6SiteLocal &&
               !(bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc);
    }

    private static WebhookConfiguration ParseAndValidateConfiguration(
        string configJson,
        IReadOnlyDictionary<string, string> secrets)
    {
        var config = JsonSerializer.Deserialize<WebhookConfiguration>(configJson, Json.Options) ??
            throw new ArgumentException("Webhook configuration is required.");
        if (string.IsNullOrWhiteSpace(config.UrlSecretName) ||
            !secrets.TryGetValue(config.UrlSecretName, out var endpointUrl) ||
            !Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            endpointUrl.Length > 2048)
            throw new ArgumentException("Webhook URL must be an absolute HTTPS URL without credentials or fragments.");

        if ((uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) &&
            !config.AllowPrivateNetwork)
            throw new ArgumentException("Private webhook destinations require explicit consent.");
        if (IPAddress.TryParse(uri.Host, out var literal) &&
            !IsAddressAllowed(literal, config.AllowPrivateNetwork))
            throw new ArgumentException("Webhook destination address is not allowed.");

        config.Uri = uri;
        config.Headers ??= new Dictionary<string, string>();
        config.SecretHeaders ??= new Dictionary<string, string>();
        if (config.Headers.Count + config.SecretHeaders.Count > 24)
            throw new ArgumentException("Too many webhook headers.");

        ValidateHeaderMap(config.Headers, secrets: null);
        ValidateHeaderMap(config.SecretHeaders, secrets);
        var configuredHeaderNames = config.Headers.Keys
            .Concat(config.SecretHeaders.Keys)
            .ToArray();
        if (configuredHeaderNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            configuredHeaderNames.Length)
            throw new ArgumentException("Webhook header names must be unique.");
        if (config.Signature is not null)
        {
            ValidateHeaderName(config.Signature.HeaderName);
            ValidateHeaderName(config.Signature.TimestampHeaderName);
            if (config.Signature.HeaderName.Equals(
                    config.Signature.TimestampHeaderName,
                    StringComparison.OrdinalIgnoreCase) ||
                configuredHeaderNames.Contains(
                    config.Signature.HeaderName,
                    StringComparer.OrdinalIgnoreCase) ||
                configuredHeaderNames.Contains(
                    config.Signature.TimestampHeaderName,
                    StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Webhook signature headers must be unique.");
            if (string.IsNullOrWhiteSpace(config.Signature.SecretName) ||
                !secrets.ContainsKey(config.Signature.SecretName))
                throw new ArgumentException("Webhook signature secret is missing.");
        }
        return config;
    }

    private static void ValidateHeaderMap(
        IReadOnlyDictionary<string, string> headers,
        IReadOnlyDictionary<string, string>? secrets)
    {
        foreach (var (name, value) in headers)
        {
            ValidateHeaderName(name);
            if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
                throw new ArgumentException("Webhook header value is invalid.");
            if (secrets is not null && !secrets.ContainsKey(value))
                throw new ArgumentException("Webhook secret header references a missing secret.");
            if (secrets is null && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Authorization must reference an encrypted secret.");
            if (secrets is null && ContainsNewLine(value))
                throw new ArgumentException("Webhook header value is invalid.");
        }
    }

    private static void ValidateHeaderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            ForbiddenHeaders.Contains(name) || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
            !name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            throw new ArgumentException("Webhook header name is invalid.");
    }

    private static void AddHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(name, value))
                throw new ArgumentException("Webhook header could not be added.");
        }
    }

    private static void AddSecretHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> secretHeaders,
        IReadOnlyDictionary<string, string> secrets)
    {
        foreach (var (headerName, secretName) in secretHeaders)
        {
            var value = secrets[secretName];
            if (value.Length > 16 * 1024 || ContainsNewLine(value) ||
                !request.Headers.TryAddWithoutValidation(headerName, value))
                throw new ArgumentException("Webhook secret header could not be added.");
        }
    }

    private static void AddSignature(
        HttpRequestMessage request,
        WebhookSignatureConfiguration? signature,
        IReadOnlyDictionary<string, string> secrets,
        string body)
    {
        if (signature is null)
            return;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var key = Encoding.UTF8.GetBytes(secrets[signature.SecretName]);
        var content = Encoding.UTF8.GetBytes($"{timestamp}.{body}");
        try
        {
            var digest = HMACSHA256.HashData(key, content);
            if (!request.Headers.TryAddWithoutValidation(signature.TimestampHeaderName, timestamp) ||
                !request.Headers.TryAddWithoutValidation(
                    signature.HeaderName,
                    "sha256=" + Convert.ToHexString(digest).ToLowerInvariant()))
                throw new ArgumentException("Webhook signature headers could not be added.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static string RenderBody(JsonElement? template, OutboundDelivery delivery)
    {
        if (template is null || template.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return delivery.PayloadJson;
        if (template.Value.ValueKind is not JsonValueKind.Object)
            throw new InvalidOperationException("Webhook template must be a JSON object.");

        var root = JsonNode.Parse(template.Value.GetRawText())!;
        var payload = JsonNode.Parse(delivery.PayloadJson) ??
            throw new JsonException("Delivery payload is invalid.");
        ExpandTemplate(root, payload, delivery);
        var rendered = root.ToJsonString(Json.Options);
        if (Encoding.UTF8.GetByteCount(rendered) > 256 * 1024)
            throw new InvalidOperationException("Rendered webhook body is too large.");
        return rendered;
    }

    private static void ExpandTemplate(
        JsonNode node,
        JsonNode payload,
        OutboundDelivery delivery)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    text == "{{payload}}")
                {
                    obj[property.Key] = payload.DeepClone();
                }
                else if (property.Value is JsonValue stringValue &&
                         stringValue.TryGetValue<string>(out var stringText))
                {
                    obj[property.Key] = ReplaceTokens(stringText, delivery);
                }
                else if (property.Value is not null)
                {
                    ExpandTemplate(property.Value, payload, delivery);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value &&
                    value.TryGetValue<string>(out var text) &&
                    text == "{{payload}}")
                    array[index] = payload.DeepClone();
                else if (array[index] is JsonValue stringValue &&
                         stringValue.TryGetValue<string>(out var stringText))
                    array[index] = ReplaceTokens(stringText, delivery);
                else if (array[index] is not null)
                    ExpandTemplate(array[index]!, payload, delivery);
            }
        }
    }

    private static string ReplaceTokens(string value, OutboundDelivery delivery) =>
        value.Replace("{{outbox_id}}", delivery.OutboxId, StringComparison.Ordinal)
            .Replace("{{notification_id}}", delivery.NotificationId, StringComparison.Ordinal);

    private static HttpClient CreateHardenedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.Zero,
            MaxConnectionsPerServer = 4,
            ConnectCallback = ConnectValidatedAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var allowPrivate = context.InitialRequestMessage.Options.TryGetValue(
            AllowPrivateNetwork,
            out var configured) && configured;
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            cancellationToken);
        var allowed = addresses.Where(address => IsAddressAllowed(address, allowPrivate)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Webhook destination resolved to a disallowed address.");

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

    private static bool IsLinkLocal(IPAddress address)
    {
        if (address.IsIPv6LinkLocal)
            return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork &&
               bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool ContainsNewLine(string value) =>
        value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal);

    private sealed class WebhookConfiguration
    {
        public string UrlSecretName { get; set; } = "endpoint_url";
        public bool AllowPrivateNetwork { get; set; }
        public Dictionary<string, string>? Headers { get; set; }
        public Dictionary<string, string>? SecretHeaders { get; set; }
        public WebhookSignatureConfiguration? Signature { get; set; }
        public JsonElement? BodyTemplate { get; set; }
        public Uri Uri { get; set; } = null!;
    }

    private sealed class WebhookSignatureConfiguration
    {
        public string SecretName { get; set; } = "hmac_secret";
        public string HeaderName { get; set; } = "X-AgentNotify-Signature";
        public string TimestampHeaderName { get; set; } = "X-AgentNotify-Timestamp";
    }
}
