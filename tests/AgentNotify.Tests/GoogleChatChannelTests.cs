using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class GoogleChatChannelTests
{
    [Fact]
    public async Task PostsBoundedTextToOfficialWebhookWithoutLeakingCredentials()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("chat.googleapis.com", handler.Uri!.Host);
        Assert.Equal("/v1/spaces/AAAA-example/messages", handler.Uri.AbsolutePath);
        Assert.Equal("AgentNotify/1.0", handler.UserAgent);
        Assert.DoesNotContain(Token, handler.Body, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(handler.Body!) <= 31_500);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Contains("[CRITICAL] Build", json.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.False(json.RootElement.TryGetProperty("thread", out _));
    }

    [Fact]
    public async Task AddsDocumentedThreadKeyAndFallbackPolicy()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(config: "{\"webhookUrlSecretName\":\"webhook_url\",\"threadKey\":\"agent-builds\",\"threadReplyPolicy\":\"fallback\"}"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("messageReplyOption=REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD", handler.Uri!.Query, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("agent-builds", json.RootElement.GetProperty("thread").GetProperty("threadKey").GetString());
    }

    [Fact]
    public async Task SupportsFailWhenThreadIsMissingPolicy()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(
            MakeDelivery(config: "{\"webhookUrlSecretName\":\"webhook_url\",\"threadKey\":\"known-thread\",\"threadReplyPolicy\":\"fail\"}"),
            CancellationToken.None);
        Assert.Contains("messageReplyOption=REPLY_MESSAGE_OR_FAIL", handler.Uri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeutralizesMentionsMarkupAndHonorsMessageRedaction()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));
        var payload = "{\"title\":\"Alert <users/all> *now*\",\"message\":null,\"agent\":\"<users/123>\"}";

        await adapter.DeliverAsync(MakeDelivery(payload), CancellationToken.None);

        using var json = JsonDocument.Parse(handler.Body!);
        var text = json.RootElement.GetProperty("text").GetString()!;
        Assert.DoesNotContain("<users/", text, StringComparison.Ordinal);
        Assert.Contains("‹users/all›", text, StringComparison.Ordinal);
        Assert.Contains("\\*now\\*", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiler failed", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://chat.googleapis.com/v1/spaces/AAAA-example/messages?key=1234567890&token=abcdefghij")]
    [InlineData("https://evil.example/v1/spaces/AAAA-example/messages?key=1234567890&token=abcdefghij")]
    [InlineData("https://chat.googleapis.com:444/v1/spaces/AAAA-example/messages?key=1234567890&token=abcdefghij")]
    [InlineData("https://chat.googleapis.com/v1/spaces/AAAA-example/messages?key=1234567890")]
    [InlineData("https://chat.googleapis.com/v1/spaces/AAAA-example/messages?key=1234567890&token=abcdefghij&evil=x")]
    [InlineData("https://chat.googleapis.com/v1/spaces/AAAA-example/messages/extra?key=1234567890&token=abcdefghij")]
    [InlineData("https://chat.googleapis.com/v1/spaces/bad%2Fspace/messages?key=1234567890&token=abcdefghij")]
    [InlineData("https://chat.googleapis.com/v2/spaces/AAAA-example/messages?key=1234567890&token=abcdefghij")]
    public async Task RejectsUnsafeOrMalformedWebhookUrls(string url)
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(url: url), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true, "google_chat_408")]
    [InlineData(HttpStatusCode.TooManyRequests, true, "google_chat_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "google_chat_503")]
    [InlineData(HttpStatusCode.Unauthorized, false, "google_chat_401")]
    [InlineData(HttpStatusCode.Redirect, false, "google_chat_redirect")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(new HttpResponseMessage(status));
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("{\"webhookUrlSecretName\":\"webhook_url\",\"threadKey\":\"thread\",\"threadReplyPolicy\":\"unknown\"}")]
    [InlineData("{\"webhookUrlSecretName\":\"webhook_url\",\"threadReplyPolicy\":\"fail\"}")]
    [InlineData("{\"webhookUrlSecretName\":\"webhook_url\",\"threadKey\":\"bad\\nthread\"}")]
    public async Task RejectsInvalidThreadConfiguration(string config)
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: config), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task BoundsSerializedUtf8PayloadWithoutSplittingSurrogatePair()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = "Build", message = new string('界', 40_000) + "😀" });

        await adapter.DeliverAsync(MakeDelivery(payload), CancellationToken.None);

        Assert.True(Encoding.UTF8.GetByteCount(handler.Body!) <= 31_500);
        using var json = JsonDocument.Parse(handler.Body!);
        var text = json.RootElement.GetProperty("text").GetString()!;
        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(text[^2]));
    }

    [Fact]
    public async Task MapsNetworkFailuresToSanitizedRetry()
    {
        var handler = new ThrowingHandler();
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Retryable);
        Assert.Equal("network_error", result.ErrorCode);
    }

    [Fact]
    public async Task EnforcesDocumentedWriteSpacingAndHonorsCancellation()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new GoogleChatChannelAdapter(new HttpClient(handler));
        Assert.True((await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None)).Succeeded);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.DeliverAsync(MakeDelivery(), cancellation.Token));
        Assert.Equal(1, handler.RequestCount);
    }

    private const string Key = "AIza-example-key-1234567890";
    private const string Token = "example-token-abcdefghijklmnopqrstuvwxyz";
    private static string Url => $"https://chat.googleapis.com/v1/spaces/AAAA-example/messages?key={Key}&token={Token}";

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}",
        string? url = null,
        string config = "{\"webhookUrlSecretName\":\"webhook_url\",\"threadReplyPolicy\":\"fallback\"}") =>
        new(
            "google-chat-outbox",
            "notification",
            payload,
            new ProviderProfile { Id = "google-chat", Name = "Google Chat", Kind = "google_chat", Enabled = true, ConfigJson = config },
            new Dictionary<string, string> { ["webhook_url"] = url ?? Url });

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public Handler(HttpResponseMessage response) => _response = response;
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? UserAgent { get; private set; }
        public int RequestCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestCount++;
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(token);
            UserAgent = request.Headers.UserAgent.ToString();
            return _response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("secret provider detail");
    }
}
