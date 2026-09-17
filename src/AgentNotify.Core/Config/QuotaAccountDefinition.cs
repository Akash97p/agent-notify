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
    /// Secondary Codex and Claude Code profiles directly under <paramref name="nativeHome"/>, such as
    /// <c>~/.codex-work</c>. Each is listed only when it looks signed in, so an empty folder adds
    /// no empty card. A null home is not scanned.
    /// </summary>
    public static IReadOnlyList<QuotaAccountDefinition> HomeSecondaryProfiles(string? nativeHome,
        Func<string, string?> labelFor)
    {
        if (string.IsNullOrWhiteSpace(nativeHome)) return [];
        return ScanSecondaryProfiles(nativeHome, (provider, suffix, directory) =>
        {
            var id = HomeAccountId(provider, suffix);
            return new QuotaAccountDefinition(id, provider,
                TryNormalizeLabel(labelFor(id)) ?? "Profile · " + suffix, directory);
        });
    }

    /// <summary>
    /// Secondary Codex and Claude Code profiles directly under each running WSL distribution's
    /// home, such as <c>~/.codex-work</c> inside Ubuntu.
    /// </summary>
    public static IReadOnlyList<QuotaAccountDefinition> WslSecondaryProfiles(IWslEnvironment wsl,
        Func<string, string?> labelFor)
    {
        var accounts = new List<QuotaAccountDefinition>();
        foreach (var home in wsl.RunningHomes())
            accounts.AddRange(ScanSecondaryProfiles(home.WindowsHome, (provider, suffix, directory) =>
            {
                var id = WslSecondaryAccountId(provider, home.Distribution, suffix);
                return new QuotaAccountDefinition(id, provider,
                    TryNormalizeLabel(labelFor(id)) ?? "WSL · " + home.Distribution + " · " + suffix, directory);
            }));
        return accounts;
    }

    /// <summary>
    /// A direct child directory is a secondary profile when its name is <c>.codex-&lt;suffix&gt;</c>
    /// or <c>.codex_&lt;suffix&gt;</c> holding <c>auth.json</c> or a <c>sessions</c> directory, or
    /// <c>.claude-&lt;suffix&gt;</c> or <c>.claude_&lt;suffix&gt;</c> holding <c>.credentials.json</c>
    /// or a <c>projects</c> directory. Only the top level is read. Files, symlinks, and unreadable
    /// entries are ignored without hiding the others, and each home contributes at most 16 profiles.
    /// </summary>
    private static IReadOnlyList<QuotaAccountDefinition> ScanSecondaryProfiles(string homeDirectory,
        Func<string, string, string, QuotaAccountDefinition> shape)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory)) return [];
        string[] entries;
        try { entries = System.IO.Directory.GetDirectories(homeDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return []; }
        Array.Sort(entries, StringComparer.Ordinal);
        var accounts = new List<QuotaAccountDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (accounts.Count >= 16) break;
            try
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
                if (Path.GetFileName(entry) is not string name) continue;
                if (!TrySplitProfileName(name, out var provider, out var suffix)) continue;
                if (!HasProfileMarker(provider, entry)) continue;
                // `.claude-work` and `.claude_work` name the same account: the first one wins.
                var account = shape(provider, suffix, entry);
                if (!seen.Add(account.Id)) continue;
                accounts.Add(account);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return accounts;
    }

    private static bool TrySplitProfileName(string name, out string provider, out string suffix)
    {
        provider = "";
        suffix = "";
        foreach (var (prefix, candidate) in new[]
            { (".codex-", "codex"), (".codex_", "codex"), (".claude-", "claude_code"), (".claude_", "claude_code") })
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = name[prefix.Length..];
            if (!IsValidProfileSuffix(rest)) return false;
            provider = candidate;
            suffix = rest;
            return true;
        }
        return false;
    }

    private static bool HasProfileMarker(string provider, string directory) => provider == "codex"
        ? File.Exists(Path.Combine(directory, "auth.json")) || System.IO.Directory.Exists(Path.Combine(directory, "sessions"))
        : File.Exists(Path.Combine(directory, ".credentials.json")) || System.IO.Directory.Exists(Path.Combine(directory, "projects"));

    /// <summary>
    /// Every discovered account in display order — native secondary profiles, WSL defaults, WSL
    /// secondary profiles — minus removed IDs and any profile whose directory the owner already
    /// monitors by hand or through a built-in default (for example <c>CODEX_HOME</c> pointing at
    /// <c>~/.codex-work</c>). The one shared ordering for Live quota, the account list, and usage.
    /// The native home is scanned only when <paramref name="nativeHome"/> is given.
    /// </summary>
    public static IReadOnlyList<QuotaAccountDefinition> MonitoredDiscoveredAccounts(IWslEnvironment? wsl,
        Func<string, string?> labelFor, IEnumerable<QuotaAccountDefinition?> configured, ICollection<string> removed,
        string? nativeHome = null)
    {
        var added = configured.OfType<QuotaAccountDefinition>().ToArray();
        var builtIn = new[] { ("codex", Default("codex").Directory), ("claude_code", Default("claude_code").Directory) };
        var result = new List<QuotaAccountDefinition>();
        void Add(IEnumerable<QuotaAccountDefinition> candidates)
        {
            foreach (var account in candidates)
            {
                if (removed.Contains(account.Id)) continue;
                if (added.Any(item => item.Provider == account.Provider && SameDirectory(item.Directory, account.Directory))) continue;
                if (result.Any(item => item.Provider == account.Provider && SameDirectory(item.Directory, account.Directory))) continue;
                if (builtIn.Any(defaulted => defaulted.Item1 == account.Provider && SameDirectory(defaulted.Item2, account.Directory))) continue;
                result.Add(account);
            }
        }
        Add(HomeSecondaryProfiles(nativeHome, labelFor));
        if (wsl is not null)
        {
            Add(WslDefaults(wsl, labelFor));
            Add(WslSecondaryProfiles(wsl, labelFor));
        }
        return result;
    }

    public static string WslAccountId(string provider, string distribution) => provider + ":wsl:" + distribution;

    public static string HomeAccountId(string provider, string suffix) => provider + ":home:" + suffix;

    public static string WslSecondaryAccountId(string provider, string distribution, string suffix) =>
        provider + ":wsl:" + distribution + ":" + suffix;

    /// <summary>A secondary profile suffix: 1–40 ASCII letters, digits, '.', '_' or '-'.</summary>
    public static bool IsValidProfileSuffix(string? suffix) =>
        suffix is { Length: > 0 and <= 40 } &&
        suffix.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Whether <paramref name="id"/> names a built-in or discovered account rather than one added by hand.</summary>
    public static bool IsDetectedAccountId(string? id) =>
        id is "codex:default" or "claude_code:default" || IsWslAccountId(id) || IsHomeAccountId(id);

    /// <summary>
    /// Whether <paramref name="id"/> names a discovered WSL profile, such as <c>codex:wsl:Ubuntu</c>
    /// or a secondary profile such as <c>codex:wsl:Ubuntu:work</c>.
    /// </summary>
    public static bool IsWslAccountId(string? id) => DetectedWslDistribution(id) is not null;

    /// <summary>Whether <paramref name="id"/> names a WSL secondary profile, such as <c>codex:wsl:Ubuntu:work</c>.</summary>
    public static bool IsWslSecondaryAccountId(string? id) =>
        DetectedWslDistribution(id) is not null && DetectedSuffix(id) is not null;

    /// <summary>Whether <paramref name="id"/> names a native secondary profile, such as <c>codex:home:work</c>.</summary>
    public static bool IsHomeAccountId(string? id)
    {
        if (id is null) return false;
        foreach (var provider in new[] { "codex", "claude_code" })
        {
            var prefix = provider + ":home:";
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            return IsValidProfileSuffix(id[prefix.Length..]);
        }
        return false;
    }

    /// <summary>Whether <paramref name="id"/> names a native or WSL secondary profile.</summary>
    public static bool IsSecondaryAccountId(string? id) => IsHomeAccountId(id) || IsWslSecondaryAccountId(id);

    /// <summary>The provider of a detected account ID, or null when the ID has no valid detected form.</summary>
    public static string? DetectedProvider(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var colon = id.IndexOf(':');
        if (colon <= 0) return null;
        var provider = id[..colon];
        return provider is "codex" or "claude_code" && IsDetectedAccountId(id) ? provider : null;
    }

    /// <summary>The distribution of a WSL account ID (<c>codex:wsl:Ubuntu</c> or <c>codex:wsl:Ubuntu:work</c>), or null.</summary>
    public static string? DetectedWslDistribution(string? id)
    {
        if (id is null) return null;
        foreach (var provider in new[] { "codex", "claude_code" })
        {
            var prefix = provider + ":wsl:";
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = id[prefix.Length..];
            var colon = rest.IndexOf(':');
            var distribution = colon < 0 ? rest : rest[..colon];
            if (!WslPath.IsValidDistributionName(distribution)) return null;
            return colon < 0 || IsValidProfileSuffix(rest[(colon + 1)..]) ? distribution : null;
        }
        return null;
    }

    /// <summary>The profile suffix of a secondary account ID (<c>codex:home:work</c> or <c>codex:wsl:Ubuntu:work</c>), or null.</summary>
    public static string? DetectedSuffix(string? id)
    {
        if (id is null) return null;
        foreach (var provider in new[] { "codex", "claude_code" })
        {
            var home = provider + ":home:";
            if (id.StartsWith(home, StringComparison.Ordinal))
                return IsValidProfileSuffix(id[home.Length..]) ? id[home.Length..] : null;
            var prefix = provider + ":wsl:";
            if (!id.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = id[prefix.Length..];
            var colon = rest.IndexOf(':');
            return colon >= 0 && WslPath.IsValidDistributionName(rest[..colon]) && IsValidProfileSuffix(rest[(colon + 1)..])
                ? rest[(colon + 1)..] : null;
        }
        return null;
    }

    /// <summary>A sensible label for a removed account whose directory is not currently found.</summary>
    public static string FallbackLabel(string id)
    {
        var distribution = DetectedWslDistribution(id);
        var suffix = DetectedSuffix(id);
        if (distribution is not null && suffix is not null) return "WSL · " + distribution + " · " + suffix;
        if (distribution is not null) return "WSL · " + distribution;
        if (suffix is not null) return "Profile · " + suffix;
        return "Removed account";
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
