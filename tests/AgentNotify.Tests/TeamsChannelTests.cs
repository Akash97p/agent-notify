using System.Net;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class TeamsChannelTests
{
    [Fact]
    public async Task PostsRouteFilteredAdaptiveCardToEncryptedWorkflowUrl()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.Accepted));
        using var adapter = new TeamsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("default.b5.environment.api.powerplatform.com", handler.Uri!.Host);
        Assert.DoesNotContain("secret-signature", handler.Body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        var card = root.GetProperty("attachments")[0].GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("1.2", card.GetProperty("version").GetString());
        Assert.Contains("Compiler", handler.Body, StringComparison.Ordinal);
        Assert.Equal("Build \\*unsafe\\*", card.GetProperty("body")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task OmitsRedactedMessage()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new TeamsChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery("{\"title\":\"Secret\",\"message\":null}"), CancellationToken.None);
        Assert.DoesNotContain("Compiler", handler.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://default.b5.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/1234567890123456/triggers/manual/paths/invoke?api-version=1&sp=x&sv=1&sig=x")]
    [InlineData("https://evil.example/powerautomate/automations/direct/workflows/1234567890123456/triggers/manual/paths/invoke?api-version=1&sp=x&sv=1&sig=x")]
    [InlineData("https://environment.api.powerplatform.com/powerautomate/automations/direct/workflows/1234567890123456/triggers/manual/paths/invoke?api-version=1&sp=x&sv=1&sig=x")]
    [InlineData("https://default.b5.environment.api.powerplatform.com/wrong/automations/direct/workflows/1234567890123456/triggers/manual/paths/invoke?api-version=1&sp=x&sv=1&sig=x")]
    [InlineData("https://default.b5.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/1234567890123456/triggers/manual/paths/invoke?api-version=1&sp=x&sv=1")]
    public async Task RejectsInvalidOrUnsignedWorkflowUrl(string url)
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new TeamsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(secrets: new Dictionary<string, string> { ["webhook_url"] = url }), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "teams_429")]
    [InlineData(HttpStatusCode.BadGateway, true, "teams_502")]
    [InlineData(HttpStatusCode.Forbidden, false, "teams_403")]
    [InlineData(HttpStatusCode.Redirect, false, "teams_redirect")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(new HttpResponseMessage(status));
        using var adapter = new TeamsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task RequiresEncryptedWorkflowUrl()
    {
        using var adapter = new TeamsChannelAdapter(new HttpClient(new Handler(new HttpResponseMessage(HttpStatusCode.OK))));
        var result = await adapter.DeliverAsync(MakeDelivery(secrets: new Dictionary<string, string>()), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task BoundsLongCardTextWithoutSplittingSurrogatePair()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.Accepted));
        using var adapter = new TeamsChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = "Build", message = new string('a', 6005) + "😀" });
        await adapter.DeliverAsync(MakeDelivery(payload), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var text = json.RootElement.GetProperty("attachments")[0].GetProperty("content").GetProperty("body")[1].GetProperty("text").GetString()!;
        Assert.True(text.Length <= 6000);
        Assert.False(char.IsHighSurrogate(text[^1]));
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetriesNetworkFailureWithStableCode()
    {
        using var adapter = new TeamsChannelAdapter(new HttpClient(new ThrowingHandler()));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Retryable);
        Assert.Equal("network_error", result.ErrorCode);
    }

    private const string Url = "https://default.b5.environment.api.powerplatform.com/powerautomate/automations/direct/workflows/12345678901234567890123456789012/triggers/manual/paths/invoke?api-version=1&sp=%2Ftriggers%2Fmanual%2Frun&sv=1.0&sig=secret-signature";
    private static OutboundDelivery MakeDelivery(string payload = "{\"title\":\"Build *unsafe*\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}", IReadOnlyDictionary<string, string>? secrets = null) =>
        new("teams-outbox", "notification", payload, new ProviderProfile { Id = "teams", Name = "Teams", Kind = "teams", Enabled = true }, secrets ?? new Dictionary<string, string> { ["webhook_url"] = Url });

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public Handler(HttpResponseMessage response) => _response = response;
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(token);
            return _response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("sensitive URL must not escape");
    }
}
