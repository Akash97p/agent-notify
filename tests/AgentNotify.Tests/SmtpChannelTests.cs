using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class SmtpChannelTests
{
    [Fact]
    public async Task BuildsAuthenticatedStartTlsMessageForAllowlistedRecipients()
    {
        var sender = new RecordingSender(DeliveryResult.Success(250));
        var adapter = new SmtpChannelAdapter(sender);
        var delivery = MakeDelivery(
            """
            {
              "host":"smtp.example.test",
              "port":587,
              "security":"start_tls",
              "fromAddress":"notify@example.test",
              "fromName":"AgentNotify Bot",
              "recipients":["Akash <akash@example.test>","akash@example.test"],
              "subjectPrefix":"[Build] "
            }
            """);

        var result = await adapter.DeliverAsync(delivery, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(sender.Requests);
        Assert.False(request.UseTlsOnConnect);
        Assert.Equal("smtp-user", request.Username);
        Assert.Equal("smtp-password", request.Password);
        Assert.Equal(["akash@example.test"], request.Recipients);
        Assert.Equal("[Build] Build failed", request.Subject);
        Assert.Contains("Compiler error", request.TextBody, StringComparison.Ordinal);
        Assert.Contains("Priority: critical", request.TextBody, StringComparison.Ordinal);
        Assert.Equal("delivery-1", request.DeliveryId);
    }

    [Fact]
    public async Task SupportsTlsOnConnectAndRouteMessageRedaction()
    {
        var sender = new RecordingSender(DeliveryResult.Success());
        var adapter = new SmtpChannelAdapter(sender);
        var config = ValidConfig.Replace("start_tls", "tls", StringComparison.Ordinal)
            .Replace("587", "465", StringComparison.Ordinal);
        var delivery = MakeDelivery(config, payload: "{\"title\":\"Secret build\",\"message\":null}");

        var result = await adapter.DeliverAsync(delivery, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(sender.Requests);
        Assert.True(request.UseTlsOnConnect);
        Assert.DoesNotContain("Compiler error", request.TextBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("auto")]
    [InlineData("start_tls_when_available")]
    public async Task RejectsDowngradeCapableSecurityModes(string security)
    {
        var sender = new RecordingSender(DeliveryResult.Success());
        var adapter = new SmtpChannelAdapter(sender);
        var config = ValidConfig.Replace("start_tls", security, StringComparison.Ordinal);

        var result = await adapter.DeliverAsync(MakeDelivery(config), CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Empty(sender.Requests);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("169.254.169.254", true)]
    public async Task EnforcesDestinationPolicyBeforeTransport(string host, bool allowPrivate)
    {
        var sender = new RecordingSender(DeliveryResult.Success());
        var adapter = new SmtpChannelAdapter(sender);
        var config = ValidConfig
            .Replace("smtp.example.test", host, StringComparison.Ordinal)
            .Replace("\"fromAddress\"", $"\"allowPrivateNetwork\":{allowPrivate.ToString().ToLowerInvariant()},\"fromAddress\"", StringComparison.Ordinal);

        var result = await adapter.DeliverAsync(MakeDelivery(config), CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Empty(sender.Requests);
    }

    [Theory]
    [InlineData("bad-address")]
    [InlineData("victim@example.test\r\nBcc: hidden@example.test")]
    public async Task RejectsInvalidOrInjectedRecipient(string recipient)
    {
        var sender = new RecordingSender(DeliveryResult.Success());
        var adapter = new SmtpChannelAdapter(sender);
        var config = ValidConfig.Replace("user@example.test", recipient.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n"), StringComparison.Ordinal);

        var result = await adapter.DeliverAsync(MakeDelivery(config), CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task RequiresEncryptedAuthenticationSecrets()
    {
        var sender = new RecordingSender(DeliveryResult.Success());
        var adapter = new SmtpChannelAdapter(sender);

        var result = await adapter.DeliverAsync(
            MakeDelivery(ValidConfig, new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Empty(sender.Requests);
    }

    [Fact]
    public async Task PreservesSanitizedTransportFailure()
    {
        var sender = new RecordingSender(DeliveryResult.Retry("smtp_451", 451));
        var adapter = new SmtpChannelAdapter(sender);

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Retryable);
        Assert.Equal("smtp_451", result.ErrorCode);
        Assert.Equal(451, result.StatusCode);
    }

    private const string ValidConfig =
        "{\"host\":\"smtp.example.test\",\"port\":587,\"security\":\"start_tls\",\"fromAddress\":\"notify@example.test\",\"recipients\":[\"user@example.test\"]}";

    private static OutboundDelivery MakeDelivery(
        string config = ValidConfig,
        IReadOnlyDictionary<string, string>? secrets = null,
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler error\",\"priority\":\"critical\",\"type\":\"error\",\"agent\":\"codex\",\"project\":\"agent-notify\"}") =>
        new(
            "delivery-1",
            "notification-1",
            payload,
            new ProviderProfile
            {
                Id = "smtp-1",
                Name = "Email",
                Kind = "smtp",
                Enabled = true,
                ConfigJson = config
            },
            secrets ?? new Dictionary<string, string>
            {
                ["username"] = "smtp-user",
                ["password"] = "smtp-password"
            });

    private sealed class RecordingSender : ISmtpSender
    {
        private readonly DeliveryResult _result;

        public RecordingSender(DeliveryResult result) => _result = result;

        public List<SmtpSendRequest> Requests { get; } = [];

        public Task<DeliveryResult> SendAsync(
            SmtpSendRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_result);
        }
    }
}
