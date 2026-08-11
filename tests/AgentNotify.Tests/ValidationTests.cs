using AgentNotify.Contracts;
using AgentNotify.Core.Services;

namespace AgentNotify.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void ValidRequest_ReturnsNull()
    {
        var req = new CreateNotificationRequest { Title = "Hello", Message = "World" };
        Assert.Null(NotificationValidator.Validate(req));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Title_Required(string? title)
    {
        var req = new CreateNotificationRequest { Title = title!, Message = "ok" };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Message_Required(string? message)
    {
        var req = new CreateNotificationRequest { Title = "t", Message = message! };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Title_MaxLength()
    {
        var req = new CreateNotificationRequest { Title = new string('a', 201), Message = "ok" };
        Assert.Contains("title", NotificationValidator.Validate(req)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Message_MaxLength()
    {
        var req = new CreateNotificationRequest { Title = "t", Message = new string('a', 4001) };
        Assert.Contains("message", NotificationValidator.Validate(req)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Title_ExactLimit_Passes()
    {
        var req = new CreateNotificationRequest { Title = new string('a', 200), Message = "ok" };
        Assert.Null(NotificationValidator.Validate(req));
    }

    [Theory]
    [InlineData(101)]
    public void Agent_MaxLength(int len)
    {
        var req = new CreateNotificationRequest { Title = "t", Message = "m", Agent = new string('a', len) };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Theory]
    [InlineData(101)]
    public void AgentInstance_MaxLength(int len)
    {
        var req = new CreateNotificationRequest { Title = "t", Message = "m", AgentInstance = new string('a', len) };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Project_MaxLength()
    {
        var req = new CreateNotificationRequest { Title = "t", Message = "m", Project = new string('a', 201) };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Key_MaxLength()
    {
        var req = new CreateNotificationRequest { Title = "t", Message = "m", Key = new string('a', 101) };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Cwd_MaxLength()
    {
        var req = new CreateNotificationRequest { Title = "t", Message = "m", Cwd = new string('a', 1025) };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Pid_Negative()
    {
        var req = new CreateNotificationRequest { Title = "t", Message = "m", Pid = -1 };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Pid_Zero_Passes() =>
        Assert.Null(NotificationValidator.Validate(new CreateNotificationRequest { Title = "t", Message = "m", Pid = 0 }));

    [Fact]
    public void Metadata_TooLarge()
    {
        var big = new string('x', 9000);
        var req = new CreateNotificationRequest
        {
            Title = "t",
            Message = "m",
            Metadata = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["big"] = System.Text.Json.JsonSerializer.SerializeToElement(big)
            }
        };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Metadata_Small_Passes()
    {
        var req = new CreateNotificationRequest
        {
            Title = "t",
            Message = "m",
            Metadata = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["k"] = System.Text.Json.JsonSerializer.SerializeToElement("v")
            }
        };
        Assert.Null(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Update_MissingStatus()
    {
        var req = new UpdateNotificationRequest { Status = null };
        Assert.NotNull(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Update_WithStatus_Passes()
    {
        var req = new UpdateNotificationRequest { Status = NotificationStatus.Dismissed };
        Assert.Null(NotificationValidator.Validate(req));
    }

    [Fact]
    public void Create_NullRequest_ReturnsError()
    {
        Assert.NotNull(NotificationValidator.Validate((CreateNotificationRequest)null!));
    }

    [Fact]
    public void Create_TrimsNotRequiredForValidity()
    {
        var req = new CreateNotificationRequest { Title = "  hello  ", Message = "  world  " };
        Assert.Null(NotificationValidator.Validate(req));
    }
}
