using System.Net;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class ZohoCliqChannelTests
{
    [Theory]
    [InlineData("cliq.zoho.com")]
    [InlineData("cliq.zoho.eu")]
    [InlineData("cliq.zoho.in")]
    [InlineData("cliq.zoho.com.au")]
    [InlineData("cliq.zoho.com.cn")]
    [InlineData("cliq.zoho.jp")]
    [InlineData("cliq.zoho.sa")]
    [InlineData("cliq.zoho.uk")]
    [InlineData("cliq.zohocloud.ca")]
    public async Task SupportsEachOfficialDataCenter(string host)
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new ZohoCliqChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(Url(host)), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(host, handler.Uri!.Host);
        Assert.DoesNotContain(Token, handler.Body, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Contains("[CRITICAL] Build", json.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscapesFormattingAndHonorsMessageRedaction()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.Created));
        using var adapter = new ZohoCliqChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(Url("cliq.zoho.com"), "{\"title\":\"Build *unsafe*\",\"message\":null}"), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var text = json.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("\\*unsafe\\*", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiler", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://cliq.zoho.com/api/v2/channelsbyname/alerts/message?zapikey=1234567890")]
    [InlineData("https://evil.example/api/v2/channelsbyname/alerts/message?zapikey=1234567890")]
    [InlineData("https://cliq.zoho.com:444/api/v2/channelsbyname/alerts/message?zapikey=1234567890")]
    [InlineData("https://cliq.zoho.com/api/v2/channelsbyname/alerts/message")]
    [InlineData("https://cliq.zoho.com/api/v2/channelsbyname/alerts/message?zapikey=1234567890&evil=x")]
    [InlineData("https://cliq.zoho.com/api/v2/unknown/alerts/message?zapikey=1234567890")]
    [InlineData("https://cliq.zoho.com/api/v2/channelsbyname/bad%2Fname/message?zapikey=1234567890")]
    public async Task RejectsUnsafeOrMalformedUrls(string url)
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new ZohoCliqChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(url), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData("channelsbyname")]
    [InlineData("channels")]
    [InlineData("bots")]
    public async Task SupportsDocumentedMessageDestinations(string resource)
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new ZohoCliqChannelAdapter(new HttpClient(handler));
        var url = $"https://cliq.zoho.com/api/v2/{resource}/alerts/message?zapikey={Token}";
        var result = await adapter.DeliverAsync(MakeDelivery(url), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "zoho_cliq_429")]
    [InlineData(HttpStatusCode.BadGateway, true, "zoho_cliq_502")]
    [InlineData(HttpStatusCode.Unauthorized, false, "zoho_cliq_401")]
    [InlineData(HttpStatusCode.Redirect, false, "zoho_cliq_redirect")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(new HttpResponseMessage(status));
        using var adapter = new ZohoCliqChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(Url("cliq.zoho.com")), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task BoundsTextWithoutSplittingSurrogatePair()
    {
        var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK));
        using var adapter = new ZohoCliqChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = "Build", message = new string('a', 5005) + "😀" });
        await adapter.DeliverAsync(MakeDelivery(Url("cliq.zoho.com"), payload), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var text = json.RootElement.GetProperty("text").GetString()!;
        Assert.True(text.Length <= 5000);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(text[^1]));
    }

    private const string Token = "1000.abcdefghijklmnopqrstuvwxyz0123456789";
    private static string Url(string host) => $"https://{host}/api/v2/channelsbyname/alerts/message?zapikey={Token}";
    private static OutboundDelivery MakeDelivery(string url, string payload = "{\"title\":\"Build\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}") =>
        new("cliq-outbox", "notification", payload, new ProviderProfile { Id = "cliq", Name = "Cliq", Kind = "zoho_cliq", Enabled = true }, new Dictionary<string, string> { ["webhook_url"] = url });

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
}
