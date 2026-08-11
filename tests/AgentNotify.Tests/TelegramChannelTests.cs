using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class TelegramChannelTests
{
    [Fact]
    public async Task BuildsProtectedTelegramMessageFromEncryptedDestination()
    {
        var sender = new RecordingSender(DeliveryResult.Success(200));
        var adapter = new TelegramChannelAdapter(sender);
        var delivery = MakeDelivery(
            """
            {
              "messageThreadId":42,
              "disableNotification":true,
              "protectContent":true
            }
            """);

        var result = await adapter.DeliverAsync(delivery, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(sender.Requests);
        Assert.Equal(ValidToken, request.BotToken);
        Assert.Equal("-1001234567890", request.ChatId);
        Assert.Equal(42, request.MessageThreadId);
        Assert.True(request.DisableNotification);
        Assert.True(request.ProtectContent);
        Assert.Contains("[CRITICAL] Build failed", request.Text, StringComparison.Ordinal);
        Assert.Contains("Compiler error", request.Text, StringComparison.Ordinal);
        Assert.Contains("Agent: codex", request.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OmitsRouteRedactedMessageAndDefaultsToProtectedContent()
    {
        var sender = new RecordingSender(DeliveryResult.Success());
        var adapter = new TelegramChannelAdapter(sender);
        var delivery = MakeDelivery(payload: "{\"title\":\"Secret build\",\"message\":null}");

        var result = await adapter.DeliverAsync(delivery, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(sender.Requests);
        Assert.DoesNotContain("Compiler error", request.Text, StringComparison.Ordinal);
        Assert.True(request.ProtectContent);
        Assert.False(request.DisableNotification);
    }

    [Theory]
    [InlineData("not-a-token", "-1001234567890")]
    [InlineData("123456:bad/token", "-1001234567890")]
    [InlineData(ValidToken, "not a chat")]
    [InlineData(ValidToken, "@bad!")]
    [InlineData(ValidToken, "0")]
    public async Task RejectsInvalidEncryptedTokenOrChat(string token, string chatId)
    {
        var sender = new RecordingSender(DeliveryResult.Success());
        var adapter = new TelegramChannelAdapter(sender);
        var secrets = new Dictionary<string, string>
        {
            ["bot_token"] = token,
            ["chat_id"] = chatId
        };

        var result = await adapter.DeliverAsync(MakeDelivery(secrets: secrets), CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task RejectsInvalidTopicId()
    {
        var sender = new RecordingSender(DeliveryResult.Success());
        var adapter = new TelegramChannelAdapter(sender);

        var result = await adapter.DeliverAsync(
            MakeDelivery("{\"messageThreadId\":0}"),
            CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task TruncatesLongMessagesWithoutSplittingSurrogatePair()
    {
        var sender = new RecordingSender(DeliveryResult.Success());
        var adapter = new TelegramChannelAdapter(sender);
        var payload = JsonSerializer.Serialize(new
        {
            title = "Build",
            message = new string('a', 4090) + "😀"
        });

        await adapter.DeliverAsync(MakeDelivery(payload: payload), CancellationToken.None);

        var text = Assert.Single(sender.Requests).Text;
        Assert.True(text.Length <= 4096);
        Assert.False(char.IsHighSurrogate(text[^1]));
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SenderPostsPlainJsonToOfficialApiWithoutTokenInBody()
    {
        var handler = new RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":1}}")
        });
        using var sender = new TelegramBotApiSender(new HttpClient(handler));
        var request = new TelegramSendRequest(
            ValidToken,
            "@agentnotify_test",
            17,
            "Hello <world>",
            false,
            true);

        var result = await sender.SendAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https", handler.RequestUri!.Scheme);
        Assert.Equal("api.telegram.org", handler.RequestUri.Host);
        Assert.Equal($"/bot{ValidToken}/sendMessage", handler.RequestUri.AbsolutePath);
        Assert.DoesNotContain(ValidToken, handler.Body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal("@agentnotify_test", root.GetProperty("chat_id").GetString());
        Assert.Equal(17, root.GetProperty("message_thread_id").GetInt32());
        Assert.Equal("Hello <world>", root.GetProperty("text").GetString());
        Assert.True(root.GetProperty("protect_content").GetBoolean());
        Assert.True(root.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
        Assert.False(root.TryGetProperty("parse_mode", out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "telegram_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "telegram_503")]
    [InlineData(HttpStatusCode.Unauthorized, false, "telegram_401")]
    [InlineData(HttpStatusCode.Forbidden, false, "telegram_403")]
    [InlineData(HttpStatusCode.Redirect, false, "telegram_redirect")]
    public async Task SenderClassifiesTelegramStatusCodes(
        HttpStatusCode status,
        bool retryable,
        string errorCode)
    {
        var handler = new RecordingHttpHandler(new HttpResponseMessage(status));
        using var sender = new TelegramBotApiSender(new HttpClient(handler));

        var result = await sender.SendAsync(
            new TelegramSendRequest(ValidToken, "123456", null, "Hello", false, true),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal((int)status, result.StatusCode);
    }

    [Theory]
    [InlineData("{\"ok\":false}")]
    [InlineData("not-json")]
    public async Task SenderRetriesInvalidSuccessResponse(string content)
    {
        var handler = new RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        });
        using var sender = new TelegramBotApiSender(new HttpClient(handler));

        var result = await sender.SendAsync(
            new TelegramSendRequest(ValidToken, "123456", null, "Hello", false, true),
            CancellationToken.None);

        Assert.True(result.Retryable);
        Assert.Equal("telegram_invalid_response", result.ErrorCode);
    }

    private const string ValidToken = "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghi";

    private static OutboundDelivery MakeDelivery(
        string config = "{}",
        IReadOnlyDictionary<string, string>? secrets = null,
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler error\",\"priority\":\"critical\",\"type\":\"error\",\"agent\":\"codex\",\"project\":\"agent-notify\"}") =>
        new(
            "delivery-telegram-1",
            "notification-1",
            payload,
            new ProviderProfile
            {
                Id = "telegram-1",
                Name = "Telegram",
                Kind = "telegram",
                Enabled = true,
                ConfigJson = config
            },
            secrets ?? new Dictionary<string, string>
            {
                ["bot_token"] = ValidToken,
                ["chat_id"] = "-1001234567890"
            });

    private sealed class RecordingSender : ITelegramSender
    {
        private readonly DeliveryResult _result;

        public RecordingSender(DeliveryResult result) => _result = result;

        public List<TelegramSendRequest> Requests { get; } = [];

        public Task<DeliveryResult> SendAsync(
            TelegramSendRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHttpHandler(HttpResponseMessage response) => _response = response;

        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }
}
