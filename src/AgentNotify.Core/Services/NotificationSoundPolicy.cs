using AgentNotify.Contracts;
using AgentNotify.Core.Config;

namespace AgentNotify.Core.Services;

public static class NotificationSoundPolicy
{
    public static bool ShouldPlay(AgentNotifyConfig config, NotificationPriority priority)
    {
        if (!config.SoundsEnabled || config.PauseNotifications) return false;
        return !config.DoNotDisturb ||
            (priority == NotificationPriority.Critical && config.PlayCriticalSoundsDuringDoNotDisturb);
    }
}

