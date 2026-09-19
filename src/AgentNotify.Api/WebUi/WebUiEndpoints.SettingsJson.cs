using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Core;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Delivery.Forms;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Harness;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Persistence;
using AgentNotify.Insights.Quota;
using AgentNotify.Core.Services;
using AgentNotify.Core.Skills;
using AgentNotify.Insights.Usage;
using AgentNotify.Core.Wsl;
using AgentNotify.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.WebUi;

public sealed partial class WebUiEndpoints
{
    private static object MenuBarSettingsJson(AgentNotifyConfig config) => new
    {
        supported = OperatingSystem.IsMacOS(),
        enabled = config.MacMenuBar.Enabled,
        refresh_minutes = config.MacMenuBar.RefreshMinutes,
        account_ids = config.MacMenuBar.AccountIds
    };

    private static MacMenuBarSettings ValidateMenuBarSettings(
        MacMenuBarSettings current,
        MenuBarSettingsBody body,
        IReadOnlySet<string> knownAccounts)
    {
        var refresh = body.RefreshMinutes ?? current.RefreshMinutes;
        if (refresh is < 5 or > 60)
            throw new ArgumentException("Menu-bar refresh must be between 5 and 60 minutes.");
        var accountIds = body.AccountIds is null ? [.. current.AccountIds] : body.AccountIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (accountIds.Count > 32)
            throw new ArgumentException("Select at most 32 menu-bar accounts.");
        var unknown = accountIds.FirstOrDefault(id => !knownAccounts.Contains(id));
        if (unknown is not null)
            throw new ArgumentException("A selected menu-bar account is not currently monitored.");
        return new MacMenuBarSettings
        {
            Enabled = body.Enabled ?? current.Enabled,
            RefreshMinutes = refresh,
            AccountIds = accountIds
        };
    }

    private static object SettingsJson(AgentNotifyConfig config) => new
    {
        port = config.Port,
        history_retention_days = config.HistoryRetentionDays,
        pause_notifications = config.PauseNotifications,
        do_not_disturb = config.DoNotDisturb,
        launch_at_startup = config.LaunchAtStartup,
        toast_location = config.ToastLocation,
        max_visible_toasts = config.MaxVisibleToasts,
        toast_durations = NotificationTypes.BuiltIns.ToDictionary(type => type, config.ToastDurationSeconds),
        sounds_enabled = config.SoundsEnabled,
        sound_volume = (int)Math.Round(config.SoundVolume * 100),
        play_critical_sounds_during_do_not_disturb = config.PlayCriticalSoundsDuringDoNotDisturb,
        default_sound_file = config.DefaultSoundFile,
        type_sound_files = config.TypeSoundFiles
    };

    private static void ApplySettings(AgentNotifyConfig config, SettingsBody body)
    {
        // Validate everything before touching the shared config, so a refusal changes nothing.
        static int Range(int? value, int current, int min, int max, string label) =>
            value is null ? current
            : value < min || value > max ? throw new ArgumentException($"{label} must be between {min} and {max}.")
            : value.Value;

        var port = Range(body.Port, config.Port, 1, 65535, "Port");
        var retention = Range(body.HistoryRetentionDays, config.HistoryRetentionDays, 0, 3650, "Retention");
        var visible = Range(body.MaxVisibleToasts, config.MaxVisibleToasts, 1, 20, "Maximum visible toasts");
        var volume = Range(body.SoundVolume, (int)Math.Round(config.SoundVolume * 100), 0, 100, "Sound volume");
        var location = body.ToastLocation ?? config.ToastLocation;
        if (location is not ("BottomRight" or "TopRight"))
            throw new ArgumentException("Screen corner must be BottomRight or TopRight.");

        var durations = new Dictionary<string, int>(config.ToastDurations, StringComparer.OrdinalIgnoreCase);
        foreach (var (type, seconds) in body.ToastDurations ?? [])
        {
            var id = NotificationTypes.Normalize(type);
            if (id is null || !NotificationTypes.BuiltIns.Contains(id))
                throw new ArgumentException($"'{type}' is not a built-in notification type.");
            durations[id] = Range(seconds, 0, 0, 86400, $"{id.Replace('_', ' ')} duration");
        }

        string? defaultSound = config.DefaultSoundFile;
        if (body.DefaultSoundFile is not null)
            defaultSound = NormalizeSound(body.DefaultSoundFile);
        Dictionary<string, string>? typeSounds = null;
        if (body.TypeSoundFiles is not null)
        {
            typeSounds = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (type, file) in body.TypeSoundFiles)
            {
                var id = NotificationTypes.Normalize(type) ?? throw new ArgumentException($"'{type}' is not a valid notification type.");
                if (NormalizeSound(file) is { } sound) typeSounds[id] = sound;
            }
        }

        config.Port = port;
        config.HistoryRetentionDays = retention;
        config.MaxVisibleToasts = visible;
        config.SoundVolume = volume / 100d;
        config.ToastLocation = location;
        config.ToastDurations = durations;
        if (body.PauseNotifications is { } pause) config.PauseNotifications = pause;
        if (body.DoNotDisturb is { } dnd) config.DoNotDisturb = dnd;
        if (body.SoundsEnabled is { } soundsEnabled) config.SoundsEnabled = soundsEnabled;
        if (body.PlayCriticalSoundsDuringDoNotDisturb is { } critical) config.PlayCriticalSoundsDuringDoNotDisturb = critical;
        config.DefaultSoundFile = defaultSound;
        if (typeSounds is not null) config.TypeSoundFiles = typeSounds;
    }

    private static string? NormalizeSound(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var file = SafeFileName.Last(value.Trim());
        var extension = Path.GetExtension(file);
        if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sounds must be WAV or MP3 files.");
        return file;
    }

    private static object TypesJson(AgentNotifyConfig config) => new
    {
        built_in = NotificationTypes.BuiltIns,
        custom = config.CustomNotificationTypes.Select(type => new
        {
            id = type.Id,
            display_name = type.DisplayName,
            accent_color = type.AccentColor,
            default_priority = type.DefaultPriority,
            duration_seconds = type.DurationSeconds,
            enabled = type.Enabled
        })
    };

    private static List<NotificationTypeDefinition> ValidateTypes(IReadOnlyList<TypeBody> types)
    {
        if (types.Count > 100) throw new ArgumentException("At most 100 custom types are supported.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<NotificationTypeDefinition>();
        foreach (var type in types)
        {
            var id = NotificationTypes.Normalize(type.Id);
            if (id is null || NotificationTypes.BuiltIns.Contains(id))
                throw new ArgumentException($"'{type.Id}' is not a usable custom type ID. Use lowercase letters, digits, and underscores, and avoid built-in names.");
            if (!seen.Add(id)) throw new ArgumentException($"The custom type ID '{id}' is used twice.");
            if (type.DurationSeconds is < 0 or > 86400) throw new ArgumentException("Custom lifetime must be 0–86400 seconds.");
            var color = (type.AccentColor ?? "").Trim();
            if (color.Length != 7 || color[0] != '#' || !color[1..].All(Uri.IsHexDigit))
                throw new ArgumentException("Accent must use #RRGGBB.");
            if (!Enum.TryParse<NotificationPriority>(type.DefaultPriority, ignoreCase: true, out var priority))
                priority = NotificationPriority.Normal;
            var name = (type.DisplayName ?? "").Trim();
            if (name.Length > 60) throw new ArgumentException("Display names must be at most 60 characters.");
            result.Add(new NotificationTypeDefinition
            {
                Id = id,
                DisplayName = name.Length == 0 ? id.Replace('_', ' ') : name,
                AccentColor = color.ToUpperInvariant(),
                DefaultPriority = priority,
                DurationSeconds = type.DurationSeconds,
                Enabled = type.Enabled
            });
        }

        return result;
    }

    // ---- request bodies --------------------------------------------------------------------

    private sealed class OpenCodeGoBody
    {
        public int? RenewalDay { get; set; }
    }

    private sealed class QuotaAccountBody
    {
        public string? Provider { get; set; }
        public string? Label { get; set; }
        public string? Directory { get; set; }
    }

    private sealed class MenuBarSettingsBody
    {
        public bool? Enabled { get; set; }
        public int? RefreshMinutes { get; set; }
        public List<string>? AccountIds { get; set; }
    }

    private sealed class SettingsBody
    {
        public int? Port { get; set; }
        public int? HistoryRetentionDays { get; set; }
        public bool? PauseNotifications { get; set; }
        public bool? DoNotDisturb { get; set; }
        public string? ToastLocation { get; set; }
        public int? MaxVisibleToasts { get; set; }
        public Dictionary<string, int>? ToastDurations { get; set; }
        public bool? SoundsEnabled { get; set; }
        public int? SoundVolume { get; set; }
        public bool? PlayCriticalSoundsDuringDoNotDisturb { get; set; }
        public string? DefaultSoundFile { get; set; }
        public Dictionary<string, string>? TypeSoundFiles { get; set; }
    }

    private sealed class TypesBody { public List<TypeBody>? Custom { get; set; } }

    private sealed class TypeBody
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? AccentColor { get; set; }
        public string? DefaultPriority { get; set; }
        public int DurationSeconds { get; set; }
        public bool Enabled { get; set; } = true;
    }

    private sealed class AnswerBody
    {
        public string? RequestDigest { get; set; }
        public string? ChoiceId { get; set; }
        public string? Text { get; set; }
    }

    private sealed class ProviderBody
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public bool Enabled { get; set; }
        public Dictionary<string, string?>? Values { get; set; }
        public Dictionary<string, string?>? Secrets { get; set; }
        public List<string>? ClearSecrets { get; set; }
        public string? PairingId { get; set; }
    }

    private sealed class PairingBody
    {
        public string? SenderName { get; set; }
        public string? ProviderId { get; set; }
    }

    private sealed class RouteBody
    {
        public string? Name { get; set; }
        public string? ProviderId { get; set; }
        public bool Enabled { get; set; }
        public string? MinimumPriority { get; set; }
        public string? TypeId { get; set; }
        public string? Project { get; set; }
        public string? Agent { get; set; }
        public bool IncludeMessage { get; set; } = true;
    }

    private sealed class SkillBody { public bool Force { get; set; } public string? Wsl { get; set; } public string? Account { get; set; } }

}
