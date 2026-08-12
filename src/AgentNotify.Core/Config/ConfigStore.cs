using System.Security.Cryptography;
using System.Text.Json;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Config;

/// <summary>Loads and persists <see cref="AgentNotifyConfig"/> under a per-user data
/// directory (default %LOCALAPPDATA%\AgentNotify). Tolerant to malformed files.</summary>
public sealed class ConfigStore
{
    private readonly string _configDir;
    private readonly bool _applyEnvOverrides;

    public ConfigStore(string? configDir = null, bool applyEnvOverrides = true)
    {
        _configDir = configDir ?? DefaultConfigDir();
        _applyEnvOverrides = applyEnvOverrides;
    }

    /// <summary>
    /// Resolves the per-user data directory: <c>%LOCALAPPDATA%\AgentNotify</c> on Windows and
    /// <c>$XDG_DATA_HOME/AgentNotify</c> (or <c>~/.local/share/AgentNotify</c>) on Unix.
    /// </summary>
    /// <remarks>
    /// On Unix <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> returns an empty
    /// string when the base directory does not exist yet, which is the normal state of a fresh
    /// account. Combining that empty string yields the relative path <c>AgentNotify</c>, so the
    /// bearer token, the secret key, and the history database would be written into whatever
    /// directory the process happened to start in — for an agent, the user's repository. The result
    /// is therefore always resolved to an absolute path before it is returned.
    /// </remarks>
    public static string DefaultConfigDir() => Path.Combine(BaseDataDir(), "AgentNotify");

    private static string BaseDataDir()
    {
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (IsUsable(baseDir)) return baseDir;

        // Fall back to the XDG base directory specification, then to the home directory.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (IsUsable(xdg)) return xdg!;

        var home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetEnvironmentVariable("USERPROFILE");
        if (IsUsable(home)) return Path.Combine(home!, ".local", "share");

        // Last resort: a user-scoped directory under the temporary path. Still absolute, so local
        // state can never land in the working directory.
        return Path.Combine(Path.GetTempPath(), $"agentnotify-{Environment.UserName}");
    }

    private static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path);

    public string ConfigDir => _configDir;
    public string ConfigPath => Path.Combine(_configDir, "config.json");
    public string DbPath => Path.Combine(_configDir, "agentnotify.db");
    public string LogsDir => Path.Combine(_configDir, "logs");
    public string SoundsDir => Path.Combine(_configDir, "sounds");

    /// <summary>Loads configuration. Returns defaults when the file is missing or malformed.</summary>
    public AgentNotifyConfig Load()
    {
        var config = new AgentNotifyConfig();
        if (File.Exists(ConfigPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AgentNotifyConfig>(File.ReadAllText(ConfigPath), Json.Options);
                if (loaded is not null)
                    config = loaded;
            }
            catch (JsonException)
            {
                // Malformed config: keep defaults; Save will rewrite a healthy file.
            }
        }

        config.ApplyDefaults();

        if (_applyEnvOverrides)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable("AGENTNOTIFY_PORT"), out var port))
                config.Port = port;
            var token = Environment.GetEnvironmentVariable("AGENTNOTIFY_TOKEN");
            if (!string.IsNullOrWhiteSpace(token))
                config.AuthToken = token.Trim();
        }

        return config;
    }

    public void Save(AgentNotifyConfig config)
    {
        UnixFilePermissions.CreateOwnerOnlyDirectory(_configDir);
        var temporary = ConfigPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(config, Json.Options));
            // Restrict before the move so the token is never briefly world-readable at its final path.
            UnixFilePermissions.RestrictFile(temporary);
            File.Move(temporary, ConfigPath, overwrite: true);
            UnixFilePermissions.RestrictFile(ConfigPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    /// <summary>Generates and persists a cryptographically random auth token if one is
    /// not already set. Returns the effective token.</summary>
    public string EnsureAuthToken(AgentNotifyConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.AuthToken))
            return config.AuthToken;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        config.AuthToken = token;
        Save(config);
        return token;
    }
}
