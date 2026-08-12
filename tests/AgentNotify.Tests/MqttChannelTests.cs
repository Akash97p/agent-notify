using System.Net;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using MQTTnet;

namespace AgentNotify.Tests;

public sealed class MqttChannelTests
{
    [Fact]
    public async Task ProjectsFixedEncryptedDestinationAndCredentials()
    {
        var publisher = new Publisher(MqttPublishOutcome.Success());
        var adapter = new MqttChannelAdapter(publisher);

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.IsType<MqttPublishRequest>(publisher.Request);
        Assert.Equal("mqtt.example.com", request.BrokerHost);
        Assert.Equal(8883, request.Port);
        Assert.False(request.AllowPrivateNetwork);
        Assert.Equal("agentnotify-prod", request.ClientId);
        Assert.Equal("agents/production/alerts", request.Topic);
        Assert.Equal("mqtt-user", request.Username);
        Assert.Equal("mqtt-password-value", request.Password);
        Assert.Null(request.ClientCertificateThumbprint);
        Assert.Equal(1, request.Qos);
        Assert.Equal(300, request.MessageExpirySeconds);
        Assert.Equal("mqtt-outbox", request.DeliveryId);
        Assert.Equal("notification", request.NotificationId);
    }

    [Fact]
    public async Task PreservesRouteRedactedJsonWithoutAddingAgentSelectedTopic()
    {
        var publisher = new Publisher(MqttPublishOutcome.Success());
        var adapter = new MqttChannelAdapter(publisher);
        const string payload = "{\"title\":\"Private build\",\"message\":null,\"priority\":\"critical\",\"topic\":\"evil/topic\"}";

        await adapter.DeliverAsync(MakeDelivery(payload: payload), CancellationToken.None);

        Assert.Equal(payload, publisher.Request!.PayloadJson);
        Assert.Equal("agents/production/alerts", publisher.Request.Topic);
        Assert.DoesNotContain("mqtt-password-value", publisher.Request.PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("client_certificate")]
    [InlineData("username_and_certificate")]
    public async Task SupportsExplicitAuthenticationModes(string mode)
    {
        var publisher = new Publisher(MqttPublishOutcome.Success());
        var adapter = new MqttChannelAdapter(publisher);
        var result = await adapter.DeliverAsync(MakeDelivery(config: Config(
            authenticationMode: mode,
            anonymousAcknowledged: mode == "anonymous")), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal(mode.Contains("username", StringComparison.Ordinal) ? "mqtt-user" : null, publisher.Request!.Username);
        Assert.Equal(mode.Contains("certificate", StringComparison.Ordinal) ? new string('A', 40) : null,
            publisher.Request.ClientCertificateThumbprint);
    }

    [Fact]
    public async Task RequiresExplicitAnonymousAcknowledgement()
    {
        await AssertConfigurationRejected(Config(authenticationMode: "anonymous", anonymousAcknowledged: false));
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    public async Task EnforcesQosDuplicateAcknowledgement(int qos, bool acknowledged, bool succeeds)
    {
        var publisher = new Publisher(MqttPublishOutcome.Success());
        var adapter = new MqttChannelAdapter(publisher);
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(qos: qos, duplicateRiskAcknowledged: acknowledged)), CancellationToken.None);
        Assert.Equal(succeeds, result.Succeeded);
        Assert.Equal(succeeds ? null : "configuration_invalid", result.ErrorCode);
    }

    [Theory]
    [InlineData("agents/#")]
    [InlineData("agents/+/alerts")]
    [InlineData("/agents/alerts")]
    [InlineData("agents/alerts/")]
    [InlineData("agents//alerts")]
    [InlineData("$SYS/broker")]
    [InlineData("agents\0alerts")]
    public async Task RejectsUnsafeOrWildcardTopics(string topic)
    {
        var publisher = new Publisher(MqttPublishOutcome.Success());
        var adapter = new MqttChannelAdapter(publisher);
        var result = await adapter.DeliverAsync(MakeDelivery(topic: topic), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(publisher.Request);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://mqtt.example.com")]
    [InlineData("mqtt.example.com.")]
    [InlineData("-mqtt.example.com")]
    [InlineData("mqtt example.com")]
    [InlineData("mütt.example.com")]
    public async Task RejectsInvalidBrokerHosts(string host)
    {
        await AssertConfigurationRejected(Config(host: host));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task RejectsInvalidPort(int port)
    {
        await AssertConfigurationRejected(Config(port: port));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(86401)]
    public async Task RejectsInvalidExpiry(int expiry)
    {
        await AssertConfigurationRejected(Config(expiry: expiry));
    }

    [Theory]
    [InlineData("contains spaces")]
    [InlineData("client/one")]
    [InlineData("")]
    public async Task RejectsInvalidClientId(string clientId)
    {
        await AssertConfigurationRejected(Config(clientId: clientId));
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task RejectsInvalidClientCertificateThumbprint(string thumbprint)
    {
        var publisher = new Publisher(MqttPublishOutcome.Success());
        var adapter = new MqttChannelAdapter(publisher);
        var result = await adapter.DeliverAsync(MakeDelivery(
            config: Config(authenticationMode: "client_certificate"), thumbprint: thumbprint), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(publisher.Request);
    }

    [Fact]
    public async Task BoundsPayloadAndRequiresObjectJson()
    {
        var adapter = new MqttChannelAdapter(new Publisher(MqttPublishOutcome.Success()));
        Assert.Equal("configuration_invalid", (await adapter.DeliverAsync(
            MakeDelivery(payload: "[]"), CancellationToken.None)).ErrorCode);
        var oversized = JsonSerializer.Serialize(new { title = new string('x', 17 * 1024) });
        Assert.Equal("configuration_invalid", (await adapter.DeliverAsync(
            MakeDelivery(payload: oversized), CancellationToken.None)).ErrorCode);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, true, "mqtt_broker_unavailable")]
    [InlineData(false, false, "mqtt_not_authorized")]
    public async Task PropagatesSanitizedPublisherOutcome(bool succeeded, bool retryable, string? code)
    {
        var outcome = new MqttPublishOutcome(succeeded, retryable, code);
        var result = await new MqttChannelAdapter(new Publisher(outcome))
            .DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task RejectsArbitraryPublisherErrorTextFromDurableDiagnostics()
    {
        var result = await new MqttChannelAdapter(new Publisher(
                MqttPublishOutcome.Permanent("password=mqtt-password-value")))
            .DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal("mqtt_failure", result.ErrorCode);
        Assert.DoesNotContain("password", result.ErrorCode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, false, "mqtt_ambiguous_failure")]
    [InlineData(1, true, "mqtt_timeout")]
    [InlineData(2, true, "mqtt_timeout")]
    public async Task ClassifiesCancellationByQosSemantics(int qos, bool retryable, string code)
    {
        var publisher = new Publisher(new OperationCanceledException());
        var result = await new MqttChannelAdapter(publisher).DeliverAsync(
            MakeDelivery(config: Config(qos: qos, duplicateRiskAcknowledged: qos > 0)), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task CarriesExplicitPrivateNetworkConsentToPinnedTransport()
    {
        var publisher = new Publisher(MqttPublishOutcome.Success());
        var result = await new MqttChannelAdapter(publisher).DeliverAsync(
            MakeDelivery(config: Config(host: "broker.internal", allowPrivate: true)), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(publisher.Request!.AllowPrivateNetwork);
    }

    [Fact]
    public async Task ProductionPublisherRejectsMixedOrPrivateDnsBeforeConnecting()
    {
        var resolver = new Resolver(IPAddress.Parse("93.184.216.34"), IPAddress.Loopback);
        var publisher = new MqttNetPublisher(resolver.ResolveAsync);
        var outcome = await publisher.PublishAsync(Request(), CancellationToken.None);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Retryable);
        Assert.Equal("mqtt_destination_blocked", outcome.ErrorCode);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public void ProductionOptionsPinAddressAndKeepTlsHostnameValidation()
    {
        var address = IPAddress.Parse("93.184.216.34");
        var options = MqttNetPublisher.BuildClientOptions(Request(), address, null);
        var tcp = Assert.IsType<MqttClientTcpOptions>(options.ChannelOptions);
        var endpoint = Assert.IsType<IPEndPoint>(tcp.RemoteEndpoint);
        Assert.Equal(address, endpoint.Address);
        Assert.Equal(8883, endpoint.Port);
        Assert.True(tcp.TlsOptions.UseTls);
        Assert.Equal("mqtt.example.com", tcp.TlsOptions.TargetHost);
        Assert.False(tcp.TlsOptions.AllowUntrustedCertificates);
        Assert.False(tcp.TlsOptions.IgnoreCertificateChainErrors);
        Assert.False(tcp.TlsOptions.IgnoreCertificateRevocationErrors);
    }

    private static async Task AssertConfigurationRejected(string config)
    {
        var publisher = new Publisher(MqttPublishOutcome.Success());
        var result = await new MqttChannelAdapter(publisher).DeliverAsync(
            MakeDelivery(config: config), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(publisher.Request);
    }

    private static string Config(
        string host = "mqtt.example.com",
        int port = 8883,
        bool allowPrivate = false,
        string clientId = "agentnotify-prod",
        string authenticationMode = "username_password",
        bool anonymousAcknowledged = false,
        int qos = 1,
        bool duplicateRiskAcknowledged = true,
        int expiry = 300) => JsonSerializer.Serialize(new
        {
            brokerHost = host,
            port,
            allowPrivateNetwork = allowPrivate,
            clientId,
            topicSecretName = "topic",
            authenticationMode,
            usernameSecretName = "username",
            passwordSecretName = "password",
            clientCertificateThumbprintSecretName = "client_certificate_thumbprint",
            anonymousAcknowledged,
            qos,
            duplicateRiskAcknowledged,
            messageExpirySeconds = expiry
        });

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler failed\",\"priority\":\"critical\"}",
        string? config = null,
        string topic = "agents/production/alerts",
        string thumbprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") => new(
            "mqtt-outbox",
            "notification",
            payload,
            new ProviderProfile
            {
                Id = "mqtt",
                Name = "MQTT",
                Kind = "mqtt",
                Enabled = true,
                ConfigJson = config ?? Config()
            },
            new Dictionary<string, string>
            {
                ["topic"] = topic,
                ["username"] = "mqtt-user",
                ["password"] = "mqtt-password-value",
                ["client_certificate_thumbprint"] = thumbprint
            });

    private static MqttPublishRequest Request() => new(
        "mqtt.example.com", 8883, false, "agentnotify-prod", "agents/production/alerts",
        "{\"title\":\"Build failed\"}", 1, 300, "mqtt-outbox", "notification",
        "mqtt-user", "mqtt-password-value", null);

    private sealed class Publisher : IMqttPublisher
    {
        private readonly MqttPublishOutcome? _outcome;
        private readonly Exception? _exception;
        public Publisher(MqttPublishOutcome outcome) => _outcome = outcome;
        public Publisher(Exception exception) => _exception = exception;
        public MqttPublishRequest? Request { get; private set; }
        public Task<MqttPublishOutcome> PublishAsync(MqttPublishRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            if (_exception is not null) throw _exception;
            return Task.FromResult(_outcome!);
        }
    }

    private sealed class Resolver(params IPAddress[] addresses)
    {
        public int Calls { get; private set; }
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(addresses);
        }
    }
}
