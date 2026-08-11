using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class PushoverChannelTests
{
    private const string ApplicationToken = "azGDORePK8gMaC0QOYAMyEEuzJnyUi";
    private const string UserKey = "uQiRzpo4DXghDmr9QzzfQu27cmVRsG";
    private const string Success = "{\"status\":1,\"request\":\"647d2300-702c-4b38-8b2f-d56326ae460b\"}";

    [Fact]
    public async Task PostsUrlEncodedSecretsOnlyToExactPushoverEndpoint()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.pushover.net/1/messages.json", handler.Uri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.DoesNotContain(ApplicationToken, handler.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(UserKey, handler.Uri.AbsoluteUri, StringComparison.Ordinal);
        var form = ParseForm(handler.Body!);
        Assert.Equal(ApplicationToken, form["token"]);
        Assert.Equal(UserKey, form["user"]);
        Assert.Equal("Build failed", form["title"]);
        Assert.Equal("1", form["priority"]);
    }

    [Fact]
    public async Task PreservesRouteRedactionAndSendsPlainTextOnly()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));

        await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Build\",\"message\":null,\"agent\":\"codex\"}"),
            CancellationToken.None);

        var form = ParseForm(handler.Body!);
        Assert.DoesNotContain("Compiler failed", form["message"], StringComparison.Ordinal);
        Assert.Contains("Agent: codex", form["message"], StringComparison.Ordinal);
        Assert.DoesNotContain("html", form.Keys);
        Assert.DoesNotContain("url", form.Keys);
        Assert.DoesNotContain("callback", form.Keys);
    }

    [Theory]
    [InlineData("low", "-1")]
    [InlineData("normal", "0")]
    [InlineData("high", "1")]
    [InlineData("critical", "1")]
    [InlineData("unknown", "0")]
    public async Task MapsNonEmergencyPriorities(string priority, string expected)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));

        await adapter.DeliverAsync(
            MakeDelivery(payload: $"{{\"title\":\"Build\",\"priority\":\"{priority}\"}}"),
            CancellationToken.None);

        var form = ParseForm(handler.Body!);
        Assert.Equal(expected, form["priority"]);
        Assert.DoesNotContain("retry", form.Keys);
        Assert.DoesNotContain("expire", form.Keys);
    }

    [Fact]
    public async Task SendsExplicitCriticalEmergencyAndRequiresReceipt()
    {
        var response = "{\"status\":1,\"request\":\"request-id\",\"receipt\":\"receipt-id\"}";
        var handler = new Handler(HttpStatusCode.OK, response);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(criticalAsEmergency: true, retry: 45, expire: 1800)),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var form = ParseForm(handler.Body!);
        Assert.Equal("2", form["priority"]);
        Assert.Equal("45", form["retry"]);
        Assert.Equal("1800", form["expire"]);
    }

    [Fact]
    public async Task RetriesEmergencySuccessWithoutReceipt()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(criticalAsEmergency: true)),
            CancellationToken.None);

        Assert.True(result.Retryable);
        Assert.Equal("pushover_invalid_response", result.ErrorCode);
    }

    [Theory]
    [InlineData(29, 1800)]
    [InlineData(60, 0)]
    [InlineData(60, 10801)]
    public async Task RejectsInvalidEmergencyTiming(int retry, int expire)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(criticalAsEmergency: true, retry: retry, expire: expire)),
            CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData("short", UserKey)]
    [InlineData(ApplicationToken, "bad-key-with-punctuation!!!!!!!")]
    public async Task RejectsInvalidKeys(string applicationToken, string userKey)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(applicationToken: applicationToken, userKey: userKey),
            CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task IncludesEncryptedDeviceAndConfiguredCustomSound()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(sound: "my_custom-tone"), device: "work_phone-2"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var form = ParseForm(handler.Body!);
        Assert.Equal("work_phone-2", form["device"]);
        Assert.Equal("my_custom-tone", form["sound"]);
    }

    [Theory]
    [InlineData("device name")]
    [InlineData("this-device-name-is-far-too-long")]
    public async Task RejectsInvalidDevice(string device)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(device: device), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task RejectsUnsafeSoundName()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(sound: "sound name&priority=2")),
            CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false, "pushover_429")]
    [InlineData(HttpStatusCode.BadRequest, false, "pushover_400")]
    [InlineData(HttpStatusCode.Redirect, false, "pushover_redirect")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "pushover_503")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(status, "{}");
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task TreatsProviderRejectionAsPermanent()
    {
        var handler = new Handler(HttpStatusCode.OK, "{\"status\":0,\"errors\":[\"invalid\"],\"request\":\"id\"}");
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.False(result.Retryable);
        Assert.Equal("pushover_rejected", result.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":1}")]
    [InlineData("not-json")]
    public async Task RetriesMalformedSuccessResponse(string body)
    {
        var handler = new Handler(HttpStatusCode.OK, body);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Retryable);
        Assert.Equal("pushover_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task BoundsUnicodeMessageAndTitleByScalarCount()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new
        {
            title = string.Concat(Enumerable.Repeat("😀", 300)),
            message = string.Concat(Enumerable.Repeat("😀", 1100))
        });

        await adapter.DeliverAsync(MakeDelivery(payload: payload), CancellationToken.None);

        var form = ParseForm(handler.Body!);
        Assert.Equal(250, form["title"].EnumerateRunes().Count());
        Assert.Equal(1024, form["message"].EnumerateRunes().Count());
        Assert.EndsWith("…", form["title"], StringComparison.Ordinal);
        Assert.EndsWith("…", form["message"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundsResponseBody()
    {
        var handler = new Handler(HttpStatusCode.OK, new string('x', 17 * 1024));
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal("pushover_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task ConvertsNetworkFailureToRetryWithoutLeakingDetails()
    {
        var handler = new Handler(new HttpRequestException("secret-bearing failure"));
        using var adapter = new PushoverChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal("network_error", result.ErrorCode);
        Assert.True(result.Retryable);
    }

    private static string Config(
        bool criticalAsEmergency = false,
        int retry = 60,
        int expire = 3600,
        string sound = "") =>
        JsonSerializer.Serialize(new
        {
            applicationTokenSecretName = "application_token",
            userKeySecretName = "user_key",
            deviceSecretName = "device",
            sound,
            criticalAsEmergency,
            emergencyRetrySeconds = retry,
            emergencyExpireSeconds = expire
        });

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}",
        string? config = null,
        string applicationToken = ApplicationToken,
        string userKey = UserKey,
        string? device = null)
    {
        var secrets = new Dictionary<string, string>
        {
            ["application_token"] = applicationToken,
            ["user_key"] = userKey
        };
        if (device is not null) secrets["device"] = device;
        return new OutboundDelivery(
            "pushover-outbox",
            "notification",
            payload,
            new ProviderProfile
            {
                Id = "pushover",
                Name = "Pushover",
                Kind = "pushover",
                Enabled = true,
                ConfigJson = config ?? Config()
            },
            secrets);
    }

    private static Dictionary<string, string> ParseForm(string body) => body
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(
            pair => Decode(pair[0]),
            pair => Decode(pair.Length == 2 ? pair[1] : ""),
            StringComparer.Ordinal);

    private static string Decode(string value) =>
        Uri.UnescapeDataString(value.Replace('+', ' '));

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _response = "";
        private readonly Exception? _exception;

        public Handler(HttpStatusCode status, string response)
        {
            _status = status;
            _response = response;
        }

        public Handler(Exception exception) => _exception = exception;

        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_exception is not null) throw _exception;
            Uri = request.RequestUri;
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }
}
