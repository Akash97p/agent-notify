using System.Net;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class GotifyChannelTests
{
    private const string Token = "AgarXhfGWhnzOae";

    [Fact]
    public async Task SendsPlainJsonUsingApplicationHeader()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":42,\"message\":\"ok\"}");
        using var adapter = new GotifyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("https://push.example/gotify/message", handler.Uri!.AbsoluteUri);
        Assert.Equal(Token, handler.Key);
        Assert.DoesNotContain(Token, handler.Uri.AbsoluteUri, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(10, json.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal("text/plain", json.RootElement.GetProperty("extras").GetProperty("client::display").GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task HonorsRouteRedactionAndDoesNotAddActions()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":1}");
        using var adapter = new GotifyChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery("{\"title\":\"Build\",\"message\":null}"), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.DoesNotContain("Compiler failed", json.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(json.RootElement.GetProperty("extras").TryGetProperty("client::notification", out _));
    }

    [Theory]
    [InlineData("low", 2)]
    [InlineData("normal", 5)]
    [InlineData("high", 7)]
    [InlineData("critical", 10)]
    public async Task MapsPriority(string priority, int expected)
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":1}");
        using var adapter = new GotifyChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery($"{{\"title\":\"Build\",\"priority\":\"{priority}\"}}"), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expected, json.RootElement.GetProperty("priority").GetInt32());
    }

    [Theory]
    [InlineData("http://push.example", false)]
    [InlineData("https://user:pass@push.example", false)]
    [InlineData("https://push.example?x=1", false)]
    [InlineData("https://push.example/message", false)]
    [InlineData("https://127.0.0.1", false)]
    [InlineData("https://[::1]", false)]
    [InlineData("https://169.254.169.254", true)]
    public async Task RejectsUnsafeServers(string server, bool allowPrivate)
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":1}");
        using var adapter = new GotifyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config(server, allowPrivate)), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task AllowsExplicitPrivateCustomPortAndSubpath()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":1}");
        using var adapter = new GotifyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config("https://10.0.0.9:8443/push", true)), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("https://10.0.0.9:8443/push/message", handler.Uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("bad:token-value")]
    public async Task RejectsInvalidApplicationToken(string token)
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":1}");
        using var adapter = new GotifyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(token: token), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "gotify_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "gotify_503")]
    [InlineData(HttpStatusCode.Unauthorized, false, "gotify_401")]
    [InlineData(HttpStatusCode.Redirect, false, "gotify_redirect")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(status, "{}");
        using var adapter = new GotifyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":0}")]
    public async Task RetriesMalformedSuccessResponse(string body)
    {
        var handler = new Handler(HttpStatusCode.OK, body);
        using var adapter = new GotifyChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal("gotify_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task BoundsTextWithoutSplittingSurrogatePair()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"id\":1}");
        using var adapter = new GotifyChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = "Build", message = new string('a', 16_500) + "😀" });
        await adapter.DeliverAsync(MakeDelivery(payload), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var message = json.RootElement.GetProperty("message").GetString()!;
        Assert.True(message.Length <= 16_384);
        Assert.EndsWith("…", message, StringComparison.Ordinal);
    }

    private static string Config(string server = "https://push.example/gotify", bool allowPrivate = false) =>
        JsonSerializer.Serialize(new { serverBaseUrl = server, allowPrivateNetwork = allowPrivate, applicationTokenSecretName = "application_token" });
    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}",
        string? config = null,
        string token = Token) =>
        new("gotify-outbox", "notification", payload,
            new ProviderProfile { Id = "gotify", Name = "Gotify", Kind = "gotify", Enabled = true, ConfigJson = config ?? Config() },
            new Dictionary<string, string> { ["application_token"] = token });

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status; private readonly string _response;
        public Handler(HttpStatusCode status, string response) { _status = status; _response = response; }
        public Uri? Uri { get; private set; } public string? Body { get; private set; } public string? Key { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri = request.RequestUri;
            Key = request.Headers.TryGetValues("X-Gotify-Key", out var values) ? values.Single() : null;
            Body = await request.Content!.ReadAsStringAsync(token);
            return new HttpResponseMessage(_status) { Content = new StringContent(_response) };
        }
    }
}
