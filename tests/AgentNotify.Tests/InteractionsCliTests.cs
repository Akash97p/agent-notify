namespace AgentNotify.Tests;

public sealed class InteractionsCliTests
{
    [Fact]
    public async Task Interactions_RequestListGetRespondCancel_RoundTrip()
    {
        await using var fx = await ApiFixture.StartAsync();
        var port = fx.Port.ToString();

        var request = await AgentNotify.Cli.Program.Main([
            "interactions", "request",
            "--kind", "permission",
            "--prompt", "Deploy to prod?",
            "--choice", "allow-once:Allow once",
            "--choice", "deny:Deny",
            "--agent", "codex",
            "--project", "shop",
            "--port", port,
            "--token", fx.Token
        ]);
        Assert.Equal(0, request);

        var pending = await fx.InteractionService.ListAsync(
            new AgentNotify.Core.Persistence.InteractionQuery { PendingOnly = true });
        var item = Assert.Single(pending);

        var list = await AgentNotify.Cli.Program.Main([
            "interactions", "list", "--pending",
            "--port", port, "--token", fx.Token]);
        Assert.Equal(0, list);

        var get = await AgentNotify.Cli.Program.Main([
            "interactions", "get", item.Id, "--port", port, "--token", fx.Token]);
        Assert.Equal(0, get);

        var respond = await AgentNotify.Cli.Program.Main([
            "interactions", "respond", item.Id,
            "--response-id", "cli-r1",
            "--digest", item.RequestDigest,
            "--choice", "deny",
            "--port", port, "--token", fx.Token]);
        Assert.Equal(0, respond);

        var cancel = await AgentNotify.Cli.Program.Main([
            "interactions", "cancel", item.Id, "--port", port, "--token", fx.Token]);
        Assert.Equal(0, cancel);
    }

    [Fact]
    public async Task Interactions_RequestRejectsMissingPrompt()
    {
        await using var fx = await ApiFixture.StartAsync();
        var exit = await AgentNotify.Cli.Program.Main([
            "interactions", "request", "--kind", "text",
            "--port", fx.Port.ToString(), "--token", fx.Token]);
        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task Interactions_UnknownSubcommandFails()
    {
        Assert.Equal(1, await AgentNotify.Cli.Program.Main(["interactions", "bogus"]));
        Assert.Equal(1, await AgentNotify.Cli.Program.Main(["interactions"]));
    }
}
