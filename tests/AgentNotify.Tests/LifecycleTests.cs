using AgentNotify.Contracts;
using AgentNotify.Core.Domain;

namespace AgentNotify.Tests;

public sealed class LifecycleTests
{
    [Theory]
    [InlineData(NotificationStatus.Active, NotificationStatus.Dismissed, true)]
    [InlineData(NotificationStatus.Active, NotificationStatus.Resolved, true)]
    [InlineData(NotificationStatus.Active, NotificationStatus.Active, true)]
    [InlineData(NotificationStatus.Dismissed, NotificationStatus.Active, true)]
    [InlineData(NotificationStatus.Dismissed, NotificationStatus.Resolved, false)]
    [InlineData(NotificationStatus.Dismissed, NotificationStatus.Dismissed, true)]
    [InlineData(NotificationStatus.Resolved, NotificationStatus.Active, true)]
    [InlineData(NotificationStatus.Resolved, NotificationStatus.Dismissed, false)]
    [InlineData(NotificationStatus.Resolved, NotificationStatus.Resolved, true)]
    public void Transitions(NotificationStatus from, NotificationStatus to, bool allowed)
    {
        var err = StatusTransitions.Validate(from, to);
        if (allowed) Assert.Null(err);
        else Assert.NotNull(err);
    }

    [Fact]
    public void ActiveCanBecomeEitherTerminalStatus()
    {
        Assert.Null(StatusTransitions.Validate(NotificationStatus.Active, NotificationStatus.Dismissed));
        Assert.Null(StatusTransitions.Validate(NotificationStatus.Active, NotificationStatus.Resolved));
    }

    [Fact]
    public void Terminal_CanBeReopened()
    {
        Assert.Null(StatusTransitions.Validate(NotificationStatus.Dismissed, NotificationStatus.Active));
        Assert.Null(StatusTransitions.Validate(NotificationStatus.Resolved, NotificationStatus.Active));
    }

    [Fact]
    public void TerminalCannotSwitchTerminal()
    {
        Assert.NotNull(StatusTransitions.Validate(NotificationStatus.Dismissed, NotificationStatus.Resolved));
        Assert.NotNull(StatusTransitions.Validate(NotificationStatus.Resolved, NotificationStatus.Dismissed));
    }
}
