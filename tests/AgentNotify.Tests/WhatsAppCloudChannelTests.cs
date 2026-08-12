using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class WhatsAppCloudChannelTests
{
    private const string Success =
        "{\"messaging_product\":\"whatsapp\",\"contacts\":[{\"input\":\"15551234567\",\"wa_id\":\"15551234567\"}],\"messages\":[{\"id\":\"wamid.example_message_identifier\"}]}";

    [Fact]
    public async Task PostsApprovedTemplateToExactVersionedMetaEndpoint()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("https://graph.facebook.com/v25.0/123456789012345/messages", handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer secret-access-token-value", handler.Authorization);
        Assert.Equal("application/json", handler.ContentType);
        using var document = JsonDocument.Parse(handler.Body!);
        var root = document.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("individual", root.GetProperty("recipient_type").GetString());
        Assert.Equal("15551234567", root.GetProperty("to").GetString());
        Assert.Equal("template", root.GetProperty("type").GetString());
        Assert.Equal("agentnotify_alert", root.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal("en_US", root.GetProperty("template").GetProperty("language").GetProperty("code").GetString());
        Assert.DoesNotContain("secret-access-token-value", handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapsOrderedApprovedTemplateBodyParameters()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(config: Config(parameters: ["title", "message", "priority", "agent"])), CancellationToken.None);
        using var document = JsonDocument.Parse(handler.Body!);
        var parameters = document.RootElement.GetProperty("template").GetProperty("components")[0].GetProperty("parameters");
        Assert.Equal(new[] { "Build failed", "Compiler failed", "critical", "codex" },
            parameters.EnumerateArray().Select(value => value.GetProperty("text").GetString()).ToArray());
        Assert.All(parameters.EnumerateArray(), value => Assert.Equal("text", value.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task OmitsComponentsForTemplateWithoutVariables()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(config: Config(parameters: [])), CancellationToken.None);
        using var document = JsonDocument.Parse(handler.Body!);
        Assert.False(document.RootElement.GetProperty("template").TryGetProperty("components", out _));
    }

    [Fact]
    public async Task HonorsRouteRedactionWithoutChangingTemplateArity()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(
            payload: "{\"title\":\"Build failed\",\"message\":null,\"priority\":\"critical\",\"agent\":\"codex\"}",
            config: Config(parameters: ["title", "message"])), CancellationToken.None);
        using var document = JsonDocument.Parse(handler.Body!);
        var parameters = document.RootElement.GetProperty("template").GetProperty("components")[0].GetProperty("parameters");
        Assert.Equal("Details withheld", parameters[1].GetProperty("text").GetString());
        Assert.DoesNotContain("Compiler failed", handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatesUnicodeByScalarsWithoutSplittingSurrogates()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = string.Concat(Enumerable.Repeat("😀", 300)), priority = "critical" });
        await adapter.DeliverAsync(MakeDelivery(payload: payload, config: Config(parameters: ["title"])), CancellationToken.None);
        using var document = JsonDocument.Parse(handler.Body!);
        var text = document.RootElement.GetProperty("template").GetProperty("components")[0]
            .GetProperty("parameters")[0].GetProperty("text").GetString()!;
        Assert.Equal(250, text.EnumerateRunes().Count());
        Assert.EndsWith("...", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public async Task BlocksNotificationsBelowConfiguredPaidPriority()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Build\",\"priority\":\"normal\"}"),
            CancellationToken.None);
        Assert.Equal("cost_policy_blocked", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task ExplicitTestBypassesPriorityFloorButStillUsesConsent()
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Test\"}", notificationId: "test"), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task RequiresEveryConsentAndTemplateAcknowledgement(bool optIn, bool approved, bool paid)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(optIn: optIn, approved: approved, paid: paid)), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData("25.0")]
    [InlineData("v25")]
    [InlineData("v0.0")]
    [InlineData("v25.1")]
    [InlineData("v100.0")]
    [InlineData("v25.0/evil")]
    public async Task RejectsInvalidGraphVersion(string version)
    {
        await AssertConfigurationRejected(Config(version: version));
    }

    [Theory]
    [InlineData("Agent Notify")]
    [InlineData("UPPER_CASE")]
    [InlineData("agent-notify")]
    [InlineData("")]
    public async Task RejectsInvalidTemplateName(string name)
    {
        await AssertConfigurationRejected(Config(template: name));
    }

    [Theory]
    [InlineData("english")]
    [InlineData("en-us")]
    [InlineData("EN_US")]
    [InlineData("en_US/evil")]
    public async Task RejectsInvalidLanguageCode(string language)
    {
        await AssertConfigurationRejected(Config(language: language));
    }

    [Theory]
    [InlineData("15551234567")]
    [InlineData("+05551234567")]
    [InlineData("+1555")]
    [InlineData("whatsapp:+15551234567")]
    public async Task RejectsNonE164Recipient(string recipient)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(recipient: recipient), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("12345x")]
    [InlineData("123456789012345678901234567890123")]
    public async Task RejectsInvalidPhoneNumberId(string phoneNumberId)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(phoneNumberId: phoneNumberId), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task RejectsUnknownDuplicateAndExcessTemplateMappings()
    {
        await AssertConfigurationRejected(Config(parameters: ["title", "unknown"]));
        await AssertConfigurationRejected(Config(parameters: ["title", "title"]));
        await AssertConfigurationRejected(Config(parameters: ["title", "message", "priority", "type", "agent", "project"]));
    }

    [Fact]
    public async Task RejectsExplicitNullConfigurationFieldsWithoutThrowing()
    {
        var valid = Config();
        foreach (var malformed in new[]
                 {
                     valid.Replace("\"apiVersion\":\"v25.0\"", "\"apiVersion\":null", StringComparison.Ordinal),
                     valid.Replace("\"templateName\":\"agentnotify_alert\"", "\"templateName\":null", StringComparison.Ordinal),
                     valid.Replace("\"languageCode\":\"en_US\"", "\"languageCode\":null", StringComparison.Ordinal),
                     valid.Replace("\"bodyParameters\":[\"title\",\"message\"]", "\"bodyParameters\":null", StringComparison.Ordinal)
                 })
            await AssertConfigurationRejected(malformed);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "whatsapp_429")]
    [InlineData(HttpStatusCode.BadRequest, false, "whatsapp_400")]
    [InlineData(HttpStatusCode.Unauthorized, false, "whatsapp_401")]
    [InlineData(HttpStatusCode.Redirect, false, "whatsapp_redirect")]
    [InlineData(HttpStatusCode.RequestTimeout, false, "whatsapp_ambiguous_failure")]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, "whatsapp_ambiguous_failure")]
    public async Task UsesAtMostOnceCostSafeStatusPolicy(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(status, "{}");
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"messaging_product\":\"whatsapp\",\"messages\":[]}")]
    [InlineData("{\"messaging_product\":\"whatsapp\",\"messages\":[{\"id\":\"bad\"}]}")]
    [InlineData("not-json")]
    public async Task DoesNotRetryMalformedSuccessResponse(string body)
    {
        var handler = new Handler(HttpStatusCode.OK, body);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.False(result.Retryable);
        Assert.Equal("whatsapp_ambiguous_response", result.ErrorCode);
    }

    [Fact]
    public async Task BoundsResponseBodyWithoutRetryingPaidRequest()
    {
        var handler = new Handler(HttpStatusCode.OK, new string('x', 33 * 1024));
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.False(result.Retryable);
        Assert.Equal("whatsapp_ambiguous_response", result.ErrorCode);
    }

    [Fact]
    public async Task NetworkAndCancellationFailuresAreNotReplayed()
    {
        foreach (var exception in new Exception[]
                 {
                     new HttpRequestException("secret-bearing details"),
                     new OperationCanceledException("delivery timeout")
                 })
        {
            var handler = new Handler(exception);
            using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
            var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
            Assert.False(result.Retryable);
            Assert.Equal("whatsapp_ambiguous_failure", result.ErrorCode);
        }
    }

    private static async Task AssertConfigurationRejected(string config)
    {
        var handler = new Handler(HttpStatusCode.OK, Success);
        using var adapter = new WhatsAppCloudChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: config), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    private static string Config(
        string version = "v25.0",
        string template = "agentnotify_alert",
        string language = "en_US",
        string[]? parameters = null,
        bool optIn = true,
        bool approved = true,
        bool paid = true,
        string minimumPriority = "critical") =>
        JsonSerializer.Serialize(new
        {
            apiVersion = version,
            phoneNumberIdSecretName = "phone_number_id",
            accessTokenSecretName = "access_token",
            recipientSecretName = "recipient",
            templateName = template,
            languageCode = language,
            bodyParameters = parameters ?? ["title", "message"],
            recipientOptInAcknowledged = optIn,
            templateApprovedAcknowledged = approved,
            paidSendConsent = paid,
            minimumPriority
        });

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}",
        string? config = null,
        string phoneNumberId = "123456789012345",
        string accessToken = "secret-access-token-value",
        string recipient = "+15551234567",
        string notificationId = "notification") =>
        new(
            "whatsapp-outbox",
            notificationId,
            payload,
            new ProviderProfile
            {
                Id = "whatsapp",
                Name = "WhatsApp Cloud",
                Kind = "whatsapp_cloud",
                Enabled = true,
                ConfigJson = config ?? Config()
            },
            new Dictionary<string, string>
            {
                ["phone_number_id"] = phoneNumberId,
                ["access_token"] = accessToken,
                ["recipient"] = recipient
            });

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _response = "";
        private readonly Exception? _exception;
        public Handler(HttpStatusCode status, string response) { _status = status; _response = response; }
        public Handler(Exception exception) => _exception = exception;
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (_exception is not null) throw _exception;
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(token);
            ContentType = request.Content.Headers.ContentType?.MediaType;
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }
}
