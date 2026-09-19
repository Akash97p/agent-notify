namespace AgentNotify.Core.Config;

/// <summary>Owner-controlled presentation for the native macOS quota menu-bar client.</summary>
public sealed class MacMenuBarSettings
{
    /// <summary>Runs the native menu-bar client when the portable broker is running on macOS.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the client asks the broker for a fresh projection.</summary>
    public int RefreshMinutes { get; set; } = 5;

    /// <summary>
    /// Accounts considered for the headline percentage. Empty means every monitored Codex and
    /// Claude Code account. The menu still lists every account regardless of this filter.
    /// </summary>
    public List<string> AccountIds { get; set; } = [];

    public void ApplyDefaults()
    {
        RefreshMinutes = Math.Clamp(RefreshMinutes, 5, 60);
        AccountIds = (AccountIds ?? [])
            .Where(IsAccountId)
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToList();
    }

    private static bool IsAccountId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl)) return false;
        return value.StartsWith("codex:", StringComparison.Ordinal) ||
               value.StartsWith("claude_code:", StringComparison.Ordinal) ||
               value is { Length: 34 } && value.StartsWith("q_", StringComparison.Ordinal) &&
               value[2..].All(Uri.IsHexDigit);
    }
}
