using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery.Channels;

public sealed record TelegramSendRequest(
    string BotToken,
    string ChatId,
    int? MessageThreadId,
    string Text,
    bool DisableNotification,
    bool ProtectContent);

public interface ITelegramSender
{
    Task<DeliveryResult> SendAsync(TelegramSendRequest request, CancellationToken cancellationToken);
}

public sealed class TelegramChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    private readonly ITelegramSender _sender;
    private readonly bool _ownsSender;

    public TelegramChannelAdapter(ITelegramSender? sender = null)
    {
        _ownsSender = sender is null;
        _sender = sender ?? new TelegramBotApiSender();
    }

    public string Kind => "telegram";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = ParseConfiguration(delivery.Profile.ConfigJson, delivery.Secrets);
            return await _sender.SendAsync(new TelegramSendRequest(
                delivery.Secrets[config.BotTokenSecretName],
                delivery.Secrets[config.ChatIdSecretName],
                config.MessageThreadId,
                BuildText(delivery.PayloadJson),
                config.DisableNotification,
                config.ProtectContent), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }
    }

    public void Dispose()
    {
        if (_ownsSender && _sender is IDisposable disposable)
            disposable.Dispose();
    }

    private static TelegramConfiguration ParseConfiguration(
        string configJson,
        IReadOnlyDictionary<string, string> secrets)
    {
        var config = JsonSerializer.Deserialize<TelegramConfiguration>(configJson, Json.Options) ??
            throw new ArgumentException("Telegram configuration is required.");
        if (config.MessageThreadId is <= 0)
            throw new ArgumentException("Telegram topic ID must be a positive integer.");
        if (string.IsNullOrWhiteSpace(config.BotTokenSecretName) ||
            !secrets.TryGetValue(config.BotTokenSecretName, out var token) ||
            !IsValidBotToken(token))
            throw new ArgumentException("An encrypted Telegram bot token is required.");
        if (string.IsNullOrWhiteSpace(config.ChatIdSecretName) ||
            !secrets.TryGetValue(config.ChatIdSecretName, out var chatId) ||
            !IsValidChatId(chatId))
            throw new ArgumentException("An encrypted Telegram chat ID is required.");
        return config;
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
            builder.Append('[').Append(priority.ToUpperInvariant()).Append("] ");
        builder.AppendLine(title.Trim());
        if (!string.IsNullOrWhiteSpace(message))
            builder.AppendLine().AppendLine(message.Trim());
        AppendField(builder, "Type", type);
        AppendField(builder, "Agent", agent);
        AppendField(builder, "Project", project);
        builder.AppendLine().Append("Sent by AgentNotify.");
        return TruncateTelegramText(builder.ToString().Replace('\0', ' '));
    }

    private static void AppendField(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            builder.Append(label).Append(": ").AppendLine(value.Trim());
    }

    private static string TruncateTelegramText(string text)
    {
        const int maximumLength = 4096;
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

    private static bool IsValidBotToken(string token)
    {
        if (token.Length is < 20 or > 256 || token.Any(char.IsWhiteSpace))
            return false;
        var separator = token.IndexOf(':');
        if (separator is < 5 or > 20 || separator != token.LastIndexOf(':'))
            return false;
        return token[..separator].All(char.IsAsciiDigit) &&
               token[(separator + 1)..].Length >= 10 &&
               token[(separator + 1)..].All(character =>
                   char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    private static bool IsValidChatId(string chatId)
    {
        if (chatId.Length is 0 or > 64 || chatId.Any(char.IsWhiteSpace))
            return false;
        if (chatId[0] == '@')
            return chatId.Length is >= 6 and <= 33 &&
                   chatId[1..].All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
        var digits = chatId[0] == '-' ? chatId[1..] : chatId;
        return digits.Length is >= 1 and <= 20 && digits.All(char.IsAsciiDigit) && digits.Any(c => c != '0');
    }

    private sealed class TelegramConfiguration
    {
        public string BotTokenSecretName { get; set; } = "bot_token";
        public string ChatIdSecretName { get; set; } = "chat_id";
        public int? MessageThreadId { get; set; }
        public bool DisableNotification { get; set; }
        public bool ProtectContent { get; set; } = true;
    }
}

public sealed class TelegramBotApiSender : ITelegramSender, IDisposable
{
    private static readonly HttpRequestOptionsKey<bool> TelegramRequest =
        new("AgentNotify.Telegram.FixedEndpoint");
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public TelegramBotApiSender(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? CreateHardenedClient();
    }

    public async Task<DeliveryResult> SendAsync(
        TelegramSendRequest request,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri($"https://api.telegram.org/bot{request.BotToken}/sendMessage");
        var payload = new TelegramSendPayload
        {
            ChatId = request.ChatId,
            MessageThreadId = request.MessageThreadId,
            Text = request.Text,
            DisableNotification = request.DisableNotification,
            ProtectContent = request.ProtectContent
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json.Options), Encoding.UTF8, "application/json")
        };
        message.Options.Set(TelegramRequest, true);
        message.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));

        try
        {
            using var response = await _client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (statusCode is >= 200 and <= 299)
            {
                var responseText = await ReadBoundedResponseAsync(response.Content, cancellationToken);
                using var document = JsonDocument.Parse(responseText);
                return document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                    ? DeliveryResult.Success(statusCode)
                    : DeliveryResult.Retry("telegram_invalid_response", statusCode);
            }
            if (statusCode is 408 or 425 or 429 || statusCode >= 500)
                return DeliveryResult.Retry($"telegram_{statusCode}", statusCode);
            if (statusCode is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("telegram_redirect", statusCode);
            return DeliveryResult.PermanentFailure($"telegram_{statusCode}", statusCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (JsonException)
        {
            return DeliveryResult.Retry("telegram_invalid_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    private static async Task<string> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 64 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (memory.Length + read > maximumBytes)
                throw new JsonException("Telegram response exceeded the allowed size.");
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
            ConnectCallback = ConnectTelegramAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectTelegramAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(TelegramRequest, out var isTelegram) ||
            !isTelegram ||
            !context.DnsEndPoint.Host.Equals("api.telegram.org", StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("Telegram transport refused a non-Telegram destination.");

        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            AddressFamily.Unspecified,
            cancellationToken);
        var allowed = addresses.Where(address => WebhookChannelAdapter.IsAddressAllowed(address, false)).ToArray();
        if (allowed.Length == 0 || allowed.Length != addresses.Length)
            throw new HttpRequestException("Telegram destination resolved to a disallowed address.");

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

    private sealed class TelegramSendPayload
    {
        [JsonPropertyName("chat_id")]
        public string ChatId { get; init; } = "";

        [JsonPropertyName("message_thread_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MessageThreadId { get; init; }

        [JsonPropertyName("text")]
        public string Text { get; init; } = "";

        [JsonPropertyName("disable_notification")]
        public bool DisableNotification { get; init; }

        [JsonPropertyName("protect_content")]
        public bool ProtectContent { get; init; }

        [JsonPropertyName("link_preview_options")]
        public object LinkPreviewOptions { get; } = new { is_disabled = true };
    }
}
