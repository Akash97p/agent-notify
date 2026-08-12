using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class TwilioWhatsAppChannelTests
{
    private const string AccountSid = "ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ApiKeySid = "SKbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CredentialSecret = "cccccccccccccccccccccccccccccccc";
    private const string MessagingServiceSid = "MGdddddddddddddddddddddddddddddddd";
    private const string ContentSid = "HXeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string MessageSid = "SMffffffffffffffffffffffffffffffff";
    private const string Success = "{\"sid\":\"" + MessageSid + "\",\"status\":\"queued\"}";

    [Fact]
    public async Task PostsContentTemplateThroughMessagingService()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json", handler.Uri!.AbsoluteUri);
        Assert.Equal($"{ApiKeySid}:{CredentialSecret}", DecodeBasic(handler.Authorization!));
        var form = ParseForm(handler.Body!);
        Assert.Equal("whatsapp:+15551234567", form["To"]);
        Assert.Equal(MessagingServiceSid, form["MessagingServiceSid"]);
        Assert.Equal(ContentSid, form["ContentSid"]);
        Assert.Equal("300", form["ValidityPeriod"]);
        Assert.Equal("discard", form["ContentRetention"]);
        Assert.Equal("obfuscate", form["AddressRetention"]);
        Assert.DoesNotContain("Body", form.Keys);
        Assert.DoesNotContain("From", form.Keys);
        Assert.DoesNotContain("MediaUrl", form.Keys);
        Assert.DoesNotContain("StatusCallback", form.Keys);
    }

    [Fact]
    public async Task MapsOrderedContentVariablesAsJsonObject()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(config: Config(variables: ["title", "message", "priority", "agent"])), CancellationToken.None);
        var form = ParseForm(handler.Body!);
        using var document = JsonDocument.Parse(form["ContentVariables"]);
        Assert.Equal("Build failed", document.RootElement.GetProperty("1").GetString());
        Assert.Equal("Compiler failed", document.RootElement.GetProperty("2").GetString());
        Assert.Equal("critical", document.RootElement.GetProperty("3").GetString());
        Assert.Equal("codex", document.RootElement.GetProperty("4").GetString());
    }

    [Fact]
    public async Task OmitsContentVariablesForTemplateWithoutPlaceholders()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(config: Config(variables: [])), CancellationToken.None);
        Assert.DoesNotContain("ContentVariables", ParseForm(handler.Body!).Keys);
    }

    [Fact]
    public async Task HonorsRouteRedactionWithoutChangingTemplateArity()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(MakeDelivery(
            payload: "{\"title\":\"Build failed\",\"message\":null,\"priority\":\"critical\"}"), CancellationToken.None);
        var variables = JsonDocument.Parse(ParseForm(handler.Body!)["ContentVariables"]).RootElement;
        Assert.Equal("Details withheld", variables.GetProperty("2").GetString());
    }

    [Fact]
    public async Task SupportsExplicitLocalTestingAuthTokenMode()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(credentialMode: "auth_token"), includeCredentialSid: false), CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal($"{AccountSid}:{CredentialSecret}", DecodeBasic(handler.Authorization!));
    }

    [Fact]
    public async Task BlocksNotificationsBelowConfiguredPaidPriority()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Build\",\"priority\":\"normal\"}"), CancellationToken.None);
        Assert.Equal("cost_policy_blocked", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task ExplicitTestBypassesPriorityFloorButStillUsesAcknowledgements()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Test\"}", notificationId: "test"), CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task RequiresEveryComplianceAcknowledgement(bool optIn, bool approved, bool textOnly, bool paid)
    {
        await AssertConfigurationRejected(Config(optIn: optIn, approved: approved, textOnly: textOnly, paid: paid));
    }

    [Theory]
    [InlineData("15551234567")]
    [InlineData("+05551234567")]
    [InlineData("+1555")]
    [InlineData("whatsapp:+15551234567")]
    public async Task RejectsInvalidRecipient(string recipient)
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(recipient: recipient), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData("ACbad", MessagingServiceSid, ContentSid)]
    [InlineData(AccountSid, "MGbad", ContentSid)]
    [InlineData(AccountSid, MessagingServiceSid, "HXbad")]
    [InlineData(AccountSid, MessagingServiceSid, "ZZeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")]
    public async Task RejectsInvalidAccountServiceAndContentSids(string account, string service, string content)
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(accountSid: account, messagingServiceSid: service, contentSid: content), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task RejectsUnknownDuplicateExcessAndNullVariableMappings()
    {
        await AssertConfigurationRejected(Config(variables: ["title", "unknown"]));
        await AssertConfigurationRejected(Config(variables: ["title", "title"]));
        await AssertConfigurationRejected(Config(variables: ["title", "message", "priority", "type", "agent", "project"]));
        await AssertConfigurationRejected(Config().Replace(
            "\"contentVariables\":[\"title\",\"message\"]", "\"contentVariables\":null", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(36001)]
    public async Task RejectsInvalidValidityPeriod(int validity)
    {
        await AssertConfigurationRejected(Config(validity: validity));
    }

    [Fact]
    public async Task TruncatesUnicodeVariablesWithoutSplittingSurrogates()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = string.Concat(Enumerable.Repeat("😀", 300)), priority = "critical" });
        await adapter.DeliverAsync(MakeDelivery(payload: payload, config: Config(variables: ["title"])), CancellationToken.None);
        using var document = JsonDocument.Parse(ParseForm(handler.Body!)["ContentVariables"]);
        var text = document.RootElement.GetProperty("1").GetString()!;
        Assert.Equal(250, text.EnumerateRunes().Count());
        Assert.EndsWith("...", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "twilio_whatsapp_429")]
    [InlineData(HttpStatusCode.BadRequest, false, "twilio_whatsapp_400")]
    [InlineData(HttpStatusCode.Unauthorized, false, "twilio_whatsapp_401")]
    [InlineData(HttpStatusCode.Redirect, false, "twilio_whatsapp_redirect")]
    [InlineData(HttpStatusCode.RequestTimeout, false, "twilio_whatsapp_ambiguous_failure")]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, "twilio_whatsapp_ambiguous_failure")]
    public async Task UsesAtMostOnceCostSafeStatusPolicy(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(status, "{}");
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sid\":\"SMbad\",\"status\":\"queued\"}")]
    [InlineData("{\"sid\":\"" + MessageSid + "\",\"status\":\"mystery\"}")]
    [InlineData("not-json")]
    public async Task DoesNotRetryMalformedSuccessResponse(string response)
    {
        var handler = new Handler(HttpStatusCode.Created, response);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.False(result.Retryable);
        Assert.Equal("twilio_whatsapp_ambiguous_response", result.ErrorCode);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("undelivered")]
    [InlineData("canceled")]
    public async Task TreatsImmediateProviderRejectionAsPermanent(string status)
    {
        var handler = new Handler(HttpStatusCode.Created, $"{{\"sid\":\"{MessageSid}\",\"status\":\"{status}\"}}");
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal("twilio_whatsapp_rejected", result.ErrorCode);
        Assert.False(result.Retryable);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("read")]
    public async Task AcceptsDocumentedWhatsAppMessageStates(string status)
    {
        var handler = new Handler(HttpStatusCode.Created, $"{{\"sid\":\"{MessageSid}\",\"status\":\"{status}\"}}");
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        Assert.True((await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task BoundsResponseAndDoesNotReplayNetworkOrCancellationAmbiguity()
    {
        var oversized = new Handler(HttpStatusCode.Created, new string('x', 33 * 1024));
        using (var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(oversized)))
            Assert.Equal("twilio_whatsapp_ambiguous_response",
                (await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None)).ErrorCode);
        foreach (var exception in new Exception[] { new HttpRequestException("private"), new OperationCanceledException() })
        {
            var handler = new Handler(exception);
            using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
            var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
            Assert.False(result.Retryable);
            Assert.Equal("twilio_whatsapp_ambiguous_failure", result.ErrorCode);
        }
    }

    private static async Task AssertConfigurationRejected(string config)
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioWhatsAppChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(config: config), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    private static string Config(
        string credentialMode = "api_key",
        string[]? variables = null,
        bool optIn = true,
        bool approved = true,
        bool textOnly = true,
        bool paid = true,
        string minimumPriority = "critical",
        int validity = 300) =>
        JsonSerializer.Serialize(new
        {
            accountSidSecretName = "account_sid",
            credentialMode,
            credentialSidSecretName = "credential_sid",
            credentialSecretName = "credential_secret",
            recipientSecretName = "recipient",
            messagingServiceSidSecretName = "messaging_service_sid",
            contentSidSecretName = "content_sid",
            contentVariables = variables ?? ["title", "message"],
            recipientOptInAcknowledged = optIn,
            templateApprovedAcknowledged = approved,
            textOnlyTemplateAcknowledged = textOnly,
            paidSendConsent = paid,
            minimumPriority,
            validityPeriodSeconds = validity
        });

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler failed\",\"priority\":\"critical\",\"agent\":\"codex\"}",
        string? config = null,
        string accountSid = AccountSid,
        string credentialSid = ApiKeySid,
        string credentialSecret = CredentialSecret,
        string recipient = "+15551234567",
        string messagingServiceSid = MessagingServiceSid,
        string contentSid = ContentSid,
        bool includeCredentialSid = true,
        string notificationId = "notification")
    {
        var secrets = new Dictionary<string, string>
        {
            ["account_sid"] = accountSid,
            ["credential_secret"] = credentialSecret,
            ["recipient"] = recipient,
            ["messaging_service_sid"] = messagingServiceSid,
            ["content_sid"] = contentSid
        };
        if (includeCredentialSid) secrets["credential_sid"] = credentialSid;
        return new OutboundDelivery(
            "twilio-whatsapp-outbox", notificationId, payload,
            new ProviderProfile
            {
                Id = "twilio-whatsapp",
                Name = "Twilio WhatsApp",
                Kind = "twilio_whatsapp",
                Enabled = true,
                ConfigJson = config ?? Config()
            }, secrets);
    }

    private static string DecodeBasic(string authorization)
    {
        Assert.StartsWith("Basic ", authorization, StringComparison.Ordinal);
        return Encoding.ASCII.GetString(Convert.FromBase64String(authorization[6..]));
    }

    private static Dictionary<string, string> ParseForm(string body) => body
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Decode(pair[0]), pair => Decode(pair.Length == 2 ? pair[1] : ""), StringComparer.Ordinal);

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _response = "";
        private readonly Exception? _exception;
        public Handler(HttpStatusCode status, string response) { _status = status; _response = response; }
        public Handler(Exception exception) => _exception = exception;
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (_exception is not null) throw _exception;
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(token);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }
}
