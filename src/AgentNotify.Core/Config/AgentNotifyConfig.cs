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

    /// <summary>Master switch for notification sounds.</summary>
    public bool SoundsEnabled { get; set; }

    public double SoundVolume { get; set; } = 0.8;
    public string? DefaultSoundFile { get; set; }
    public Dictionary<string, string> TypeSoundFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool PlayCriticalSoundsDuringDoNotDisturb { get; set; }

    /// <summary>Kestrel max request body size in bytes.</summary>
    public long MaxRequestBodyBytes { get; set; } = 64 * 1024;

    /// <summary>Maximum accepted POST rate per token per second (simple guard).</summary>
    public int RateLimitPerSecond { get; set; } = 30;

    /// <summary>Metadata size cap (serialized) in bytes.</summary>
    public int MaxMetadataBytes { get; set; } = 8192;

    /// <summary>Auto-dismiss duration in seconds per notification type. 0 = until dismissed/resolved.</summary>
    public Dictionary<string, int> ToastDurations { get; set; } = DefaultDurations();

    /// <summary>User-defined presentation and behavior. Built-in definitions remain code-owned.</summary>
    public List<NotificationTypeDefinition> CustomNotificationTypes { get; set; } = [];

    public int ToastDurationSeconds(NotificationType type)
        => ToastDurationSeconds(NotificationTypes.FromBuiltIn(type));

    public int ToastDurationSeconds(string type)
    {
        var normalized = NotificationTypes.Normalize(type);
        var custom = CustomNotificationTypes.FirstOrDefault(x => x.Enabled && x.Id == normalized);
        if (custom is not null) return custom.DurationSeconds;
        if (normalized is not null && ToastDurations is { } d && d.TryGetValue(normalized, out var seconds))
            return seconds;
        return 7;
    }

    public static string EnumName(NotificationType type) => NotificationTypes.FromBuiltIn(type);

    public NotificationTypeDefinition? CustomType(string type)
    {
        var id = NotificationTypes.Normalize(type);
        return CustomNotificationTypes.FirstOrDefault(x => x.Enabled && x.Id == id);
    }

    public NotificationPriority DefaultPriorityFor(string type) =>
        CustomType(type)?.DefaultPriority ?? NotificationPriority.Normal;

    public static Dictionary<string, int> DefaultDurations() => new(StringComparer.OrdinalIgnoreCase)
    {
        [NotificationTypes.Completed] = 5,
        [NotificationTypes.Success] = 5,
        [NotificationTypes.Info] = 7,
        [NotificationTypes.Warning] = 12,
        [NotificationTypes.Error] = 15,
        [NotificationTypes.InputRequired] = 0,
        [NotificationTypes.PermissionRequired] = 0,
        [NotificationTypes.Blocked] = 0
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
        SoundVolume = Math.Clamp(SoundVolume, 0, 1);
        DefaultSoundFile = NormalizeSoundFile(DefaultSoundFile);
        TypeSoundFiles ??= new(StringComparer.OrdinalIgnoreCase);
        TypeSoundFiles = TypeSoundFiles
            .Select(x => new KeyValuePair<string, string?>(NotificationTypes.Normalize(x.Key) ?? "", NormalizeSoundFile(x.Value)))
            .Where(x => x.Key.Length > 0 && x.Value is not null)
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last().Value!, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(ToastLocation))
            ToastLocation = "BottomRight";
        if (ToastDurations is null || ToastDurations.Count == 0)
            ToastDurations = DefaultDurations();
        foreach (var key in ToastDurations.Keys.ToList())
        {
            var normalized = NotificationTypes.Normalize(key);
            if (normalized is not null && normalized != key && !ToastDurations.ContainsKey(normalized))
                ToastDurations[normalized] = ToastDurations[key];
        }
        foreach (var kv in DefaultDurations())
            ToastDurations.TryAdd(kv.Key, kv.Value);
        CustomNotificationTypes ??= [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CustomNotificationTypes = CustomNotificationTypes.Where(def =>
        {
            var id = NotificationTypes.Normalize(def.Id);
            if (id is null || NotificationTypes.BuiltIns.Contains(id) || !seen.Add(id)) return false;
            def.Id = id;
            def.DisplayName = string.IsNullOrWhiteSpace(def.DisplayName) ? id.Replace('_', ' ') : def.DisplayName.Trim();
            def.AccentColor = IsHexColor(def.AccentColor) ? def.AccentColor.ToUpperInvariant() : "#4A90D9";
            def.DurationSeconds = Math.Clamp(def.DurationSeconds, 0, 86400);
            return true;
        }).ToList();
    }

    private static bool IsHexColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        value[1..].All(Uri.IsHexDigit);

    public string? SoundFileFor(string type)
    {
        var id = NotificationTypes.Normalize(type);
        return id is not null && TypeSoundFiles.TryGetValue(id, out var file) ? file : DefaultSoundFile;
    }

    private static string? NormalizeSoundFile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var file = Path.GetFileName(value.Trim());
        var extension = Path.GetExtension(file);
        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            ? file : null;
    }
}
