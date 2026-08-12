using AgentNotify.Contracts;
using AgentNotify.Core.Domain;
using AgentNotify.Desktop;

namespace AgentNotify.Tests;

/// <summary>
/// Covers the portable desktop notification backends. Notification text is attacker-influenced —
/// any agent that can reach the loopback API chooses it — so the emphasis is on what reaches an
/// external program.
/// </summary>
public sealed class DesktopNotifierTests
{
    private static Notification Sample(
        string title = "Need your decision",
        string message = "Option A or B?",
        string type = NotificationTypes.InputRequired) =>
        new()
        {
            Agent = "codex",
            Project = "payments",
            Type = type,
            Priority = NotificationPriority.High,
            Title = title,
            Message = message
        };

    [Fact]
    public async Task ConsoleNotifier_WritesOneLinePerNotification()
    {
        var writer = new StringWriter();
        var notifier = new ConsoleDesktopNotifier(writer);

        var shown = await notifier.ShowAsync(Sample(), new NotificationLifetime(0));

        Assert.True(shown);
        var output = writer.ToString();
        Assert.Contains("Need your decision", output, StringComparison.Ordinal);
        Assert.Contains("codex", output, StringComparison.Ordinal);
        Assert.Single(output.TrimEnd().Split('\n'));
    }

    [Fact]
    public async Task ConsoleNotifier_MarksAttentionTypesDistinctly()
    {
        var attention = new StringWriter();
        var informational = new StringWriter();

        await new ConsoleDesktopNotifier(attention)
            .ShowAsync(Sample(type: NotificationTypes.Blocked), new NotificationLifetime(0));
        await new ConsoleDesktopNotifier(informational)
            .ShowAsync(Sample(type: NotificationTypes.Info), new NotificationLifetime(7));

        Assert.StartsWith("!", attention.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("*", informational.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsoleNotifier_StripsControlCharactersFromAgentText()
    {
        var writer = new StringWriter();

        // A terminal escape in a title could otherwise rewrite the surrounding output.
        await new ConsoleDesktopNotifier(writer).ShowAsync(
            Sample(title: "Deploy[2Kdone\nsecond line", message: "body\r\nwith breaks"),
            new NotificationLifetime(5));

        // Compare the written content, not the writer's own trailing line terminator.
        var output = writer.ToString().TrimEnd('\r', '\n');
        Assert.DoesNotContain('', output);
        Assert.DoesNotContain('\r', output);
        Assert.DoesNotContain('\n', output);
    }

    [Fact]
    public void Lifetime_TreatsZeroAndNegativeAsSticky()
    {
        Assert.True(new NotificationLifetime(0).IsSticky);
        Assert.True(new NotificationLifetime(-1).IsSticky);
        Assert.False(new NotificationLifetime(7).IsSticky);
    }

    [Fact]
    public void Factory_AlwaysReturnsAUsableBackend()
    {
        var notifier = DesktopNotifierFactory.Create();

        // A notification must never be dropped because no graphical session exists.
        Assert.True(notifier.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(notifier.Name));
    }

    [Fact]
    public void PlatformNotifiers_AreOnlyAvailableOnTheirOwnPlatform()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.False(new MacOsDesktopNotifier().IsAvailable);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.False(new NotifySendDesktopNotifier().IsAvailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ConsoleNotifier_ToleratesEmptyText(string? title)
    {
        var writer = new StringWriter();

        var shown = await new ConsoleDesktopNotifier(writer).ShowAsync(
            new Notification { Title = title ?? "", Message = "", Agent = "", Type = NotificationTypes.Info },
            new NotificationLifetime(7));

        Assert.True(shown);
        Assert.Contains("AgentNotify", writer.ToString(), StringComparison.Ordinal);
    }
}
