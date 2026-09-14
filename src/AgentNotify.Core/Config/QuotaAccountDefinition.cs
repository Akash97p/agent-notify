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

    public static QuotaAccountDefinition Create(string? provider, string? label, string? directory,
        IEnumerable<QuotaAccountDefinition> existing)
    {
        if (provider is not ("codex" or "claude_code"))
            throw new ArgumentException("Choose Codex or Claude Code.");
        label = NormalizeLabel(label);
        if (string.IsNullOrWhiteSpace(directory) || directory.Length > 1024 || directory.Any(char.IsControl))
            throw new ArgumentException("Enter the agent's absolute profile directory.");

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

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (existing.Any(item => item.Provider == provider &&
            string.Equals(item.Directory, path, comparison)))
            throw new ArgumentException("That profile directory is already listed for this provider.");
        return new QuotaAccountDefinition("q_" + Guid.NewGuid().ToString("N"), provider, label, path);
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
