using System.Net;
using System.Text.Json;
using AgentNotify.Core.Delivery.Channels;

namespace AgentNotify.Tests;

public sealed class RelayPairingTests
{
    private const string PollToken = "pol_x9F_example_poll_credential";
    private const string InstallationToken = "inst_MivWSeeSbQhV1dS2mce82UQUmXoXg9oMWRRoIWN0nvI";
    private static readonly Uri BaseUri = new("https://relay.example.com/");

    [Fact]
    public async Task BeginSendsSenderMetadata()
    {
        var handler = new PairingHandler(_ => Json(HttpStatusCode.Created, BeginResponse()));
        using var client = new RelayPairingClient(new HttpClient(handler));

        var result = await client.BeginAsync(BaseUri, "ThinkPad", "windows", "1.0.0", default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://relay.example.com/v1/pairing/sender", request.Uri.AbsoluteUri);
        Assert.Null(request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("ThinkPad", body.RootElement.GetProperty("sender_name").GetString());
        Assert.Equal("windows", body.RootElement.GetProperty("platform").GetString());
        Assert.Equal("1.0.0", body.RootElement.GetProperty("client_version").GetString());
        Assert.Equal("W899-SUX9", result.UserCode);
    }

    [Fact]
    public async Task PollSendsBearerPollToken()
    {
        var handler = new PairingHandler(_ => Json(HttpStatusCode.OK, "{\"status\":\"pending\"}"));
        using var client = new RelayPairingClient(new HttpClient(handler));

        var result = await client.PollAsync(BaseUri, "a1b2c3d4e5f60718", PollToken, default);

        Assert.Equal("pending", result.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer " + PollToken, request.Authorization);
        Assert.DoesNotContain(PollToken, request.Uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PollPendingThenApprovedReturnsToken()
    {
        var handler = Sequence(
            Json(HttpStatusCode.OK, "{\"status\":\"pending\"}"),
            Json(HttpStatusCode.OK, ApprovedResponse()));
        var clock = new TestClock();
        using var client = CreateFastClient(handler, clock);

        var result = await client.WaitForApprovalAsync(BaseUri, PairingRequest(), null, default);

        Assert.Equal("approved", result.Status);
        Assert.Equal(InstallationToken, result.InstallationToken);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("expired")]
    public async Task TerminalStateStopsPolling(string status)
    {
        var handler = new PairingHandler(_ => Json(HttpStatusCode.OK, $"{{\"status\":\"{status}\"}}"));
        var clock = new TestClock();
        using var client = CreateFastClient(handler, clock);

        var result = await client.WaitForApprovalAsync(BaseUri, PairingRequest(), null, default);

        Assert.Equal(status, result.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PollConsumedIsTerminal()
    {
        var handler = new PairingHandler(_ => Json(HttpStatusCode.OK, "{\"status\":\"consumed\"}"));
        var clock = new TestClock();
        using var client = CreateFastClient(handler, clock);

        var exception = await Assert.ThrowsAsync<RelayPairingException>(() =>
            client.WaitForApprovalAsync(BaseUri, PairingRequest(), null, default));

        Assert.Equal("consumed", exception.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SlowDownIncreasesInterval()
    {
        var handler = Sequence(
            Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"slow_down\",\"retry_after_ms\":6000}}"),
            Json(HttpStatusCode.OK, ApprovedResponse()));
        var clock = new TestClock();
        using var client = CreateFastClient(handler, clock);

        var result = await client.WaitForApprovalAsync(BaseUri, PairingRequest(interval: 2), null, default);

        Assert.Equal("approved", result.Status);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7)], clock.Delays);
    }

    [Fact]
    public async Task TransientNetworkErrorsAreToleratedThenGiveUp()
    {
        var recoverHandler = new PairingHandler(requestNumber =>
        {
            if (requestNumber <= 4)
                throw new HttpRequestException($"synthetic {PollToken} {InstallationToken}");
            return Json(HttpStatusCode.OK, ApprovedResponse());
        });
        var recoverClock = new TestClock();
        using (var recovering = CreateFastClient(recoverHandler, recoverClock))
        {
            var result = await recovering.WaitForApprovalAsync(BaseUri, PairingRequest(), null, default);
            Assert.Equal("approved", result.Status);
            Assert.Equal(5, recoverHandler.Requests.Count);
        }

        var failHandler = new PairingHandler(_ =>
            throw new HttpRequestException($"synthetic {PollToken} {InstallationToken}"));
        var failClock = new TestClock();
        using var failing = CreateFastClient(failHandler, failClock);
        var exception = await Assert.ThrowsAsync<RelayPairingException>(() =>
            failing.WaitForApprovalAsync(BaseUri, PairingRequest(), null, default));
        Assert.Equal("network_error", exception.Code);
        Assert.Equal(5, failHandler.Requests.Count);
        Assert.DoesNotContain(PollToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallationToken, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsCrossOriginVerificationUri()
    {
        var body = BeginResponse(verificationComplete: "https://evil.example/pair?code=W899-SUX9");
        var handler = new PairingHandler(_ => Json(HttpStatusCode.Created, body));
        using var client = new RelayPairingClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<RelayPairingException>(() =>
            client.BeginAsync(BaseUri, "ThinkPad", "windows", "1.0.0", default));

        Assert.Equal("unexpected_verification_uri", exception.Code);
        Assert.Equal("The relay returned an unexpected verification URL.", exception.Message);
    }

    [Fact]
    public void RejectsNonHttpsRelayUrl()
    {
        Assert.Throws<ArgumentException>(() =>
            RelayChannelAdapter.ValidateRelayUrl("http://relay.example.com", allowPrivate: false));
        Assert.Throws<ArgumentException>(() =>
            RelayChannelAdapter.ValidateRelayUrl("http://localhost:4000", allowPrivate: false));
        var localhost = RelayChannelAdapter.ValidateRelayUrl("http://localhost:4000", allowPrivate: true);
        Assert.Equal("http://localhost:4000/", localhost.AbsoluteUri);
    }

    [Fact]
    public async Task TokenNeverAppearsInThrownMessages()
    {
        var handler = new PairingHandler(_ =>
            throw new HttpRequestException($"leaked {PollToken} and {InstallationToken}"));
        using var client = new RelayPairingClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<RelayPairingException>(() =>
            client.PollAsync(BaseUri, "a1b2c3d4e5f60718", PollToken, default));

        Assert.DoesNotContain(PollToken, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(InstallationToken, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationStopsPolling()
    {
        var handler = new PairingHandler(_ => Json(HttpStatusCode.OK, "{\"status\":\"pending\"}"));
        using var cts = new CancellationTokenSource();
        using var client = new RelayPairingClient(
            new HttpClient(handler),
            allowPrivateNetwork: false,
            async (_, cancellationToken) =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            static () => DateTimeOffset.UtcNow);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.WaitForApprovalAsync(BaseUri, PairingRequest(), null, cts.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DiscoverAndVerifyValidateResponses()
    {
        var handler = Sequence(
            Json(HttpStatusCode.OK, "{\"service\":\"agentnotify-relay\",\"api_versions\":[\"v1\"],\"envelope_versions\":[\"1\"]}"),
            Json(HttpStatusCode.OK, "{\"installation_id\":\"installation-1\",\"display_name\":\"Home relay\",\"created_at\":\"2026-08-30T00:00:00Z\",\"last_seen_at\":null}"));
        using var client = new RelayPairingClient(new HttpClient(handler));

        var discovery = await client.DiscoverAsync(BaseUri, default);
        var installation = await client.VerifyAsync(BaseUri, InstallationToken, default);

        Assert.Contains("v1", discovery.ApiVersions);
        Assert.Equal("Home relay", installation.DisplayName);
        Assert.Equal("Bearer " + InstallationToken, handler.Requests[1].Authorization);
    }

    private static RelayPairingClient CreateFastClient(PairingHandler handler, TestClock clock) =>
        new(
            new HttpClient(handler),
            allowPrivateNetwork: false,
            clock.DelayAsync,
            () => clock.Now);

    private static RelayPairingRequest PairingRequest(int interval = 1) =>
        new(
            "a1b2c3d4e5f60718",
            "W899-SUX9",
            new Uri("https://relay.example.com/pair"),
            new Uri("https://relay.example.com/pair?code=W899-SUX9"),
            PollToken,
            600,
            interval);

    private static PairingHandler Sequence(params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>(responses);
        return new PairingHandler(_ => queue.Dequeue());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body) };

    private static string BeginResponse(string verificationComplete = "https://relay.example.com/pair?code=W899-SUX9") =>
        $$"""
        {
          "pairing_id":"a1b2c3d4e5f60718",
          "user_code":"W899-SUX9",
          "verification_uri":"https://relay.example.com/pair",
          "verification_uri_complete":"{{verificationComplete}}",
          "poll_token":"{{PollToken}}",
          "expires_in":600,
          "interval":5
        }
        """;

    private static string ApprovedResponse() =>
        $$"""{"status":"approved","installation_id":"installation-1","installation_token":"{{InstallationToken}}","relay_name":"Home relay"}""";

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 8, 30, 0, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            Now += delay;
            return Task.CompletedTask;
        }
    }

    private sealed class PairingHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _response;

        public PairingHandler(Func<int, HttpResponseMessage> response) => _response = response;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                body));
            return _response(Requests.Count);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);
}
