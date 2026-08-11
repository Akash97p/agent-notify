using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class MatrixChannelTests
{
    [Fact]
    public async Task SendsIdempotentTextEventWithHeaderAuthentication()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"event_id\":\"$event:example.org\"}");
        using var adapter = new MatrixChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal("Bearer secret-access-token", handler.Authorization);
        Assert.DoesNotContain("secret-access-token", handler.Uri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("/_matrix/client/v3/rooms/%21room%3Aexample.org/send/m.room.message/", handler.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("m.text", json.RootElement.GetProperty("msgtype").GetString());
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("m.mentions").ValueKind);
    }

    [Fact]
    public async Task StableOutboxIdProducesStableTransactionId()
    {
        var first = new Handler(HttpStatusCode.OK, "{\"event_id\":\"$one\"}");
        var second = new Handler(HttpStatusCode.OK, "{\"event_id\":\"$two\"}");
        using var a = new MatrixChannelAdapter(new HttpClient(first));
        using var b = new MatrixChannelAdapter(new HttpClient(second));
        await a.DeliverAsync(MakeDelivery(), CancellationToken.None);
        await b.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(first.Uri!.AbsolutePath, second.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task NeutralizesLegacyMentionsAndHonorsRedaction()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"event_id\":\"$event\"}");
        using var adapter = new MatrixChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery("{\"title\":\"Alert @room\",\"message\":null,\"agent\":\"@alice:example.org\"}"), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement.GetProperty("body").GetString()!;
        Assert.DoesNotContain("@room", body, StringComparison.Ordinal);
        Assert.Contains("＠room", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Compiler failed", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://matrix.example", false)]
    [InlineData("https://user:pass@matrix.example", false)]
    [InlineData("https://matrix.example?x=1", false)]
    [InlineData("https://127.0.0.1", false)]
    [InlineData("https://[::1]", false)]
    [InlineData("https://169.254.169.254", true)]
    public async Task RejectsUnsafeHomeservers(string url, bool allowPrivate)
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"event_id\":\"$event\"}");
        using var adapter = new MatrixChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config(url, allowPrivate)), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task ExplicitConsentAllowsPrivateCustomPortAndSubpath()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"event_id\":\"$event\"}");
        using var adapter = new MatrixChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config("https://10.0.0.5:8448/matrix", true)), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(8448, handler.Uri!.Port);
        Assert.StartsWith("/matrix/_matrix/client/v3/", handler.Uri.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("room:example.org", "secret-access-token")]
    [InlineData("!room:example.org", "short")]
    [InlineData("!bad\nroom", "secret-access-token")]
    public async Task RejectsInvalidEncryptedDestinationOrToken(string room, string token)
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"event_id\":\"$event\"}");
        using var adapter = new MatrixChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(room: room, token: token), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "matrix_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "matrix_503")]
    [InlineData(HttpStatusCode.Forbidden, false, "matrix_403")]
    [InlineData(HttpStatusCode.Redirect, false, "matrix_redirect")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(status, "{}");
        using var adapter = new MatrixChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task RejectsMalformedOrOversizedSuccessResponse()
    {
        using var malformed = new MatrixChannelAdapter(new HttpClient(new Handler(HttpStatusCode.OK, "{}")));
        Assert.Equal("matrix_invalid_response", (await malformed.DeliverAsync(MakeDelivery(), CancellationToken.None)).ErrorCode);
        using var oversized = new MatrixChannelAdapter(new HttpClient(new Handler(HttpStatusCode.OK, new string('x', 9000))));
        Assert.Equal("matrix_invalid_response", (await oversized.DeliverAsync(MakeDelivery(), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task BoundsSerializedUtf8Payload()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"event_id\":\"$event\"}");
        using var adapter = new MatrixChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = "Build", message = new string('界', 40_000) + "😀" });
        await adapter.DeliverAsync(MakeDelivery(payload), CancellationToken.None);
        Assert.True(Encoding.UTF8.GetByteCount(handler.Body!) <= 48 * 1024);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.EndsWith("…", json.RootElement.GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    private static string Config(string url = "https://matrix.example", bool allowPrivate = false) =>
        JsonSerializer.Serialize(new { homeserverBaseUrl = url, allowPrivateNetwork = allowPrivate, accessTokenSecretName = "access_token", roomIdSecretName = "room_id" });
    private static OutboundDelivery MakeDelivery(string payload = "{\"title\":\"Build\",\"message\":\"Compiler failed\",\"priority\":\"critical\"}", string? config = null, string room = "!room:example.org", string token = "secret-access-token") =>
        new("matrix-outbox", "notification", payload, new ProviderProfile { Id = "matrix", Name = "Matrix", Kind = "matrix", Enabled = true, ConfigJson = config ?? Config() }, new Dictionary<string, string> { ["access_token"] = token, ["room_id"] = room });

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status; private readonly string _response;
        public Handler(HttpStatusCode status, string response) { _status = status; _response = response; }
        public Uri? Uri { get; private set; } public string? Body { get; private set; }
        public string? Authorization { get; private set; } public HttpMethod? Method { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Uri = request.RequestUri; Method = request.Method; Authorization = request.Headers.Authorization?.ToString(); Body = await request.Content!.ReadAsStringAsync(token); return new HttpResponseMessage(_status) { Content = new StringContent(_response) }; }
    }
}
