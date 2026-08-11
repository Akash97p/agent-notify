using AgentNotify.Contracts;

namespace AgentNotify.Core.Config;

public sealed class NotificationTypeDefinition
{
    public string Id { get; set; } = "custom";
    public string DisplayName { get; set; } = "Custom";
    public string AccentColor { get; set; } = "#4A90D9";
    public NotificationPriority DefaultPriority { get; set; } = NotificationPriority.Normal;
    public int DurationSeconds { get; set; } = 7;
    public bool Enabled { get; set; } = true;

    public NotificationTypeDefinition Clone() => new()
    {
        Id = Id, DisplayName = DisplayName, AccentColor = AccentColor,
        DefaultPriority = DefaultPriority, DurationSeconds = DurationSeconds, Enabled = Enabled
    };
}

