using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Config;
using AgentNotify.Core.Wsl;
using Microsoft.Data.Sqlite;

namespace AgentNotify.Insights.Usage;

/// <summary>Read-only, in-process index of local coding-agent token ledgers.</summary>
public sealed partial class LocalUsageService
{
    private readonly IReadOnlyList<string> _claudeRoots;
    private readonly IReadOnlyList<string> _codexRoots;
    private readonly string _openCodeDatabase;
    private readonly IReadOnlyList<string> _museRoots;
    private readonly IReadOnlyList<string> _kiloDatabases;
    private readonly IReadOnlyList<string> _geminiRoots;
    private readonly IWslEnvironment? _wsl;
    private readonly Func<IReadOnlyList<QuotaAccountDefinition>>? _accounts;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private Dictionary<string, CachedFile> _files = new(StringComparer.Ordinal);
    private Dictionary<string, CachedDatabase> _databases = new(StringComparer.Ordinal);

    /// <param name="wsl">
    /// Running WSL distributions whose agent logs are read too. Defaults to <see cref="WslDiscovery.Default"/>
    /// only when no explicit roots are given, so a caller that names its roots gets exactly those.
    /// </param>
    /// <param name="accounts">Profiles added by hand on Live quota, whose ledgers are read as well.</param>
    /// <param name="museRoots">Muse Code <c>sessions</c> directories.</param>
    /// <param name="kiloDatabases">Kilo CLI SQLite databases, which use OpenCode's schema.</param>
    /// <param name="geminiRoots">Gemini CLI <c>tmp</c> directories holding per-project <c>chats</c>.</param>
    public LocalUsageService(IEnumerable<string>? claudeRoots = null, IEnumerable<string>? codexRoots = null,
        string? openCodeDatabase = null, IWslEnvironment? wsl = null,
        Func<IReadOnlyList<QuotaAccountDefinition>>? accounts = null, IEnumerable<string>? museRoots = null,
        IEnumerable<string>? kiloDatabases = null, IEnumerable<string>? geminiRoots = null)
    {
        _accounts = accounts;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaults = claudeRoots is null && codexRoots is null && openCodeDatabase is null;
        _wsl = wsl ?? (defaults ? WslDiscovery.Default : null);
        _museRoots = (museRoots ?? (defaults ? [MuseSessions(home)] : [])).ToArray();
        _kiloDatabases = (kiloDatabases ?? (defaults ? [KiloDatabase(home)] : [])).ToArray();
        _geminiRoots = (geminiRoots ?? (defaults ? [Path.Combine(home, ".gemini", "tmp")] : [])).ToArray();
        _claudeRoots = (claudeRoots ?? ClaudeRoots(home)).Distinct(StringComparer.Ordinal).ToArray();
        _codexRoots = (codexRoots ?? CodexRoots(home)).Distinct(StringComparer.Ordinal).ToArray();
        _openCodeDatabase = openCodeDatabase ?? OpenCodeDatabase(home);
    }

    /// <summary>
    /// The ledgers to read now. WSL homes are resolved on every scan because distributions start
    /// and stop; inside WSL the agents' own environment variables are not visible, so their
    /// default locations are used.
    /// </summary>
    private Sources CurrentSources()
    {
        var homes = _wsl?.RunningHomes() ?? [];
        var accounts = (_accounts?.Invoke() ?? []).Where(account => Path.IsPathFullyQualified(account.Directory)).ToArray();
        return new Sources(
            [.. new[]
            {
                _claudeRoots,
                homes.SelectMany(home => new[]
                {
                    Path.Combine(home.WindowsHome, ".claude", "projects"),
                    Path.Combine(home.WindowsHome, ".config", "claude", "projects")
                }),
                accounts.Where(account => account.Provider == "claude_code")
                    .Select(account => Path.Combine(account.Directory, "projects"))
            }.SelectMany(roots => roots).Distinct(PathComparer)],
            [.. new[]
            {
                _codexRoots,
                homes.SelectMany(home => new[]
                {
                    Path.Combine(home.WindowsHome, ".codex", "sessions"),
                    Path.Combine(home.WindowsHome, ".codex", "archived_sessions")
                }),
                accounts.Where(account => account.Provider == "codex").SelectMany(account => new[]
                {
                    Path.Combine(account.Directory, "sessions"),
                    Path.Combine(account.Directory, "archived_sessions")
                })
            }.SelectMany(roots => roots).Distinct(PathComparer)],
            [_openCodeDatabase, .. homes.Select(home => Path.Combine(home.WindowsHome, ".local", "share", "opencode", "opencode.db"))],
            homes.Select(home => home.Distribution).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            [.. _museRoots.Concat(homes.Select(home => Path.Combine(home.WindowsHome, ".local", "share", "muse", "sessions"))).Distinct(PathComparer)],
            [.. _kiloDatabases.Concat(homes.Select(home => Path.Combine(home.WindowsHome, ".local", "share", "kilo", "kilo.db"))).Distinct(PathComparer)],
            [.. _geminiRoots.Concat(homes.Select(home => Path.Combine(home.WindowsHome, ".gemini", "tmp"))).Distinct(PathComparer)]);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record Sources(string[] ClaudeRoots, string[] CodexRoots, string[] OpenCodeDatabases,
        string[] WslDistributions, string[] MuseRoots, string[] KiloDatabases, string[] GeminiRoots);

    private static string XdgData(string home)
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg;
    }

    private static string MuseSessions(string home) => Path.Combine(XdgData(home), "muse", "sessions");

    private static string KiloDatabase(string home) => Path.Combine(XdgData(home), "kilo", "kilo.db");

    private static string OpenCodeDatabase(string home)
    {
        var configured = Environment.GetEnvironmentVariable("OPENCODE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.Combine(configured, "opencode.db");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(home, ".local", "share") : xdg,
            "opencode", "opencode.db");
    }

    private static IEnumerable<string> ClaudeRoots(string home)
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var path in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return Path.Combine(path, "projects");
        }
        else
        {
            yield return Path.Combine(home, ".claude", "projects");
            yield return Path.Combine(home, ".config", "claude", "projects");
        }
    }

    private static IEnumerable<string> CodexRoots(string home)
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        var root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(home, ".codex") : configured;
        yield return Path.Combine(root, "sessions");
        yield return Path.Combine(root, "archived_sessions");
    }

}
