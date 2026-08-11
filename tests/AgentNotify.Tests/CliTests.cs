using System.Net.Http.Json;
using AgentNotify.Contracts;

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
}
