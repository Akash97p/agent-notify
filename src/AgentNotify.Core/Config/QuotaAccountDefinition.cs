using AgentNotify.Core.Wsl;

namespace AgentNotify.Core.Config;

/// <summary>A named local agent profile. Credentials stay in the agent-owned directory.</summary>
public sealed record QuotaAccountDefinition(string Id, string Provider, string Label, string Directory)
{
    public static QuotaAccountDefinition Default(string provider, string? label = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directory = provider == "codex"
            ? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex")
            : Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")?.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
                ?? Path.Combine(home, ".claude");
        return new QuotaAccountDefinition(provider + ":default", provider,
            TryNormalizeLabel(label) ?? "Current account", directory);
    }

    /// <summary>
    /// The Codex and Claude Code profiles found in running WSL distributions. Each is listed only when
    /// its directory exists, so a distribution without that agent adds no empty card.
    /// </summary>
    public static IReadOnlyList<QuotaAccountDefinition> WslDefaults(IWslEnvironment wsl, Func<string, string?> labelFor)
    {
        var accounts = new List<QuotaAccountDefinition>();
        foreach (var home in wsl.RunningHomes())
        {
            foreach (var (provider, folder) in new[] { ("codex", ".codex"), ("claude_code", ".claude") })
            {
                var directory = Path.Combine(home.WindowsHome, folder);
                if (!System.IO.Directory.Exists(directory)) continue;
                var id = WslAccountId(provider, home.Distribution);
                accounts.Add(new QuotaAccountDefinition(id, provider,
                    TryNormalizeLabel(labelFor(id)) ?? "WSL · " + home.Distribution, directory));
            }
        }
        return accounts;
    }

    /// <summary>
    /// The discovered WSL profiles to monitor: those the owner has neither removed nor added by hand
    /// under the same directory.
    /// </summary>
    public static IReadOnlyList<QuotaAccountDefinition> MonitoredWslDefaults(IWslEnvironment wsl,
        Func<string, string?> labelFor, IEnumerable<QuotaAccountDefinition?> configured, ICollection<string> removed)
    {
        var added = configured.OfType<QuotaAccountDefinition>().ToArray();
        return WslDefaults(wsl, labelFor)
            .Where(account => !removed.Contains(account.Id) && !added.Any(item => item.Provider == account.Provider &&
                string.Equals(item.Directory, account.Directory, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static string WslAccountId(string provider, string distribution) => provider + ":wsl:" + distribution;

    /// <summary>Whether <paramref name="id"/> names a built-in or discovered account rather than one added by hand.</summary>
    public static bool IsDetectedAccountId(string? id) => id is "codex:default" or "claude_code:default" || IsWslAccountId(id);

    /// <summary>Whether <paramref name="id"/> names a discovered WSL profile, such as <c>codex:wsl:Ubuntu</c>.</summary>
    public static bool IsWslAccountId(string? id)
    {
        if (id is null) return false;
        foreach (var provider in new[] { "codex", "claude_code" })
            if (id.StartsWith(provider + ":wsl:", StringComparison.Ordinal))
                return WslPath.IsValidDistributionName(id[(provider.Length + 5)..]);
        return false;
    }

    public static QuotaAccountDefinition Create(string? provider, string? label, string? directory,
        IEnumerable<QuotaAccountDefinition> existing)
    {
        if (provider is not ("codex" or "claude_code"))
            throw new ArgumentException("Choose Codex or Claude Code.");
        label = NormalizeLabel(label);
        var path = NormalizeDirectory(directory);
        if (existing.Any(item => item.Provider == provider && SameDirectory(item.Directory, path)))
            throw new ArgumentException("That profile directory is already listed for this provider.");
        return new QuotaAccountDefinition("q_" + Guid.NewGuid().ToString("N"), provider, label, path);
    }

    public static bool SameDirectory(string? left, string? right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// The absolute form of a profile directory an owner entered: a <c>\\wsl.localhost</c> share path,
    /// or a directory under the home directory, where <c>~/</c> is expanded.
    /// </summary>
    public static string NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 1024 || directory.Any(char.IsControl))
            throw new ArgumentException("Enter the agent's absolute profile directory.");
        directory = directory.Trim();

        // A profile inside WSL lives on the \\wsl.localhost share, outside the Windows home.
        if (OperatingSystem.IsWindows() && WslPath.TryParse(directory, out var distribution, out var linuxPath))
        {
            if (linuxPath == "/")
                throw new ArgumentException("Enter the agent's profile directory inside the WSL distribution.");
            return WslPath.ToWindows(@"\\wsl.localhost\" + distribution, linuxPath);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = directory.StartsWith("~/", StringComparison.Ordinal) || directory.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, directory[2..]) : directory;
        string path;
        try { path = Path.GetFullPath(expanded); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("Enter a valid profile directory."); }
        if (!Path.IsPathFullyQualified(expanded) || string.IsNullOrWhiteSpace(home))
            throw new ArgumentException("Enter an absolute profile directory under your home directory.");
        var relative = Path.GetRelativePath(home, path);
        if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new ArgumentException("The profile directory must be under your home directory.");
        return path;
    }

    public static string NormalizeLabel(string? label) => TryNormalizeLabel(label)
        ?? throw new ArgumentException("Give this account a name of 1–60 characters.");

    public static string? TryNormalizeLabel(string? label)
    {
        label = label?.Trim();
        return string.IsNullOrWhiteSpace(label) || label.Length > 60 || label.Any(char.IsControl)
            ? null : label;
    }

    public static bool TryNormalizeExisting(QuotaAccountDefinition? value,
        IEnumerable<QuotaAccountDefinition> existing, out QuotaAccountDefinition normalized)
    {
        normalized = null!;
        if (value is null || value.Id is not { Length: 34 } ||
            !value.Id.StartsWith("q_", StringComparison.Ordinal) || !value.Id[2..].All(Uri.IsHexDigit) ||
            existing.Any(item => item.Id.Equals(value.Id, StringComparison.OrdinalIgnoreCase))) return false;
        try
        {
            var validated = Create(value.Provider, value.Label, value.Directory, existing);
            normalized = validated with { Id = value.Id.ToLowerInvariant() };
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
