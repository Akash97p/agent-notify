using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class WebhookChannelTests
{
    [Fact]
    public async Task SendsTemplateSecretHeadersSignatureAndIdempotencyKey()
    {
        var handler = new CapturingHandler(HttpStatusCode.Accepted);
        using var adapter = new WebhookChannelAdapter(new HttpClient(handler));
        var delivery = MakeDelivery(
            """
            {
              "headers":{"X-Source":"agentnotify"},
              "secretHeaders":{"Authorization":"authorization"},
              "signature":{"secretName":"hmac_secret"},
              "bodyTemplate":{
                "event":"notification",
                "deliveryId":"{{outbox_id}}",
                "notification":"{{payload}}"
              }
            }
            """,
            new Dictionary<string, string>
            {
                ["authorization"] = "Bearer encrypted-at-rest",
                ["hmac_secret"] = "signing-key",
                ["endpoint_url"] = "https://hooks.example.test/events"
            });

        var result = await adapter.DeliverAsync(delivery, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(202, result.StatusCode);
        Assert.Equal("Bearer encrypted-at-rest", handler.Headers["Authorization"]);
        Assert.Equal("delivery-1", handler.Headers["Idempotency-Key"]);
        Assert.Equal("agentnotify", handler.Headers["X-Source"]);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("delivery-1", body.RootElement.GetProperty("deliveryId").GetString());
        Assert.Equal("Build failed", body.RootElement.GetProperty("notification").GetProperty("title").GetString());

        var timestamp = handler.Headers["X-AgentNotify-Timestamp"];
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("signing-key"),
            Encoding.UTF8.GetBytes($"{timestamp}.{handler.Body}"));
        Assert.Equal(
            "sha256=" + Convert.ToHexString(expected).ToLowerInvariant(),
            handler.Headers["X-AgentNotify-Signature"]);
    }

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("https://user:secret@example.com/hook")]
    [InlineData("https://example.com/hook#fragment")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    public async Task RejectsUnsafeDestinationBeforeSending(string url)
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var adapter = new WebhookChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(secrets: new Dictionary<string, string> { ["endpoint_url"] = url }),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ExplicitPrivateNetworkConsentAllowsPrivateHttpsDestination()
    {
        var handler = new CapturingHandler(HttpStatusCode.NoContent);
        using var adapter = new WebhookChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(
            MakeDelivery(
                "{\"allowPrivateNetwork\":true}",
                new Dictionary<string, string> { ["endpoint_url"] = "https://127.0.0.1/hook" }),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "http_429")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "http_503")]
    [InlineData(HttpStatusCode.BadRequest, false, "http_400")]
    [InlineData(HttpStatusCode.Found, false, "http_redirect")]
    public async Task ClassifiesHttpStatusWithoutReadingResponseBody(
        HttpStatusCode status,
        bool retryable,
        string errorCode)
    {
        var handler = new CapturingHandler(status);
        using var adapter = new WebhookChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(errorCode, result.ErrorCode);
    }

    [Fact]
    public async Task RejectsHeaderInjectionAndMissingSecretReference()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        using var adapter = new WebhookChannelAdapter(new HttpClient(handler));
        var config =
            """
            {
              "headers":{"X-Unsafe":"line1\r\nInjected: yes"},
              "secretHeaders":{"Authorization":"missing"}
            }
            """;

        var result = await adapter.DeliverAsync(MakeDelivery(config), CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("8.8.8.8", false, true)]
    [InlineData("10.0.0.1", false, false)]
    [InlineData("10.0.0.1", true, true)]
    [InlineData("127.0.0.1", false, false)]
    [InlineData("127.0.0.1", true, true)]
    [InlineData("169.254.169.254", true, false)]
    [InlineData("224.0.0.1", true, false)]
    [InlineData("192.0.2.1", true, false)]
    [InlineData("::1", false, false)]
    [InlineData("fc00::1", false, false)]
    [InlineData("fc00::1", true, true)]
    [InlineData("2001:db8::1", true, false)]
    public void DestinationPolicy_BlocksPrivateAndAlwaysBlocksLinkLocal(
        string address,
        bool allowPrivate,
        bool expected)
    {
        Assert.Equal(
            expected,
            WebhookChannelAdapter.IsAddressAllowed(IPAddress.Parse(address), allowPrivate));
    }

    private static OutboundDelivery MakeDelivery(
        string configJson = "{}",
        IReadOnlyDictionary<string, string>? secrets = null) =>
        new(
            "delivery-1",
            "notification-1",
            "{\"title\":\"Build failed\",\"message\":\"Compiler error\"}",
            new ProviderProfile
            {
                Id = "provider-1",
                Name = "Webhook",
                Kind = "webhook",
                Enabled = true,
                ConfigJson = configJson
            },
            secrets ?? new Dictionary<string, string>
            {
                ["endpoint_url"] = "https://example.test/hook"
            });

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;

        public CapturingHandler(HttpStatusCode statusCode) => _statusCode = statusCode;

        public int CallCount { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers)
                Headers[header.Key] = string.Join(",", header.Value);
            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("response-body-must-not-be-consumed")
            };
        }
    }
}
