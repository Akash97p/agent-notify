using AgentNotify.Contracts;

namespace AgentNotify.Core.Config;

/// <summary>Strongly typed runtime configuration. Loaded from and persisted to
/// %LOCALAPPDATA%\AgentNotify\config.json. Values use safe defaults.</summary>
public sealed class AgentNotifyConfig
{
    /// <summary>Loopback port for the local API.</summary>
    public int Port { get; set; } = 47821;

    /// <summary>Loopback API version (read-only).</summary>
    public string ApiVersion { get; set; } = "v1";

    /// <summary>Bearer token required for all /v1 routes. Generated on first run.</summary>
    public string AuthToken { get; set; } = "";

    /// <summary>Where toasts appear: "BottomRight" or "TopRight".</summary>
    public string ToastLocation { get; set; } = "BottomRight";

    /// <summary>Maximum simultaneously visible toasts. Oldest toasts are dismissed (and
    /// recorded in the log) when this is exceeded.</summary>
    public int MaxVisibleToasts { get; set; } = 5;

    /// <summary>History retention in days for non-active notifications.</summary>
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>Launch AgentNotify automatically at Windows logon.</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>When true, notifications are stored but no toasts are shown.</summary>
    public bool PauseNotifications { get; set; }

    /// <summary>Stub for future quiet-hours scheduling (reserved).</summary>
    public bool DoNotDisturb { get; set; }

    /// <summary>Reserved; V1 does not play sounds.</summary>
    public bool SoundsEnabled { get; set; }

    /// <summary>Kestrel max request body size in bytes.</summary>
    public long MaxRequestBodyBytes { get; set; } = 64 * 1024;

    /// <summary>Maximum accepted POST rate per token per second (simple guard).</summary>
    public int RateLimitPerSecond { get; set; } = 30;

    /// <summary>Metadata size cap (serialized) in bytes.</summary>
    public int MaxMetadataBytes { get; set; } = 8192;

    /// <summary>Auto-dismiss duration in seconds per notification type. 0 = until dismissed/resolved.</summary>
    public Dictionary<string, int> ToastDurations { get; set; } = DefaultDurations();

    public int ToastDurationSeconds(NotificationType type)
    {
        if (ToastDurations is { } d && d.TryGetValue(EnumName(type), out var seconds))
            return seconds;
        return 0;
    }

    public static string EnumName(NotificationType type) => type.ToString();

    public static Dictionary<string, int> DefaultDurations() => new(StringComparer.OrdinalIgnoreCase)
    {
        [nameof(NotificationType.Completed)] = 5,
        [nameof(NotificationType.Success)] = 5,
        [nameof(NotificationType.Info)] = 7,
        [nameof(NotificationType.Warning)] = 12,
        [nameof(NotificationType.Error)] = 15,
        [nameof(NotificationType.InputRequired)] = 0,
        [nameof(NotificationType.PermissionRequired)] = 0,
        [nameof(NotificationType.Blocked)] = 0
    };

    /// <summary>Copies defaults into missing keys (tolerant to partial config files).</summary>
    public void ApplyDefaults()
    {
        if (Port <= 0 || Port > 65535) Port = 47821;
        if (MaxVisibleToasts <= 0) MaxVisibleToasts = 5;
        if (HistoryRetentionDays < 0) HistoryRetentionDays = 30;
        if (MaxRequestBodyBytes <= 0) MaxRequestBodyBytes = 64 * 1024;
        if (RateLimitPerSecond <= 0) RateLimitPerSecond = 30;
        if (MaxMetadataBytes <= 0) MaxMetadataBytes = 8192;
        if (string.IsNullOrWhiteSpace(ToastLocation))
            ToastLocation = "BottomRight";
        if (ToastDurations is null || ToastDurations.Count == 0)
            ToastDurations = DefaultDurations();
        foreach (var kv in DefaultDurations())
            ToastDurations.TryAdd(kv.Key, kv.Value);
    }
}
