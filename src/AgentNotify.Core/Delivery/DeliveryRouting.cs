using System.Text.Json;
using AgentNotify.Contracts;
using AgentNotify.Core.Domain;

namespace AgentNotify.Core.Delivery;

public static class DeliveryRouting
{
    public static bool Matches(DeliveryRoute route, Notification notification)
    {
        if (!route.Enabled || notification.Priority < route.MinimumPriority)
            return false;
        if (route.TypeId is not null &&
            !string.Equals(
                NotificationTypes.Normalize(route.TypeId),
                notification.Type,
                StringComparison.OrdinalIgnoreCase))
            return false;
        if (route.Project is not null &&
            !string.Equals(route.Project, notification.Project, StringComparison.OrdinalIgnoreCase))
            return false;
        if (route.Agent is not null &&
            !string.Equals(route.Agent, notification.Agent, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    public static string CreatePayload(Notification notification, bool includeMessage) =>
        JsonSerializer.Serialize(new
        {
            notificationId = notification.Id,
            notification.Type,
            priority = notification.Priority.ToString().ToLowerInvariant(),
            notification.Title,
            message = includeMessage ? notification.Message : null,
            notification.Agent,
            notification.AgentInstance,
            notification.Project,
            notification.CreatedAt
        }, Json.Options);
}

public static class RetrySchedule
{
    public const int MaximumAttempts = 6;

    public static TimeSpan DelayFor(int attempt, double jitter = 0) =>
        TimeSpan.FromSeconds(
            Math.Min(900, Math.Pow(2, Math.Clamp(attempt, 1, MaximumAttempts)) * 5) *
            Math.Clamp(1 + jitter, .5, 1.5));
}
