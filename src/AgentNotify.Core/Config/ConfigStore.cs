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

    public static string DefaultConfigDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentNotify");

    public string ConfigDir => _configDir;
    public string ConfigPath => Path.Combine(_configDir, "config.json");
    public string DbPath => Path.Combine(_configDir, "agentnotify.db");
    public string LogsDir => Path.Combine(_configDir, "logs");

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
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, Json.Options));
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
