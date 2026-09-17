using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Quota;
using AgentNotify.Core.Skills;
using AgentNotify.Core.Usage;
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

        Assert.Equal("3", report.ContractVersion);
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
            probeFactory: _ => new StaticProbe(), defaultAccountLabel: key => labels.GetValueOrDefault(key), wsl: wsl);

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
            wsl: new FakeWsl(home), removedAccounts: () => removed);

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
