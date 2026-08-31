using System.Net;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class RelayChannelTests
{
    private const string ValidToken = "inst_MivWSeeSbQhV1dS2mce82UQUmXoXg9oMWRRoIWN0nvI";
    private const string DefaultDevicesJson = "{\"devices\":[{\"device_id\":\"dev123\",\"key_id\":\"k1\",\"public_key\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"revoked_at\":null}]}";

    [Fact]
    public async Task SendsEnvelopeWithIdempotencyAndAuth()
    {
        var handler = new RelayHandler(HttpStatusCode.Created, "{\"envelope_id\":\"abc123\",\"status\":\"accepted\"}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("https://relay.example.com/v1/envelopes", handler.PostUri!.AbsoluteUri);
        Assert.Equal("Bearer " + ValidToken, handler.PostAuthorization);
        Assert.Equal("relay-outbox", handler.PostIdempotencyKey);
        Assert.DoesNotContain(ValidToken, handler.PostUri.AbsoluteUri, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(handler.PostBody!);
        Assert.Equal("1", json.RootElement.GetProperty("envelope_version").GetString());
        Assert.Equal("notification", json.RootElement.GetProperty("client_event_id").GetString());
        Assert.True(json.RootElement.TryGetProperty("recipients", out var recipients) && recipients.GetArrayLength() >= 1);
        var first = recipients[0];
        Assert.True(first.TryGetProperty("ciphertext", out var ct) && ct.GetString()!.Length > 10);
        Assert.True(first.TryGetProperty("device_id", out _));
        Assert.True(first.TryGetProperty("key_id", out _));
    }

    [Fact]
    public async Task UsesSenderNameFromConfig()
    {
        var handler = new RelayHandler(HttpStatusCode.Created, "{\"envelope_id\":\"id1\",\"status\":\"accepted\"}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(config: Config("https://relay.example.com", senderName: "My ThinkPad")), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.PostBody!);
        Assert.Equal("My ThinkPad", json.RootElement.GetProperty("sender_name").GetString());
    }

    [Fact]
    public async Task HonorsDeviceFetchWhenAvailable()
    {
        var handler = new RelayHandler(HttpStatusCode.Created, "{\"envelope_id\":\"id1\",\"status\":\"accepted\"}", devicesJson: "{\"devices\":[{\"device_id\":\"dev123\",\"key_id\":\"k1\",\"public_key\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}]}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.PostBody!);
        var recipients = json.RootElement.GetProperty("recipients");
        Assert.Equal("dev123", recipients[0].GetProperty("device_id").GetString());
        Assert.Equal("k1", recipients[0].GetProperty("key_id").GetString());
    }

    [Fact]
    public async Task EmptyDeviceListFailsWithoutPostingPlaceholder()
    {
        var handler = new RelayHandler(
            HttpStatusCode.Created,
            "{\"envelope_id\":\"id1\",\"status\":\"accepted\"}",
            devicesJson: "{\"devices\":[],\"total\":0}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal("no_devices_paired", result.ErrorCode);
        Assert.Null(handler.PostUri);
        Assert.Null(handler.PostBody);
    }

    [Fact]
    public async Task UnknownPinnedDeviceFailsWithoutPosting()
    {
        var handler = new RelayHandler(HttpStatusCode.Created, "{}", devicesJson: DefaultDevicesJson);
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var config = JsonSerializer.Serialize(new
        {
            deployment = "custom",
            relay_url = "https://relay.example.com",
            device_id = "missing-device"
        });

        var result = await adapter.DeliverAsync(MakeDelivery(config: config), CancellationToken.None);

        Assert.Equal("relay_device_not_found", result.ErrorCode);
        Assert.False(result.Retryable);
        Assert.Null(handler.PostUri);
    }

    [Fact]
    public void ParsesPairedInstallationIdentity()
    {
        var config = JsonSerializer.Serialize(new
        {
            deployment = "custom",
            relay_url = "https://relay.example.com",
            installation_id = "installation-1"
        });

        var parsed = RelayChannelAdapter.ParseAndValidateConfiguration(
            config,
            new Dictionary<string, string> { ["installation_token"] = ValidToken });

        Assert.Equal("installation-1", parsed.InstallationId);
    }

    [Theory]
    [InlineData("http://relay.example.com", false)]
    [InlineData("https://user:pass@relay.example.com", false)]
    [InlineData("https://relay.example.com?x=1", false)]
    [InlineData("https://relay.example.com#frag", false)]
    [InlineData("https://127.0.0.1", false)]
    [InlineData("https://[::1]", false)]
    [InlineData("https://169.254.169.254", true)]
    [InlineData("http://localhost:4000", false)] // http localhost requires allowPrivate? now rejected if false
    public async Task RejectsUnsafeServers(string url, bool allowPrivate)
    {
        var handler = new RelayHandler(HttpStatusCode.Created, "{\"envelope_id\":\"id\"}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config(url, allowPrivate)), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.PostUri);
    }

    [Fact]
    public async Task AllowsExplicitPrivateServerWithSubpathAndCustomPort()
    {
        var handler = new RelayHandler(HttpStatusCode.Created, "{\"envelope_id\":\"id1\",\"status\":\"accepted\"}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config("https://10.0.0.8:8443/relay", true)), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("https://10.0.0.8:8443/relay/v1/envelopes", handler.PostUri!.AbsoluteUri);
    }

    [Fact]
    public async Task AllowsHttpLocalhostWithPrivateConsent()
    {
        var handler = new RelayHandler(HttpStatusCode.Created, "{\"envelope_id\":\"id1\",\"status\":\"accepted\"}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config("http://localhost:4000", true)), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal("http://localhost:4000/v1/envelopes", handler.PostUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("bad-token")]
    [InlineData("dev_abc")]
    [InlineData("")]
    public async Task RejectsInvalidInstallationToken(string token)
    {
        var handler = new RelayHandler(HttpStatusCode.Created, "{\"envelope_id\":\"id\"}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(token: token), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.PostUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "relay_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "relay_503")]
    [InlineData(HttpStatusCode.RequestTimeout, true, "relay_408")]
    [InlineData(HttpStatusCode.Forbidden, false, "relay_403")]
    [InlineData(HttpStatusCode.Unauthorized, false, "relay_401")]
    [InlineData(HttpStatusCode.Redirect, false, "relay_redirect")]
    public async Task ClassifiesStatuses(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new RelayHandler(status, "{}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"unknown\"}")]
    public async Task RetriesMalformedSuccessResponse(string body)
    {
        var handler = new RelayHandler(HttpStatusCode.OK, body);
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal("relay_invalid_response", result.ErrorCode);
    }

    [Fact]
    public async Task ReturnsDuplicateAsSuccess()
    {
        var handler = new RelayHandler(HttpStatusCode.OK, "{\"envelope_id\":\"dup\",\"status\":\"duplicate\"}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RejectsRelayGoDeploymentUntilReady()
    {
        var handler = new RelayHandler(HttpStatusCode.Created, "{\"envelope_id\":\"id\"}");
        using var adapter = new RelayChannelAdapter(new HttpClient(handler));
        var config = JsonSerializer.Serialize(new { deployment = "relay_go", relay_url = "https://relay.example.com", sender_name = "Test" });
        var result = await adapter.DeliverAsync(MakeDelivery(config: config), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
    }

    private static string Config(string relayUrl = "https://relay.example.com", bool allowPrivate = false, string? senderName = null, string deployment = "custom") =>
        JsonSerializer.Serialize(new
        {
            deployment,
            relay_url = relayUrl,
            sender_name = senderName,
            allowPrivateNetwork = allowPrivate
        });

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}",
        string? config = null,
        string token = ValidToken)
    {
        return new OutboundDelivery(
            "relay-outbox",
            "notification",
            payload,
            new ProviderProfile { Id = "relay", Name = "Relay", Kind = "relay", Enabled = true, ConfigJson = config ?? Config() },
            new Dictionary<string, string> { ["installation_token"] = token });
    }

    private sealed class RelayHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _response;
        private readonly string? _devicesJson;

        public RelayHandler(HttpStatusCode status, string response, string? devicesJson = null)
        {
            _status = status;
            _response = response;
            _devicesJson = devicesJson;
        }

        public Uri? PostUri { get; private set; }
        public string? PostBody { get; private set; }
        public string? PostAuthorization { get; private set; }
        public string? PostIdempotencyKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Simulate GET /v1/devices
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/v1/devices"))
            {
                var json = _devicesJson ?? DefaultDevicesJson;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }

            // POST /v1/envelopes
            if (request.Method == HttpMethod.Post)
            {
                PostUri = request.RequestUri;
                PostAuthorization = request.Headers.Authorization?.ToString();
                if (request.Headers.TryGetValues("Idempotency-Key", out var vals))
                    PostIdempotencyKey = vals.SingleOrDefault();
                PostBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(_status) { Content = new StringContent(_response) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }
    }
}
