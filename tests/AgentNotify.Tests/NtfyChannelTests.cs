using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class NtfyChannelTests
{
    private const string Token = "tk_12345678901234567890123456789";

    [Fact]
    public async Task PublishesJsonWithoutPuttingTopicOrTokenInUrl()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":\"abc123\",\"event\":\"message\"}");
        using var adapter = new NtfyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("https://ntfy.sh/", handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer " + Token, handler.Authorization);
        Assert.DoesNotContain("private-agent-topic", handler.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, handler.Uri.AbsoluteUri, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("private-agent-topic", json.RootElement.GetProperty("topic").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("priority").GetInt32());
        Assert.False(json.RootElement.GetProperty("markdown").GetBoolean());
    }

    [Fact]
    public async Task AllowsUnauthenticatedOnlyWithExplicitConsent()
    {
        var accepted = new Handler(HttpStatusCode.OK, "{\"id\":\"abc123\"}");
        using var adapter = new NtfyChannelAdapter(new HttpClient(accepted));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(allowAnonymous: true), includeToken: false),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Null(accepted.Authorization);

        var rejected = new Handler(HttpStatusCode.OK, "{\"id\":\"abc123\"}");
        using var second = new NtfyChannelAdapter(new HttpClient(rejected));
        result = await second.DeliverAsync(
            MakeDelivery(config: Config(), includeToken: false),
            CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(rejected.Uri);
    }

    [Fact]
    public async Task UsesStableSequenceIdAndHonorsMessageRedaction()
    {
        var first = new Handler(HttpStatusCode.OK, "{\"id\":\"one\"}");
        var second = new Handler(HttpStatusCode.OK, "{\"id\":\"two\"}");
        using var a = new NtfyChannelAdapter(new HttpClient(first));
        using var b = new NtfyChannelAdapter(new HttpClient(second));
        const string payload = "{\"title\":\"Build\",\"message\":null,\"priority\":\"high\"}";
        await a.DeliverAsync(MakeDelivery(payload), CancellationToken.None);
        await b.DeliverAsync(MakeDelivery(payload), CancellationToken.None);
        using var one = JsonDocument.Parse(first.Body!);
        using var two = JsonDocument.Parse(second.Body!);
        Assert.Equal(one.RootElement.GetProperty("sequence_id").GetString(), two.RootElement.GetProperty("sequence_id").GetString());
        Assert.DoesNotContain("Compiler failed", first.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("low", 2)]
    [InlineData("normal", 3)]
    [InlineData("high", 4)]
    [InlineData("critical", 5)]
    public async Task MapsPriority(string priority, int expected)
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":\"abc123\"}");
        using var adapter = new NtfyChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery($"{{\"title\":\"Build\",\"priority\":\"{priority}\"}}"), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expected, json.RootElement.GetProperty("priority").GetInt32());
    }

    [Theory]
    [InlineData("http://ntfy.sh", false)]
    [InlineData("https://user:pass@ntfy.sh", false)]
    [InlineData("https://ntfy.sh?x=1", false)]
    [InlineData("https://127.0.0.1", false)]
    [InlineData("https://[::1]", false)]
    [InlineData("https://169.254.169.254", true)]
    public async Task RejectsUnsafeServers(string url, bool allowPrivate)
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":\"abc123\"}");
        using var adapter = new NtfyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config(url, allowPrivate)), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task AllowsExplicitPrivateServerWithSubpathAndCustomPort()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":\"abc123\"}");
        using var adapter = new NtfyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config("https://10.0.0.8:8443/ntfy", true)),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("https://10.0.0.8:8443/ntfy/", handler.Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("bad/topic", Token)]
    [InlineData("topic", "bad-token")]
    public async Task RejectsInvalidEncryptedTopicOrToken(string topic, string token)
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":\"abc123\"}");
        using var adapter = new NtfyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(topic: topic, token: token), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "ntfy_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "ntfy_503")]
    [InlineData(HttpStatusCode.Forbidden, false, "ntfy_403")]
    [InlineData(HttpStatusCode.Redirect, false, "ntfy_redirect")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(status, "{}");
        using var adapter = new NtfyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"abc\",\"event\":\"open\"}")]
    public async Task RetriesMalformedSuccessResponse(string response)
    {
        var handler = new Handler(HttpStatusCode.OK, response);
        using var adapter = new NtfyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal("ntfy_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task BoundsMessageTo4096Utf8BytesWithoutSplittingSurrogatePair()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":\"abc123\"}");
        using var adapter = new NtfyChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = "Build", message = new string('界', 5000) + "😀" });
        await adapter.DeliverAsync(MakeDelivery(payload), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var message = json.RootElement.GetProperty("message").GetString()!;
        Assert.True(Encoding.UTF8.GetByteCount(message) <= 4096);
        Assert.EndsWith("…", message, StringComparison.Ordinal);
    }

    private static string Config(
        string server = "https://ntfy.sh",
        bool allowPrivate = false,
        bool allowAnonymous = false) =>
        JsonSerializer.Serialize(new
        {
            serverBaseUrl = server,
            allowPrivateNetwork = allowPrivate,
            allowUnauthenticatedTopic = allowAnonymous,
            topicSecretName = "topic",
            accessTokenSecretName = "access_token"
        });

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}",
        string? config = null,
        string topic = "private-agent-topic",
        string token = Token,
        bool includeToken = true)
    {
        var secrets = new Dictionary<string, string> { ["topic"] = topic };
        if (includeToken) secrets["access_token"] = token;
        return new OutboundDelivery(
            "ntfy-outbox",
            "notification",
            payload,
            new ProviderProfile { Id = "ntfy", Name = "ntfy", Kind = "ntfy", Enabled = true, ConfigJson = config ?? Config() },
            secrets);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _response;
        public Handler(HttpStatusCode status, string response) { _status = status; _response = response; }
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(_status) { Content = new StringContent(_response) };
        }
    }
}
