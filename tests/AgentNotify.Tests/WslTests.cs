using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Insights.Quota;
using AgentNotify.Core.Skills;
using AgentNotify.Insights.Usage;
using AgentNotify.Core.Wsl;

namespace AgentNotify.Tests;

public sealed class WslTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"an-wsl-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu-20.04\home\akash\.codex", "Ubuntu-20.04", "/home/akash/.codex")]
    [InlineData(@"\\wsl$\Ubuntu\home\akash\", "Ubuntu", "/home/akash")]
    [InlineData("//WSL.LOCALHOST/Debian/root/.claude", "Debian", "/root/.claude")]
    [InlineData(@"\\wsl.localhost\Ubuntu", "Ubuntu", "/")]
    public void WslPathReadsShareIntoDistributionAndLinuxPath(string windows, string distribution, string linux)
    {
        Assert.True(WslPath.TryParse(windows, out var parsedDistribution, out var parsedLinux));
        Assert.Equal(distribution, parsedDistribution);
        Assert.Equal(linux, parsedLinux);
    }

    [Theory]
    [InlineData(@"C:\Users\akash\.codex")]
    [InlineData(@"\\server\share\home")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\..\etc")]
    [InlineData(@"\\wsl.localhost\bad name\home")]
    [InlineData("/home/akash/.codex")]
    [InlineData("")]
    public void WslPathRejectsEverythingElse(string windows) =>
        Assert.False(WslPath.TryParse(windows, out _, out _));

    [Fact]
    public void RunningListDecodesUtf8AndBothUtf16Forms()
    {
        const string listing = "Ubuntu-20.04\r\ndocker-desktop\r\n\r\n";
        var utf16 = Encoding.Unicode.GetBytes(listing);
        var withBom = new byte[] { 0xFF, 0xFE }.Concat(utf16).ToArray();

        foreach (var output in new[] { Encoding.UTF8.GetBytes(listing), utf16, withBom })
            Assert.Equal(["Ubuntu-20.04", "docker-desktop"], WslDiscovery.ParseRunningList(output));
        Assert.Empty(WslDiscovery.ParseRunningList([]));
    }

    [Fact]
    public void PasswdLookupFindsTheDefaultUsersHome()
    {
        const string passwd = "root:x:0:0:root:/root:/bin/bash\nakash:x:1000:1000:,,,:/home/akash/:/usr/bin/zsh\r\nevil:x:1001:1001::/home/../etc:/bin/sh\n";
        Assert.Equal("/root", WslDiscovery.FindHome(passwd, 0));
        Assert.Equal("/home/akash", WslDiscovery.FindHome(passwd, 1000));
        Assert.Null(WslDiscovery.FindHome(passwd, 1001));
        Assert.Null(WslDiscovery.FindHome(passwd, 4242));
        Assert.Equal("/home/akash", WslDiscovery.FindHome(passwd, "akash"));
        Assert.Null(WslDiscovery.FindHome(passwd, "evil"));
        Assert.Null(WslDiscovery.FindHome(passwd, "nobody"));
    }

    [Theory]
    [InlineData("[user]\ndefault=akash\n", "akash")]
    [InlineData("[boot]\nsystemd=true\n\n[user]\r\ndefault = \"akash\"  # login user\r\n", "akash")]
    [InlineData("[User]\n  Default=dev_1\n[network]\ndefault=ignored\n", "dev_1")]
    [InlineData("[boot]\ndefault=akash\n", null)]
    [InlineData("# [user]\n# default=akash\n", null)]
    [InlineData("[user]\ndefault=../etc\n", null)]
    [InlineData("[user]\ndefault=\n", null)]
    [InlineData("", null)]
    public void WslConfNamesTheDefaultUser(string wslConf, string? user) =>
        Assert.Equal(user, WslDiscovery.DefaultUser(wslConf));

    [Fact]
    public void NativeFindListingBecomesSharePathsWithSizesAndTimes()
    {
        var output = Encoding.UTF8.GetBytes(
            "120\t1786382251.5000000000\t/home/tester/.codex/sessions/a b.jsonl\0" +
            "7\t0.0000000000\t/home/tester/tab\tname.jsonl\0" +
            "x\t1.0\t/home/tester/bad-size.jsonl\0" +
            "9\t1.0\t/home/tester/../etc/passwd\0" +
            "9\t1.0\trelative.jsonl\0");

        var files = WslFileListing.Parse(output, @"\\wsl.localhost\Ubuntu-Test");

        // Paths with control characters, unparsable sizes, ".." segments, or no leading slash are dropped.
        var file = Assert.Single(files);
        Assert.Equal(@"\\wsl.localhost\Ubuntu-Test\home\tester\.codex\sessions\a b.jsonl", file.WindowsPath);
        Assert.Equal(120, file.Length);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1786382251.5), file.ModifiedUtc);
        Assert.Null(WslFileListing.TryList(@"C:\Users\tester\.codex", "*.jsonl", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task UsageIncludesRunningDistributionsAndTheirLinuxProjectNames()
    {
        var home = Home();
        var projects = Path.Combine(home.WindowsHome, ".claude", "projects", "-home-tester-myproj");
        Directory.CreateDirectory(projects);
        File.WriteAllText(Path.Combine(projects, "session.jsonl"), JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = DateTimeOffset.UtcNow, sessionId = "s1", cwd = "/home/tester/myproj",
            requestId = "r1", message = new { id = "m1", model = "claude-opus-5", usage = new { input_tokens = 10 } }
        }) + "\n");
        var wsl = new FakeWsl(home);
        var usage = new LocalUsageService([], [], Path.Combine(_root, "missing.db"), wsl);

        var report = await usage.GetReportAsync(7);

        Assert.Equal("4", report.ContractVersion);
        Assert.Equal(1, report.Events);
        Assert.Equal("myproj", Assert.Single(report.Projects).Name);
        Assert.Equal(["Ubuntu-Test"], report.WslDistributions);

        wsl.Homes.Clear();
        var stopped = await usage.GetReportAsync(7);
        Assert.Equal(0, stopped.Events);
        Assert.Empty(stopped.WslDistributions);
    }

    [Fact]
    public async Task LiveQuotaListsWslProfilesThatExistUnlessAddedByHand()
    {
        var home = Home();
        var wsl = new FakeWsl(home);
        var accounts = new List<QuotaAccountDefinition>();
        var labels = new Dictionary<string, string>();
        var service = new LiveQuotaService([new StaticProbe()], accounts: () => accounts.ToArray(),
            probeFactory: _ => new StaticProbe(), defaultAccountLabel: key => labels.GetValueOrDefault(key), wsl: wsl,
            nativeHome: Path.Combine(_root, "native-home"));

        Assert.Equal(["codex:default"], (await service.GetReportAsync()).Providers.Select(p => p.AccountId));

        var codexHome = Path.Combine(home.WindowsHome, ".codex");
        Directory.CreateDirectory(codexHome);
        var found = (await service.GetReportAsync()).Providers;
        Assert.Equal(["codex:default", "codex:wsl:Ubuntu-Test"], found.Select(p => p.AccountId));
        Assert.Equal("WSL · Ubuntu-Test", found[1].AccountLabel);

        labels["codex:wsl:Ubuntu-Test"] = "Linux Codex";
        Assert.Equal("Linux Codex", (await service.GetReportAsync()).Providers[1].AccountLabel);

        accounts.Add(new QuotaAccountDefinition("q_" + Guid.NewGuid().ToString("N"), "codex", "By hand", codexHome));
        Assert.Equal(["codex:default", accounts[0].Id], (await service.GetReportAsync()).Providers.Select(p => p.AccountId));

        accounts.Clear();
        wsl.Homes.Clear();
        Assert.Equal(["codex:default"], (await service.GetReportAsync()).Providers.Select(p => p.AccountId));
    }

    [Fact]
    public async Task LiveQuotaLeavesOutRemovedBuiltInAndDiscoveredAccounts()
    {
        var home = Home();
        Directory.CreateDirectory(Path.Combine(home.WindowsHome, ".claude"));
        var removed = new List<string>();
        var service = new LiveQuotaService([new StaticProbe()], probeFactory: _ => new StaticProbe(),
            wsl: new FakeWsl(home), removedAccounts: () => removed, nativeHome: Path.Combine(_root, "native-home"));

        Assert.Equal(["codex:default", "claude_code:wsl:Ubuntu-Test"], (await service.GetReportAsync()).Providers.Select(p => p.AccountId));
        removed.AddRange(["codex:default", "claude_code:wsl:Ubuntu-Test"]);
        Assert.Empty((await service.GetReportAsync()).Providers);
        removed.Clear();
        Assert.Equal(2, (await service.GetReportAsync()).Providers.Count);
    }

    [Fact]
    public async Task UsageReadsProfilesAddedByHandOnceEvenWhenDiscoveredToo()
    {
        var home = Home();
        var session = JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = DateTimeOffset.UtcNow, sessionId = "s1", cwd = "/home/tester/myproj",
            requestId = "r1", message = new { id = "m1", model = "claude-opus-5", usage = new { input_tokens = 10 } }
        }) + "\n";
        var discovered = Path.Combine(home.WindowsHome, ".claude", "projects", "-home-tester-myproj");
        Directory.CreateDirectory(discovered);
        File.WriteAllText(Path.Combine(discovered, "session.jsonl"), session);
        var second = Path.Combine(_root, "claude-second");
        Directory.CreateDirectory(Path.Combine(second, "projects", "p"));
        File.WriteAllText(Path.Combine(second, "projects", "p", "other.jsonl"), session.Replace("\"r1\"", "\"r2\"").Replace("\"m1\"", "\"m2\""));
        var accounts = new List<QuotaAccountDefinition>
        {
            new("q_" + Guid.NewGuid().ToString("N"), "claude_code", "Same as discovered", Path.Combine(home.WindowsHome, ".claude")),
            new("q_" + Guid.NewGuid().ToString("N"), "claude_code", "Second", second)
        };
        var usage = new LocalUsageService([], [], Path.Combine(_root, "missing.db"), new FakeWsl(home), () => accounts);

        Assert.Equal(2, (await usage.GetReportAsync(7)).Events);
        accounts.RemoveAt(1);
        Assert.Equal(1, (await usage.GetReportAsync(7)).Events);
    }

    [Fact]
    public void ConfigKeepsOnlyValidRemovedAccountIds()
    {
        var config = new AgentNotifyConfig
        {
            RemovedQuotaAccounts = ["codex:default", "codex:default", "claude_code:wsl:Ubuntu", "q_" + new string('a', 32),
                "opencode:default", "codex:wsl:bad name"]
        };

        config.ApplyDefaults();

        Assert.Equal(["codex:default", "claude_code:wsl:Ubuntu"], config.RemovedQuotaAccounts);
    }

    [Fact]
    public void ConfigKeepsLabelsForDiscoveredWslAccountsOnly()
    {
        var config = new AgentNotifyConfig
        {
            DefaultQuotaAccountLabels = new()
            {
                ["codex:wsl:Ubuntu-20.04"] = " Linux ",
                ["claude_code:wsl:bad name"] = "Discard",
                ["opencode:wsl:Ubuntu"] = "Discard"
            }
        };

        config.ApplyDefaults();

        var label = Assert.Single(config.DefaultQuotaAccountLabels);
        Assert.Equal("codex:wsl:Ubuntu-20.04", label.Key);
        Assert.Equal("Linux", label.Value);
    }

    [Fact]
    public void SecondaryProfilesNeedAMatchingDirectoryNameAndAMarker()
    {
        var home = Path.Combine(_root, "secondary-home");
        Directory.CreateDirectory(home);
        void Profile(string name, string? markerFile = null, string? markerDir = null)
        {
            var dir = Directory.CreateDirectory(Path.Combine(home, name));
            if (markerFile is not null) File.WriteAllText(Path.Combine(dir.FullName, markerFile), "{}");
            if (markerDir is not null) Directory.CreateDirectory(Path.Combine(dir.FullName, markerDir));
        }
        Profile(".codex-work", markerFile: "auth.json");
        Profile(".codex_lab", markerDir: "sessions");
        Profile(".claude-personal", markerFile: ".credentials.json");
        Profile(".claude_personal", markerDir: "projects");
        Profile(".codex-empty");
        Profile(".claude-empty");
        Profile(".codex", markerFile: "auth.json");
        Profile(".claude", markerFile: ".credentials.json");
        Profile(".codex-", markerFile: "auth.json");
        Profile(".codex-bad name", markerFile: "auth.json");
        Profile(".other-work", markerFile: "auth.json");
        File.WriteAllText(Path.Combine(home, ".claude.json"), "{}");
        File.WriteAllText(Path.Combine(home, ".codex-file"), "{}");
        var nested = Directory.CreateDirectory(Path.Combine(home, "sub", ".codex-deep"));
        File.WriteAllText(Path.Combine(nested.FullName, "auth.json"), "{}");

        var found = QuotaAccountDefinition.HomeSecondaryProfiles(home, _ => null);

        // `.claude-personal` and `.claude_personal` name the same account, so one entry wins.
        Assert.Equal(["claude_code:home:personal", "codex:home:lab", "codex:home:work"],
            found.Select(account => account.Id).Order(StringComparer.Ordinal));
        Assert.Equal("Profile · work", found.Single(account => account.Id == "codex:home:work").Label);
        Assert.Equal("codex", found.Single(account => account.Id == "codex:home:lab").Provider);
        Assert.Equal(Path.Combine(home, ".codex-work"),
            found.Single(account => account.Id == "codex:home:work").Directory);
    }

    [Fact]
    public void SecondaryProfileSuffixIsLimitedAndEachHomeIsCapped()
    {
        var home = Path.Combine(_root, "capped-home");
        Directory.CreateDirectory(home);
        var ok = new string('a', 40);
        var tooLong = new string('b', 41);
        Directory.CreateDirectory(Path.Combine(home, ".codex-" + ok));
        File.WriteAllText(Path.Combine(home, ".codex-" + ok, "auth.json"), "{}");
        Directory.CreateDirectory(Path.Combine(home, ".codex-" + tooLong));
        File.WriteAllText(Path.Combine(home, ".codex-" + tooLong, "auth.json"), "{}");
        for (var i = 0; i < 17; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(home, $".codex-p{i:00}"));
            File.WriteAllText(Path.Combine(dir.FullName, "auth.json"), "{}");
        }

        var found = QuotaAccountDefinition.HomeSecondaryProfiles(home, _ => null);

        Assert.DoesNotContain(found, account => account.Id.Contains(tooLong));
        Assert.Contains(found, account => account.Id == "codex:home:" + ok);
        Assert.Equal(16, found.Count);
        Assert.DoesNotContain("codex:home:p16", found.Select(account => account.Id));
    }

    [Theory]
    [InlineData("codex:default", true)]
    [InlineData("claude_code:default", true)]
    [InlineData("codex:wsl:Ubuntu", true)]
    [InlineData("claude_code:wsl:Ubuntu-20.04", true)]
    [InlineData("codex:home:work", true)]
    [InlineData("claude_code:home:a.b_c-d", true)]
    [InlineData("codex:wsl:Ubuntu:work", true)]
    [InlineData("claude_code:wsl:Debian:personal", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("codex", false)]
    [InlineData("opencode:default", false)]
    [InlineData("codex:wsl:bad name", false)]
    [InlineData("codex:wsl:", false)]
    [InlineData("codex:wsl:Ubuntu:bad name", false)]
    [InlineData("codex:wsl:Ubuntu:work:extra", false)]
    [InlineData("codex:home:", false)]
    [InlineData("codex:home:bad name", false)]
    [InlineData("codex:home:a:b", false)]
    [InlineData("codex:other:work", false)]
    public void DetectedAccountIdsAreValidatedStrictly(string? id, bool detected) =>
        Assert.Equal(detected, QuotaAccountDefinition.IsDetectedAccountId(id));

    [Fact]
    public void SecondaryAccountIdHelpersParseProviderDistributionAndSuffix()
    {
        Assert.True(QuotaAccountDefinition.IsHomeAccountId("codex:home:work"));
        Assert.False(QuotaAccountDefinition.IsHomeAccountId("codex:wsl:Ubuntu:work"));
        Assert.True(QuotaAccountDefinition.IsWslSecondaryAccountId("codex:wsl:Ubuntu:work"));
        Assert.False(QuotaAccountDefinition.IsWslSecondaryAccountId("codex:wsl:Ubuntu"));
        Assert.True(QuotaAccountDefinition.IsSecondaryAccountId("claude_code:home:me"));
        Assert.False(QuotaAccountDefinition.IsSecondaryAccountId("claude_code:default"));

        Assert.Equal("Ubuntu", QuotaAccountDefinition.DetectedWslDistribution("codex:wsl:Ubuntu"));
        Assert.Equal("Ubuntu", QuotaAccountDefinition.DetectedWslDistribution("codex:wsl:Ubuntu:work"));
        Assert.Null(QuotaAccountDefinition.DetectedWslDistribution("codex:home:work"));
        Assert.Null(QuotaAccountDefinition.DetectedWslDistribution("codex:default"));
        Assert.Null(QuotaAccountDefinition.DetectedWslDistribution("codex:wsl:bad name:work"));

        Assert.Equal("work", QuotaAccountDefinition.DetectedSuffix("codex:home:work"));
        Assert.Equal("lab", QuotaAccountDefinition.DetectedSuffix("codex:wsl:Ubuntu:lab"));
        Assert.Null(QuotaAccountDefinition.DetectedSuffix("codex:wsl:Ubuntu"));
        Assert.Null(QuotaAccountDefinition.DetectedSuffix("codex:default"));

        Assert.Equal("codex", QuotaAccountDefinition.DetectedProvider("codex:home:work"));
        Assert.Equal("claude_code", QuotaAccountDefinition.DetectedProvider("claude_code:wsl:Debian:me"));
        Assert.Null(QuotaAccountDefinition.DetectedProvider("opencode:default"));

        Assert.Equal("WSL · Ubuntu · work", QuotaAccountDefinition.FallbackLabel("codex:wsl:Ubuntu:work"));
        Assert.Equal("WSL · Ubuntu", QuotaAccountDefinition.FallbackLabel("codex:wsl:Ubuntu"));
        Assert.Equal("Profile · me", QuotaAccountDefinition.FallbackLabel("claude_code:home:me"));
    }

    [Fact]
    public void SecondaryProfilesUseOwnerLabelsWhenGiven()
    {
        var home = Path.Combine(_root, "labeled-home");
        var work = Directory.CreateDirectory(Path.Combine(home, ".codex-work"));
        File.WriteAllText(Path.Combine(work.FullName, "auth.json"), "{}");
        var labels = new Dictionary<string, string> { ["codex:home:work"] = " Day job " };

        var found = QuotaAccountDefinition.HomeSecondaryProfiles(home, key => labels.GetValueOrDefault(key));

        Assert.Equal("Day job", Assert.Single(found).Label);
    }

    [Fact]
    public void ConfigKeepsSecondaryAccountIds()
    {
        var config = new AgentNotifyConfig
        {
            DefaultQuotaAccountLabels = new()
            {
                ["codex:home:work"] = " Work ",
                ["claude_code:wsl:Ubuntu:personal"] = "Personal",
                ["codex:wsl:Ubuntu"] = "Kept",
                ["codex:default"] = "Dropped",
                ["opencode:home:work"] = "Dropped",
                ["codex:wsl:bad name"] = "Dropped"
            },
            RemovedQuotaAccounts = ["codex:home:work", "claude_code:wsl:Ubuntu:personal", "codex:wsl:bad name"]
        };

        config.ApplyDefaults();

        Assert.Equal(3, config.DefaultQuotaAccountLabels.Count);
        Assert.Equal("Work", config.DefaultQuotaAccountLabels["codex:home:work"]);
        Assert.Equal("Personal", config.DefaultQuotaAccountLabels["claude_code:wsl:Ubuntu:personal"]);
        Assert.Equal(["claude_code:wsl:Ubuntu:personal", "codex:home:work"],
            config.RemovedQuotaAccounts.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task LiveQuotaOrdersSecondaryProfilesAndSkipsDuplicatesAndRemoved()
    {
        var native = Path.Combine(_root, "order-home");
        Directory.CreateDirectory(native);
        var work = Directory.CreateDirectory(Path.Combine(native, ".codex-work"));
        File.WriteAllText(Path.Combine(work.FullName, "auth.json"), "{}");
        var me = Directory.CreateDirectory(Path.Combine(native, ".claude-me"));
        File.WriteAllText(Path.Combine(me.FullName, ".credentials.json"), "{}");
        var wslHome = Home();
        Directory.CreateDirectory(Path.Combine(wslHome.WindowsHome, ".codex"));
        Directory.CreateDirectory(Path.Combine(wslHome.WindowsHome, ".claude"));
        var lab = Directory.CreateDirectory(Path.Combine(wslHome.WindowsHome, ".codex-lab"));
        Directory.CreateDirectory(Path.Combine(lab.FullName, "sessions"));
        var accounts = new List<QuotaAccountDefinition>
        {
            // The same directory by hand takes the place of the discovered secondary profile.
            new("q_" + Guid.NewGuid().ToString("N"), "codex", "By hand", work.FullName),
            new("q_" + Guid.NewGuid().ToString("N"), "codex", "Other", Path.Combine(native, ".codex-other"))
        };
        var removed = new List<string>();
        var service = new LiveQuotaService([new StaticProbe()], accounts: () => accounts.ToArray(),
            probeFactory: _ => new StaticProbe(), wsl: new FakeWsl(wslHome),
            removedAccounts: () => removed, nativeHome: native);

        var ids = (await service.GetReportAsync()).Providers.Select(p => p.AccountId).ToArray();
        // The fixture probes only Codex, so there is no claude_code:default built-in here.
        Assert.Equal([
            "codex:default",
            "claude_code:home:me",
            "codex:wsl:Ubuntu-Test", "claude_code:wsl:Ubuntu-Test", "codex:wsl:Ubuntu-Test:lab",
            accounts[0].Id, accounts[1].Id
        ], ids);

        removed.Add("claude_code:home:me");
        ids = (await service.GetReportAsync()).Providers.Select(p => p.AccountId).ToArray();
        Assert.DoesNotContain("claude_code:home:me", ids);
        Assert.Contains("codex:wsl:Ubuntu-Test:lab", ids);
    }

    [Fact]
    public async Task UsageCountsSecondaryProfileLedgersPassedAsAccounts()
    {
        var native = Path.Combine(_root, "usage-home");
        var projects = Directory.CreateDirectory(Path.Combine(native, ".claude-personal", "projects", "-home-x-proj"));
        File.WriteAllText(Path.Combine(projects.FullName, "session.jsonl"), JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = DateTimeOffset.UtcNow, sessionId = "s1", cwd = "/home/x/proj",
            requestId = "r1", message = new { id = "m1", model = "claude-opus-5", usage = new { input_tokens = 10 } }
        }) + "\n");
        var detected = QuotaAccountDefinition.HomeSecondaryProfiles(native, _ => null);
        Assert.Equal("claude_code:home:personal", Assert.Single(detected).Id);
        var usage = new LocalUsageService([], [], Path.Combine(_root, "secondary-usage.db"),
            new FakeWsl(), () => detected);

        Assert.Equal(1, (await usage.GetReportAsync(7)).Events);
    }

    [Fact]
    public void SkillsRootCanBeResolvedUnderAnotherHome()
    {
        var home = Path.Combine(_root, "linux-home");
        Assert.Equal(Path.Combine(home, ".claude", "skills"),
            AgentSkillCatalog.DefaultSkillsRoot(AgentSkillCatalog.ClaudeCode, homeDirectory: home));
        Assert.Equal(Path.Combine(_root, "repo", ".agents", "skills"),
            AgentSkillCatalog.DefaultSkillsRoot(AgentSkillCatalog.Codex, Path.Combine(_root, "repo"), home));
    }

    [Fact]
    public void CodexInWslStartsInsideTheDistributionWithItsLinuxHome()
    {
        var info = CodexQuotaProbe.CreateStartInfo("codex", @"\\wsl.localhost\Ubuntu\home\akash\.codex", windows: true,
            systemDirectory: @"C:\Windows\System32", inheritedWslEnv: "USERPROFILE/p");

        Assert.EndsWith("wsl.exe", info.FileName);
        Assert.Equal(["--distribution", "Ubuntu", "--exec", "/bin/sh", "-c", CodexQuotaProbe.WslLaunchScript], info.ArgumentList);
        Assert.Contains("codex app-server --stdio", CodexQuotaProbe.WslLaunchScript);
        Assert.Equal("/home/akash/.codex", info.Environment["CODEX_HOME"]);
        Assert.Equal("USERPROFILE/p:CODEX_HOME", info.Environment["WSLENV"]);

        var native = CodexQuotaProbe.CreateStartInfo("codex", @"\\wsl.localhost\Ubuntu\home\akash\.codex", windows: false,
            systemDirectory: "", inheritedWslEnv: null);
        Assert.Equal("codex", native.FileName);
        Assert.Equal(["app-server", "--stdio"], native.ArgumentList);
    }

    private WslHome Home()
    {
        var windowsHome = Path.Combine(_root, "Ubuntu-Test", "home", "tester");
        Directory.CreateDirectory(windowsHome);
        return new WslHome("Ubuntu-Test", "/home/tester", windowsHome);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class StaticProbe : ILiveQuotaProbe
    {
        public string Provider => "codex";
        public string ScopeKey() => "static";
        public Task<LiveQuotaSnapshot> FetchAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(new LiveQuotaSnapshot("codex", "ok", "test", now, null, null, [], null));
    }
}

/// <summary>A fixed set of running WSL homes, changed by tests to start and stop distributions.</summary>
internal sealed class FakeWsl(params WslHome[] homes) : IWslEnvironment
{
    public List<WslHome> Homes { get; } = [.. homes];
    public IReadOnlyList<WslHome> RunningHomes() => Homes.ToArray();
}
