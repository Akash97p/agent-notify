using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class SlackChannelTests
{
    [Fact]
    public async Task PostsPlainMentionSafeMessageUsingEncryptedWebhook()
    {
        var handler = new RecordingHandler(OkResponse());
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));
        var delivery = MakeDelivery("{\"threadTimestamp\":\"1712345678.123456\"}");

        var result = await adapter.DeliverAsync(delivery, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("hooks.slack.com", handler.RequestUri!.Host);
        Assert.DoesNotContain(WebhookToken, handler.Body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.False(root.GetProperty("mrkdwn").GetBoolean());
        Assert.Equal(0, root.GetProperty("link_names").GetInt32());
        Assert.Equal("1712345678.123456", root.GetProperty("thread_ts").GetString());
        var text = root.GetProperty("text").GetString()!;
        Assert.Contains("[CRITICAL] Build failed", text, StringComparison.Ordinal);
        Assert.Contains("&lt;!everyone&gt;", text, StringComparison.Ordinal);
        Assert.Contains("&amp;", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupportsOfficialGovSlackWebhookHost()
    {
        var handler = new RecordingHandler(OkResponse());
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));
        var secrets = new Dictionary<string, string>
        {
            ["webhook_url"] = $"https://hooks.slack-gov.com/services/T12345678/B12345678/{WebhookToken}"
        };

        var result = await adapter.DeliverAsync(MakeDelivery(secrets: secrets), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("hooks.slack-gov.com", handler.RequestUri!.Host);
    }

    [Fact]
    public async Task HonorsRouteMessageRedaction()
    {
        var handler = new RecordingHandler(OkResponse());
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Secret build\",\"message\":null}"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("Compiler", handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://hooks.slack.com/services/T12345678/B12345678/ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("https://evil.example/services/T12345678/B12345678/ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("https://hooks.slack.com:444/services/T12345678/B12345678/ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("https://hooks.slack.com/services/T12345678/B12345678/ABCDEFGHIJKLMNOPQRSTUVWXYZ?x=1")]
    [InlineData("https://hooks.slack.com/services/T12345678/B12345678/ABCDEFGHIJKLMNOPQRSTUVWXYZ#fragment")]
    [InlineData("https://hooks.slack.com/not-services/T12345678/B12345678/ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("https://hooks.slack.com/services/T/B12345678/ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    [InlineData("https://hooks.slack.com/services/T12345678/B12345678/short")]
    public async Task RejectsNonOfficialOrMalformedWebhookUrls(string endpoint)
    {
        var handler = new RecordingHandler(OkResponse());
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));
        var secrets = new Dictionary<string, string> { ["webhook_url"] = endpoint };

        var result = await adapter.DeliverAsync(MakeDelivery(secrets: secrets), CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.RequestUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1712345678")]
    [InlineData("1712345678.123")]
    [InlineData("not-a-timestamp")]
    [InlineData("1712345678.123456.7")]
    public async Task RejectsInvalidThreadTimestamp(string threadTimestamp)
    {
        var handler = new RecordingHandler(OkResponse());
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(JsonSerializer.Serialize(new { threadTimestamp })),
            CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.RequestUri);
    }

    [Fact]
    public async Task TruncatesTextWithoutSplittingSurrogatePair()
    {
        var handler = new RecordingHandler(OkResponse());
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new
        {
            title = "Build",
            message = new string('a', 3990) + "😀"
        });

        await adapter.DeliverAsync(MakeDelivery(payload: payload), CancellationToken.None);

        using var document = JsonDocument.Parse(handler.Body!);
        var text = document.RootElement.GetProperty("text").GetString()!;
        Assert.True(text.Length <= 4000);
        Assert.False(char.IsHighSurrogate(text[^1]));
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "slack_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "slack_503")]
    [InlineData(HttpStatusCode.BadRequest, false, "slack_400")]
    [InlineData(HttpStatusCode.Forbidden, false, "slack_403")]
    [InlineData(HttpStatusCode.NotFound, false, "slack_404")]
    [InlineData(HttpStatusCode.Gone, false, "slack_410")]
    [InlineData(HttpStatusCode.Redirect, false, "slack_redirect")]
    public async Task ClassifiesSlackStatusCodes(HttpStatusCode status, bool retryable, string errorCode)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(status));
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal((int)status, result.StatusCode);
    }

    [Theory]
    [InlineData("not-ok")]
    [InlineData("")]
    public async Task RetriesUnexpectedSuccessBody(string responseBody)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody)
        });
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Retryable);
        Assert.Equal("slack_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task RejectsOversizedSuccessBodyWithoutPersistingIt()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 9000))
        });
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Retryable);
        Assert.Equal("slack_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task RetriesNetworkFailureWithStableErrorCode()
    {
        var handler = new RecordingHandler(new HttpRequestException("contains-secret"));
        using var adapter = new SlackChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Retryable);
        Assert.Equal("network_error", result.ErrorCode);
    }

    private const string WebhookToken = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuv";
    private const string WebhookUrl = "https://hooks.slack.com/services/T12345678/B12345678/" + WebhookToken;

    private static HttpResponseMessage OkResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("ok", Encoding.UTF8, "text/plain")
    };

    private static OutboundDelivery MakeDelivery(
        string config = "{}",
        IReadOnlyDictionary<string, string>? secrets = null,
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler <!everyone> & <@U123>\",\"priority\":\"critical\",\"type\":\"error\",\"agent\":\"codex\",\"project\":\"agent-notify\"}") =>
        new(
            "delivery-slack-1",
            "notification-1",
            payload,
            new ProviderProfile
            {
                Id = "slack-1",
                Name = "Slack",
                Kind = "slack",
                Enabled = true,
                ConfigJson = config
            },
            secrets ?? new Dictionary<string, string> { ["webhook_url"] = WebhookUrl });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _exception;

        public RecordingHandler(HttpResponseMessage response) => _response = response;
        public RecordingHandler(Exception exception) => _exception = exception;

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
            if (_exception is not null)
                throw _exception;
            return _response!;
        }
    }
}
