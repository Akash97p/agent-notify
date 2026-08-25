using System.Net.Http.Json;
using AgentNotify.Protocol;

namespace AgentNotify.Tests;

public sealed class CliTests
{
    [Fact]
    public async Task List_UnresolvedWithoutValue_DoesNotCrash()
    {
        var exitCode = await AgentNotify.Cli.Program.Main(
            ["list", "--unresolved", "--port", "1", "--token", "test"]);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Send_AcceptsHyphenatedNotificationType()
    {
        await using var fx = await ApiFixture.StartAsync();
        var exitCode = await AgentNotify.Cli.Program.Main([
            "send",
            "--title", "Need input",
            "--message", "Choose one",
            "--type", "input-required",
            "--port", fx.Port.ToString(),
            "--token", fx.Token
        ]);
        Assert.Equal(0, exitCode);

        using var client = fx.AuthedClient();
        var notifications = await client.GetFromJsonAsync<List<NotificationDto>>(
            $"{fx.BaseUrl}/v1/notifications?type=input_required", Json.Options);
        Assert.Contains(notifications!, n => n.Title == "Need input" && n.Type == NotificationTypes.InputRequired);
    }

    [Fact]
    public async Task CustomType_IsAccepted()
    {
        await using var fx = await ApiFixture.StartAsync();
        var exitCode = await AgentNotify.Cli.Program.Main([
            "send", "--title", "t", "--message", "m", "--type", "deployment-waiting", "--port", fx.Port.ToString(), "--token", fx.Token
        ]);
        Assert.Equal(0, exitCode);
        using var client = fx.AuthedClient();
        var notifications = await client.GetFromJsonAsync<List<NotificationDto>>(
            $"{fx.BaseUrl}/v1/notifications?type=deployment_waiting", Json.Options);
        Assert.Contains(notifications!, n => n.Type == "deployment_waiting");
    }

    [Fact]
    public async Task InvalidType_IsRejectedBeforeNetworkCall()
    {
        var exitCode = await AgentNotify.Cli.Program.Main([
            "send", "--title", "t", "--message", "m", "--type", "bad type!", "--port", "1", "--token", "test"
        ]);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task VersionSwitch_Works()
    {
        Assert.Equal(0, await AgentNotify.Cli.Program.Main(["--version"]));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task InstallSkill_WritesBundledSkill(string agent)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentnotify-skill-{Guid.NewGuid():N}");
        try
        {
            var exitCode = await AgentNotify.Cli.Program.Main(["install-skill", agent, "--path", root]);

            Assert.Equal(0, exitCode);
            var skill = Path.Combine(root, "agentnotify", "SKILL.md");
            Assert.True(File.Exists(skill));
            Assert.Contains("name: agentnotify", await File.ReadAllTextAsync(skill), StringComparison.Ordinal);
            Assert.Equal(agent == "codex", File.Exists(Path.Combine(root, "agentnotify", "agents", "openai.yaml")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallSkill_ProtectsExistingCustomizationUnlessForced()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentnotify-skill-{Guid.NewGuid():N}");
        var skillDirectory = Path.Combine(root, "agentnotify");
        var skill = Path.Combine(skillDirectory, "SKILL.md");
        try
        {
            Directory.CreateDirectory(skillDirectory);
            await File.WriteAllTextAsync(skill, "custom instructions");

            Assert.Equal(1, await AgentNotify.Cli.Program.Main(["install", "skill", "claude", "--path", root]));
            Assert.Equal("custom instructions", await File.ReadAllTextAsync(skill));

            Assert.Equal(0, await AgentNotify.Cli.Program.Main(["install-skill", "claude", "--path", root, "--force"]));
            Assert.Contains("name: agentnotify", await File.ReadAllTextAsync(skill), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallSkill_DryRunDoesNotWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agentnotify-skill-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal(0, await AgentNotify.Cli.Program.Main(["install-skill", "codex", "--path", root, "--dry-run"]));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
