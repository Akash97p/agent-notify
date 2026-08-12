using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class TwilioSmsChannelTests
{
    private const string AccountSid = "ACaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ApiKeySid = "SKbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CredentialSecret = "cccccccccccccccccccccccccccccccc";
    private const string MessagingServiceSid = "MGdddddddddddddddddddddddddddddddd";
    private const string MessageSid = "SMeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string Success = "{\"sid\":\"" + MessageSid + "\",\"status\":\"queued\",\"num_segments\":\"1\"}";

    [Fact]
    public async Task PostsSingleSmsWithApiKeyBasicAuthentication()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));

        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal($"https://api.twilio.com/2010-04-01/Accounts/{AccountSid}/Messages.json", handler.Uri!.AbsoluteUri);
        Assert.Equal("application/x-www-form-urlencoded", handler.ContentType);
        Assert.Equal($"{ApiKeySid}:{CredentialSecret}", DecodeBasic(handler.Authorization!));
        var form = ParseForm(handler.Body!);
        Assert.Equal("+15551234567", form["To"]);
        Assert.Equal("+15557654321", form["From"]);
        Assert.Equal("300", form["ValidityPeriod"]);
        Assert.Equal("true", form["SmartEncoded"]);
        Assert.Equal("discard", form["ContentRetention"]);
        Assert.Equal("obfuscate", form["AddressRetention"]);
        Assert.DoesNotContain("StatusCallback", form.Keys);
        Assert.DoesNotContain("MediaUrl", form.Keys);
        Assert.DoesNotContain(CredentialSecret, handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupportsExplicitLocalTestingAuthTokenMode()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(credentialMode: "auth_token"), includeCredentialSid: false),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.Equal($"{AccountSid}:{CredentialSecret}", DecodeBasic(handler.Authorization!));
    }

    [Fact]
    public async Task SupportsMessagingServiceAndAcceptedZeroSegmentResponse()
    {
        var response = "{\"sid\":\"" + MessageSid + "\",\"status\":\"accepted\",\"num_segments\":\"0\"}";
        var handler = new Handler(HttpStatusCode.Created, response);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(senderType: "messaging_service"), sender: MessagingServiceSid),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        var form = ParseForm(handler.Body!);
        Assert.Equal(MessagingServiceSid, form["MessagingServiceSid"]);
        Assert.DoesNotContain("From", form.Keys);
    }

    [Fact]
    public async Task HonorsRouteRedaction()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Build failed\",\"message\":null,\"priority\":\"critical\"}"),
            CancellationToken.None);
        var form = ParseForm(handler.Body!);
        Assert.DoesNotContain("Compiler failed", form["Body"], StringComparison.Ordinal);
        Assert.Equal("[CRITICAL] Build failed", form["Body"]);
    }

    [Fact]
    public async Task BlocksNotificationsBelowConfiguredPaidPriority()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Build\",\"priority\":\"normal\"}"),
            CancellationToken.None);
        Assert.Equal("cost_policy_blocked", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task ExplicitTestSendBypassesPriorityFloorButStillUsesPaidConsent()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(payload: "{\"title\":\"Test\"}", notificationId: "test"),
            CancellationToken.None);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RequiresPaidSendConsent()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(paidSendConsent: false)),
            CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData("+1555")]
    [InlineData("15551234567")]
    [InlineData("+05551234567")]
    [InlineData("whatsapp:+15551234567")]
    public async Task RejectsNonE164SmsRecipient(string recipient)
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(recipient: recipient), CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData("bad-account", ApiKeySid)]
    [InlineData(AccountSid, "SKnothexnothexnothexnothexnothexno")]
    public async Task RejectsInvalidCredentialSids(string accountSid, string credentialSid)
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(accountSid: accountSid, credentialSid: credentialSid),
            CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(36001)]
    public async Task RejectsUnsafeValidityPeriod(int validityPeriod)
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(
            MakeDelivery(config: Config(validityPeriod: validityPeriod)),
            CancellationToken.None);
        Assert.Equal("configuration_invalid", result.ErrorCode);
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task TruncatesGsmTextToOne160SeptetSegment()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = "Build", message = new string('a', 300), priority = "critical" });
        await adapter.DeliverAsync(MakeDelivery(payload: payload), CancellationToken.None);
        var body = ParseForm(handler.Body!)["Body"];
        Assert.Equal(160, CountGsmSeptets(body));
        Assert.EndsWith("...", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountsExtendedGsmCharactersAsTwoSeptets()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = new string('^', 100), priority = "critical" });
        await adapter.DeliverAsync(MakeDelivery(payload: payload), CancellationToken.None);
        var body = ParseForm(handler.Body!)["Body"];
        var septets = CountGsmSeptets(body);
        Assert.True(septets <= 160);
        Assert.EndsWith("...", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatesUnicodeToOne70CodeUnitSegmentWithoutSplittingSurrogate()
    {
        var handler = new Handler(HttpStatusCode.Created, Success);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var payload = JsonSerializer.Serialize(new { title = "Build 😀", message = string.Concat(Enumerable.Repeat("😀", 100)), priority = "critical" });
        await adapter.DeliverAsync(MakeDelivery(payload: payload), CancellationToken.None);
        var body = ParseForm(handler.Body!)["Body"];
        Assert.True(body.Length <= 70);
        Assert.EndsWith("...", body, StringComparison.Ordinal);
        Assert.False(body.Any(character => char.IsSurrogate(character)) && !HasValidSurrogates(body));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true, "twilio_429")]
    [InlineData(HttpStatusCode.BadRequest, false, "twilio_400")]
    [InlineData(HttpStatusCode.Unauthorized, false, "twilio_401")]
    [InlineData(HttpStatusCode.Redirect, false, "twilio_redirect")]
    [InlineData(HttpStatusCode.RequestTimeout, false, "twilio_ambiguous_failure")]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, "twilio_ambiguous_failure")]
    public async Task UsesAtMostOnceCostSafeStatusPolicy(HttpStatusCode status, bool retryable, string code)
    {
        var handler = new Handler(status, "{}");
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.Equal(retryable, result.Retryable);
        Assert.Equal(code, result.ErrorCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sid\":\"SMbad\",\"status\":\"queued\",\"num_segments\":\"1\"}")]
    [InlineData("{\"sid\":\"" + MessageSid + "\",\"status\":\"queued\",\"num_segments\":\"2\"}")]
    [InlineData("not-json")]
    public async Task DoesNotRetryAmbiguousSuccessResponse(string body)
    {
        var handler = new Handler(HttpStatusCode.Created, body);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.False(result.Retryable);
        Assert.Equal("twilio_ambiguous_response", result.ErrorCode);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("undelivered")]
    [InlineData("canceled")]
    public async Task TreatsImmediateProviderRejectionAsPermanent(string status)
    {
        var response = $"{{\"sid\":\"{MessageSid}\",\"status\":\"{status}\",\"num_segments\":\"1\"}}";
        var handler = new Handler(HttpStatusCode.Created, response);
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.False(result.Retryable);
        Assert.Equal("twilio_rejected", result.ErrorCode);
    }

    [Fact]
    public async Task BoundsResponseBodyWithoutRetryingPaidRequest()
    {
        var handler = new Handler(HttpStatusCode.Created, new string('x', 33 * 1024));
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.False(result.Retryable);
        Assert.Equal("twilio_ambiguous_response", result.ErrorCode);
    }

    [Fact]
    public async Task NetworkFailureIsPermanentToAvoidDuplicateCharge()
    {
        var handler = new Handler(new HttpRequestException("secret-bearing details"));
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.False(result.Retryable);
        Assert.Equal("twilio_ambiguous_failure", result.ErrorCode);
    }

    [Fact]
    public async Task CancellationIsPermanentToAvoidDispatcherTimeoutReplay()
    {
        var handler = new Handler(new OperationCanceledException("delivery timeout"));
        using var adapter = new TwilioSmsChannelAdapter(new HttpClient(handler));
        var result = await adapter.DeliverAsync(MakeDelivery(), CancellationToken.None);
        Assert.False(result.Retryable);
        Assert.Equal("twilio_ambiguous_failure", result.ErrorCode);
    }

    private static string Config(
        string credentialMode = "api_key",
        string senderType = "phone",
        bool paidSendConsent = true,
        string minimumPriority = "critical",
        int validityPeriod = 300) =>
        JsonSerializer.Serialize(new
        {
            accountSidSecretName = "account_sid",
            credentialMode,
            credentialSidSecretName = "credential_sid",
            credentialSecretName = "credential_secret",
            recipientSecretName = "recipient",
            senderType,
            senderSecretName = "sender",
            paidSendConsent,
            minimumPriority,
            validityPeriodSeconds = validityPeriod
        });

    private static OutboundDelivery MakeDelivery(
        string payload = "{\"title\":\"Build failed\",\"message\":\"Compiler failed\",\"priority\":\"critical\"}",
        string? config = null,
        string accountSid = AccountSid,
        string credentialSid = ApiKeySid,
        string credentialSecret = CredentialSecret,
        string recipient = "+15551234567",
        string sender = "+15557654321",
        bool includeCredentialSid = true,
        string notificationId = "notification")
    {
        var secrets = new Dictionary<string, string>
        {
            ["account_sid"] = accountSid,
            ["credential_secret"] = credentialSecret,
            ["recipient"] = recipient,
            ["sender"] = sender
        };
        if (includeCredentialSid) secrets["credential_sid"] = credentialSid;
        return new OutboundDelivery(
            "twilio-outbox",
            notificationId,
            payload,
            new ProviderProfile
            {
                Id = "twilio",
                Name = "Twilio SMS",
                Kind = "twilio_sms",
                Enabled = true,
                ConfigJson = config ?? Config()
            },
            secrets);
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

    private static int CountGsmSeptets(string value) =>
        value.Sum(character => "^{}\\[~]|€\f".Contains(character, StringComparison.Ordinal) ? 2 : 1);

    private static bool HasValidSurrogates(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }

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
