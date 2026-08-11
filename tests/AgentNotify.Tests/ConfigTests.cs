using System.Text.Json;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;

namespace AgentNotify.Tests;

public sealed class ConfigTests
{
    [Fact]
    public void Defaults_AreSane()
    {
        var c = new AgentNotifyConfig();
        c.ApplyDefaults();
        Assert.Equal(47821, c.Port);
        Assert.Equal(5, c.MaxVisibleToasts);
        Assert.Equal(30, c.HistoryRetentionDays);
        Assert.NotEmpty(c.ToastDurations);
    }

    [Fact]
    public void ApplyDefaults_FillsMissing()
    {
        var c = new AgentNotifyConfig { Port = 0, MaxVisibleToasts = 0, ToastDurations = null! };
        c.ApplyDefaults();
        Assert.Equal(47821, c.Port);
        Assert.Equal(5, c.MaxVisibleToasts);
        Assert.NotNull(c.ToastDurations);
    }

    [Fact]
    public void ApplyDefaults_DoesNotOverwriteExplicitValues()
    {
        var durations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Info"] = 99 };
        var c = new AgentNotifyConfig { Port = 9999, MaxVisibleToasts = 7, ToastDurations = durations };
        c.ApplyDefaults();
        Assert.Equal(9999, c.Port);
        Assert.Equal(7, c.MaxVisibleToasts);
        Assert.Equal(99, c.ToastDurations["Info"]);
        // Default keys still filled.
        Assert.True(c.ToastDurations.ContainsKey("Error"));
    }

    [Fact]
    public void ToastDurations_KnownTypes()
    {
        var c = new AgentNotifyConfig();
        Assert.Equal(0, c.ToastDurationSeconds(NotificationType.InputRequired));
        Assert.Equal(0, c.ToastDurationSeconds(NotificationType.PermissionRequired));
        Assert.Equal(0, c.ToastDurationSeconds(NotificationType.Blocked));
        Assert.True(c.ToastDurationSeconds(NotificationType.Success) > 0);
        Assert.True(c.ToastDurationSeconds(NotificationType.Error) > 0);
    }

    [Fact]
    public void CustomTypes_AreNormalizedAndControlPresentationDefaults()
    {
        var c = new AgentNotifyConfig
        {
            CustomNotificationTypes =
            [
                new() { Id = "Deploy-Wait", DisplayName = "Deployment waiting", AccentColor = "#12abef", DurationSeconds = 0, DefaultPriority = NotificationPriority.High },
                new() { Id = "info", DisplayName = "Cannot replace built-in" },
                new() { Id = "bad id!", DisplayName = "Invalid" }
            ]
        };
        c.ApplyDefaults();
        var custom = Assert.Single(c.CustomNotificationTypes);
        Assert.Equal("deploy_wait", custom.Id);
        Assert.Equal("#12ABEF", custom.AccentColor);
        Assert.Equal(0, c.ToastDurationSeconds("deploy_wait"));
        Assert.Equal(NotificationPriority.High, c.DefaultPriorityFor("deploy_wait"));
    }

    [Fact]
    public void LegacyPascalCaseDurations_AreMigrated()
    {
        var c = new AgentNotifyConfig { ToastDurations = new(StringComparer.OrdinalIgnoreCase) { ["InputRequired"] = 42 } };
        c.ApplyDefaults();
        Assert.Equal(42, c.ToastDurationSeconds(NotificationTypes.InputRequired));
    }

    [Fact]
    public void SoundProfiles_AreSanitizedAndResolvePerTypeOverride()
    {
        var c = new AgentNotifyConfig
        {
            SoundVolume = 4,
            DefaultSoundFile = @"C:\outside\global.MP3",
            TypeSoundFiles = new()
            {
                ["input-required"] = @"..\attention.wav",
                ["bad type!"] = "ignored.mp3",
                ["info"] = "ignored.exe"
            }
        };
        c.ApplyDefaults();
        Assert.Equal(1, c.SoundVolume);
        Assert.Equal("global.MP3", c.DefaultSoundFile);
        Assert.Equal("attention.wav", c.SoundFileFor(NotificationTypes.InputRequired));
        Assert.Equal("global.MP3", c.SoundFileFor(NotificationTypes.Info));
        Assert.Single(c.TypeSoundFiles);
    }

    [Fact]
    public void ConfigStore_Load_MissingFile_ReturnsDefaults()
    {
        var dir = NewTempDir();
        try
        {
            var store = new ConfigStore(dir, applyEnvOverrides: false);
            var cfg = store.Load();
            Assert.Equal(47821, cfg.Port);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ConfigStore_SaveAndLoad_RoundTrips()
    {
        var dir = NewTempDir();
        try
        {
            var store = new ConfigStore(dir, applyEnvOverrides: false);
            var cfg = store.Load();
            cfg.Port = 43210;
            cfg.PauseNotifications = true;
            store.Save(cfg);

            var loaded = new ConfigStore(dir, applyEnvOverrides: false).Load();
            Assert.Equal(43210, loaded.Port);
            Assert.True(loaded.PauseNotifications);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ConfigStore_MalformedJson_ReturnsDefaults()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ not valid json");
            var cfg = new ConfigStore(dir, applyEnvOverrides: false).Load();
            Assert.Equal(47821, cfg.Port);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ConfigStore_EnvOverrides()
    {
        var dir = NewTempDir();
        try
        {
            Environment.SetEnvironmentVariable("AGENTNOTIFY_PORT", "55555");
            Environment.SetEnvironmentVariable("AGENTNOTIFY_TOKEN", "envtok");
            var cfg = new ConfigStore(dir, applyEnvOverrides: true).Load();
            Assert.Equal(55555, cfg.Port);
            Assert.Equal("envtok", cfg.AuthToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTNOTIFY_PORT", null);
            Environment.SetEnvironmentVariable("AGENTNOTIFY_TOKEN", null);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void EnsureAuthToken_GeneratesWhenMissing()
    {
        var dir = NewTempDir();
        try
        {
            var store = new ConfigStore(dir, applyEnvOverrides: false);
            var cfg = new AgentNotifyConfig { AuthToken = "" };
            var token = store.EnsureAuthToken(cfg);
            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.Equal(token, cfg.AuthToken);
            // Persists
            Assert.True(File.Exists(store.ConfigPath));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void EnsureAuthToken_DoesNotReplaceExisting()
    {
        var dir = NewTempDir();
        try
        {
            var store = new ConfigStore(dir, applyEnvOverrides: false);
            var cfg = new AgentNotifyConfig { AuthToken = "existing" };
            var token = store.EnsureAuthToken(cfg);
            Assert.Equal("existing", token);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Json_SnakeCase_RoundTrip()
    {
        var req = new CreateNotificationRequest { Title = "t", Message = "m", Type = NotificationTypes.InputRequired };
        var json = JsonSerializer.Serialize(req, Json.Options);
        Assert.Contains("input_required", json);
        var back = JsonSerializer.Deserialize<CreateNotificationRequest>(json, Json.Options)!;
        Assert.Equal(NotificationTypes.InputRequired, back.Type);
    }

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), "an-test-" + Guid.NewGuid().ToString("N"));
}
