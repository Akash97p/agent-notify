using System.Net;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Quota;
using AgentNotify.Core.Config;

namespace AgentNotify.Tests;

public sealed class LiveQuotaTests
{
    [Fact]
    public async Task NamedAccountsHaveIndependentCacheAndDisappearWhenRemoved()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var primary = new FakeProbe { Scope = "primary" };
        var extra = new FakeProbe { Scope = "secondary" };
        var account = new QuotaAccountDefinition("q_" + Guid.NewGuid().ToString("N"), "codex", "Second",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-second"));
        var accounts = new List<QuotaAccountDefinition> { account };
        var service = new LiveQuotaService([primary], clock, () => accounts.ToArray(), probeFactory: _ => extra,
            defaultAccountLabel: _ => "Primary");

        var first = await service.GetReportAsync();
        Assert.Equal(2, first.Providers.Count);
        Assert.Equal("codex:default", first.Providers[0].AccountId);
        Assert.Equal("Primary", first.Providers[0].AccountLabel);
        Assert.Equal(account.Id, first.Providers[1].AccountId);
        Assert.Equal("Second", first.Providers[1].AccountLabel);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, extra.Calls);
        await service.GetReportAsync();
        Assert.Equal(1, extra.Calls);

        extra.Fails = true;
        clock.Advance(TimeSpan.FromSeconds(31));
        var refreshed = await service.GetReportAsync(refresh: true);
        Assert.Equal("ok", refreshed.Providers[0].Status);
        Assert.Equal("stale", refreshed.Providers[1].Status);

        accounts.Clear();
        Assert.Single((await service.GetReportAsync()).Providers);
        accounts.Add(account);
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal("unavailable", (await service.GetReportAsync()).Providers[1].Status);
    }

    [Fact]
    public void MenuBarProjectionUsesLowestSelectedFiveHourBalanceAndListsEveryAccount()
    {
        var now = DateTimeOffset.Parse("2026-09-19T10:00:00Z");
        var codex = new LiveQuotaSnapshot("codex", "ok", "fixture", now, "pro", null,
            [new LiveQuotaWindow("primary", "Codex · 5-hour", 35, 65, 300, now.AddHours(2)),
             new LiveQuotaWindow("weekly", "Codex · 7-day", 10, 90, 10080, now.AddDays(2))], null,
            "codex:default", "Personal Codex");
        var claude = new LiveQuotaSnapshot("claude_code", "stale", "fixture", now, null, null,
            [new LiveQuotaWindow("five_hour", "5-hour", 62, 38, 300, now.AddHours(1))], "temporarily unavailable",
            "claude_code:default", "Work Claude");
        var report = new LiveQuotaReport(now, [codex, claude], null);

        var all = MenuBarQuotaProjector.Project(report, new MacMenuBarSettings());
        Assert.Equal("1", all.ContractVersion);
        Assert.Equal(2, all.Accounts.Count);
        Assert.Equal("claude_code:default", all.Headline!.AccountId);
        Assert.Equal(38, all.Headline.RemainingPercent);
        Assert.True(all.Headline.Stale);

        var selected = MenuBarQuotaProjector.Project(report, new MacMenuBarSettings
        {
            AccountIds = ["codex:default"]
        });
        Assert.Equal("codex:default", selected.Headline!.AccountId);
        Assert.Equal(65, selected.Headline.RemainingPercent);
        Assert.Equal(2, selected.Accounts.Count);
    }

    [Fact]
    public void AccountProfileRequiresUniqueDirectoryUnderHome()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-other");
        var account = QuotaAccountDefinition.Create("codex", " Other ", directory, []);
        Assert.Equal("Other", account.Label);
        Assert.Throws<ArgumentException>(() => QuotaAccountDefinition.Create("codex", "Again", directory, [account]));
        Assert.Throws<ArgumentException>(() => QuotaAccountDefinition.Create("codex", "Relative", "not-absolute", []));
    }

    [Fact]
    public void ConfigDefaultsDiscardMalformedAndDuplicateAccountProfiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directory = Path.Combine(home, ".codex-other");
        var valid = new QuotaAccountDefinition("q_" + Guid.NewGuid().ToString("N"), "codex", " Other ", directory);
        var config = new AgentNotifyConfig
        {
            DefaultQuotaAccountLabels = new()
            {
                ["codex"] = " Primary ",
                ["claude_code"] = "\0invalid",
                ["unknown"] = "Discard"
            },
            QuotaAccounts =
            [
                valid,
                valid with { Id = "bad", Label = "Broken" },
                valid with { Id = "q_" + Guid.NewGuid().ToString("N"), Label = "Duplicate" },
                valid with { Id = "q_" + Guid.NewGuid().ToString("N"), Directory = Path.GetPathRoot(home)! }
            ]
        };

        config.ApplyDefaults();

        var account = Assert.Single(config.QuotaAccounts);
        Assert.Equal(valid.Id, account.Id);
        Assert.Equal("Other", account.Label);
        Assert.Equal(Path.GetFullPath(directory), account.Directory);
        Assert.Equal("Primary", Assert.Single(config.DefaultQuotaAccountLabels).Value);
    }

    [Fact]
    public void CodexParserKeepsBucketsWindowsAndCreditsSeparate()
    {
        using var json = JsonDocument.Parse("""
            {"rateLimits":{"limitId":"codex","primary":{"usedPercent":99}},
             "rateLimitsByLimitId":{
               "codex":{"limitId":"codex","planType":"pro","credits":{"hasCredits":true,"unlimited":false,"balance":3.5},
                 "primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1789329600},
                 "secondary":{"usedPercent":40,"windowDurationMins":10080,"resetsAt":1789934400}},
               "codex_other":{"limitName":"Other models","primary":{"usedPercent":0,"windowDurationMins":60}}}}
            """);

        var snapshot = CodexQuotaProbe.Parse(json.RootElement, DateTimeOffset.UtcNow);

        Assert.Equal("ok", snapshot.Status);
        Assert.Equal("pro", snapshot.Plan);
        Assert.Equal(3.5m, snapshot.CreditBalance);
        Assert.Equal(3, snapshot.Windows.Count);
        Assert.Contains(snapshot.Windows, window => window.Key == "codex:primary" && window.UsedPercent == 25 && window.RemainingPercent == 75);
        Assert.Contains(snapshot.Windows, window => window.Key == "codex:secondary" && window.DurationMinutes == 10080);
        Assert.Contains(snapshot.Windows, window => window.Key == "codex_other:primary" && window.UsedPercent == 0);
        Assert.DoesNotContain(snapshot.Windows, window => window.UsedPercent == 99);
    }

    [Fact]
    public void ClaudeParserKeepsMissingWindowsUnknown()
    {
        using var json = JsonDocument.Parse("""
            {"five_hour":{"utilization":23.5,"resets_at":"2026-09-13T18:00:00Z"},
             "seven_day":null,"seven_day_opus":{"utilization":68,"resets_at":"2026-09-18T00:00:00Z"},
             "seven_day_sonnet":{"utilization":-1}}
            """);

        var snapshot = ClaudeQuotaProbe.Parse(json.RootElement, DateTimeOffset.UtcNow);

        Assert.Equal(2, snapshot.Windows.Count);
        Assert.Contains(snapshot.Windows, window => window.Key == "five_hour" && window.UsedPercent == 23.5 && window.ResetsAt is not null);
        Assert.Contains(snapshot.Windows, window => window.Key == "seven_day_opus" && window.UsedPercent == 68);
        Assert.DoesNotContain(snapshot.Windows, window => window.Key == "seven_day");
    }

    [Fact]
    public async Task RefreshCachesCoalescesAndNeverCarriesOldAccountAcrossCredentialChange()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var probe = new FakeProbe();
        var service = new LiveQuotaService([probe], clock);

        var first = await service.GetReportAsync();
        Assert.Equal(1, probe.Calls);
        Assert.Equal("ok", first.Providers[0].Status);
        await Task.WhenAll(service.GetReportAsync(), service.GetReportAsync());
        Assert.Equal(1, probe.Calls);

        probe.Fails = true;
        Assert.Equal("ok", (await service.GetReportAsync(refresh: true)).Providers[0].Status); // 30-second gate
        clock.Advance(TimeSpan.FromSeconds(31));
        var stale = (await service.GetReportAsync(refresh: true)).Providers[0];
        Assert.Equal("stale", stale.Status);
        Assert.Equal(first.Providers[0].FetchedAt, stale.FetchedAt);
        Assert.Single(stale.Windows);

        probe.AuthRequired = true;
        clock.Advance(TimeSpan.FromSeconds(31));
        var auth = (await service.GetReportAsync(refresh: true)).Providers[0];
        Assert.Equal("auth_required", auth.Status);
        Assert.Empty(auth.Windows);

        probe.AuthRequired = false;
        probe.Scope = "another-account";
        var switched = (await service.GetReportAsync()).Providers[0];
        Assert.Equal("unavailable", switched.Status);
        Assert.Empty(switched.Windows);
    }

    [Fact]
    public async Task ClaudeProbeUsesOnlyFixedFirstPartyEndpointAndReturnsNoCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "an-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".credentials.json");
        const string marker = "synthetic-secret-do-not-return";
        File.WriteAllText(path, JsonSerializer.Serialize(new { claudeAiOauth = new
        {
            accessToken = marker, expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
        } }));
        try
        {
            var handler = new StubHandler(request =>
            {
                Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.ToString());
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal(marker, request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"five_hour\":{\"utilization\":12,\"resets_at\":\"2026-09-13T18:00:00Z\"}}", Encoding.UTF8, "application/json")
                };
            });
            var probe = new ClaudeQuotaProbe(path, new HttpClient(handler));
            var snapshot = await probe.FetchAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            Assert.Equal("ok", snapshot.Status);
            Assert.DoesNotContain(marker, JsonSerializer.Serialize(snapshot));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class FakeProbe : ILiveQuotaProbe
    {
        public string Provider => "codex";
        public string Scope { get; set; } = "first-account";
        public int Calls { get; private set; }
        public bool Fails { get; set; }
        public bool AuthRequired { get; set; }
        public string ScopeKey() => Scope;
        public Task<LiveQuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(AuthRequired ? LiveQuotaSnapshot.Unavailable(Provider, "sign in", now, "auth_required")
                : Fails ? LiveQuotaSnapshot.Unavailable(Provider, "probe failed", now)
                : new LiveQuotaSnapshot(Provider, "ok", "fixture", now, "pro", null,
                    [new LiveQuotaWindow("session", "Session", 20, 80, 300, now.AddHours(5))], null));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
