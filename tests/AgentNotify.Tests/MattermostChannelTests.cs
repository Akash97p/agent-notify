using System.Net;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class MattermostChannelTests
{
    [Fact]
    public async Task PostsToSelfHostedWebhookAndVerifiesAcknowledgement()
    {
        var handler = new Handler(HttpStatusCode.OK, "ok");
        using var adapter = new MattermostChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("mattermost.example", handler.Uri!.Host);
        Assert.Equal("/mattermost/hooks/abcdefghijklmnopqrstuvwxyz", handler.Uri.AbsolutePath);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", handler.Body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Contains("[CRITICAL] Build", json.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.False(json.RootElement.GetProperty("silent").GetBoolean());
    }

    [Fact]
    public async Task SupportsSilentDelivery()
    {
        var handler = new Handler(HttpStatusCode.NoContent, "");
        using var adapter = new MattermostChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config(silent: true)), CancellationToken.None);
        Assert.True(result.Succeeded);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.True(json.RootElement.GetProperty("silent").GetBoolean());
    }

    [Fact]
    public async Task NeutralizesMentionsSlackControlsAndMarkdown()
    {
        var handler = new Handler(HttpStatusCode.OK, "ok");
        using var adapter = new MattermostChannelAdapter(new HttpClient(handler));
        var payload = "{\"title\":\"Alert @channel <!here> *now*\",\"message\":null,\"agent\":\"@alice\"}";
        await adapter.DeliverAsync(MakeDelivery(payload), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var text = json.RootElement.GetProperty("text").GetString()!;
        Assert.DoesNotContain("@channel", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<!here>", text, StringComparison.Ordinal);
        Assert.Contains("＠channel", text, StringComparison.Ordinal);
        Assert.Contains("\\*now\\*", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiler failed", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://mattermost.example/hooks/abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("https://user:pass@mattermost.example/hooks/abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("https://mattermost.example/hooks/abcdefghijklmnopqrstuvwxyz?x=1", false)]
    [InlineData("https://mattermost.example/not-hooks/abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("https://mattermost.example/hooks/short", false)]
    [InlineData("https://mattermost.example/hooks/bad%2Ftoken", false)]
    [InlineData("https://127.0.0.1/hooks/abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("https://[::1]/hooks/abcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("https://169.254.169.254/hooks/abcdefghijklmnopqrstuvwxyz", true)]
    public async Task RejectsUnsafeOrMalformedUrls(string url, bool allowPrivate)
    {
        var handler = new Handler(HttpStatusCode.OK, "ok");
        using var adapter = new MattermostChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(url: url, config: Config(allowPrivate)),
            CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task ExplicitConsentAllowsPrivateHttpsServer()
    {
        var handler = new Handler(HttpStatusCode.OK, "ok");
        using var adapter = new MattermostChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(
                url: "https://10.0.0.4:8443/hooks/abcdefghijklmnopqrstuvwxyz",
                config: Config(allowPrivate: true)),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(8443, handler.Uri!.Port);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "mattermost_429")]
    [InlineData(HttpStatusCode.BadGateway, true, "mattermost_502")]
    [InlineData(HttpStatusCode.Unauthorized, false, "mattermost_401")]
    [InlineData(HttpStatusCode.Redirect, false, "mattermost_redirect")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(status, "ignored");
        using var adapter = new MattermostChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData(200, "not-ok")]
    [InlineData(201, "")]
    public async Task RetriesMalformedSuccessAcknowledgements(int status, string body)
    {
        var handler = new Handler((HttpStatusCode)status, body);
        using var adapter = new MattermostChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Retryable);
        Assert.Equal("mattermost_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task RejectsOversizedAcknowledgement()
    {
        var handler = new Handler(HttpStatusCode.OK, new string('x', 9000));
        using var adapter = new MattermostChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal("mattermost_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task BoundsTextWithoutSplittingSurrogatePair()
    {
        var handler = new Handler(HttpStatusCode.OK, "ok");
        using var adapter = new MattermostChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = "Build", message = new string('a', 16_500) + "😀" });
        await adapter.DeliverAsync(MakeDelivery(payload), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var text = json.RootElement.GetProperty("text").GetString()!;
        Assert.True(text.Length <= 16_383);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(text[^2]));
    }

    [Fact]
    public async Task MapsNetworkFailureToSanitizedRetry()
    {
        using var adapter = new MattermostChannelAdapter(new HttpClient(new ThrowingHandler()));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Retryable);
        Assert.Equal("network_error", result.ErrorCode);
    }

    private static string Config(bool allowPrivate = false, bool silent = false) =>
        JsonSerializer.Serialize(new { webhookUrlSecretName = "webhook_url", allowPrivateNetwork = allowPrivate, silent });

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}",
        string url = "https://mattermost.example/mattermost/hooks/abcdefghijklmnopqrstuvwxyz",
        string? config = null) =>
        new(
            "mattermost-outbox",
            "notification",
            payload,
            new ProviderProfile { Id = "mattermost", Name = "Mattermost", Kind = "mattermost", Enabled = true, ConfigJson = config ?? Config() },
            new Dictionary<string, string> { ["webhook_url"] = url });

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _response;
        public Handler(HttpStatusCode status, string response) { _status = status; _response = response; }
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(_status) { Content = new StringContent(_response) };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("secret endpoint detail");
    }
}
