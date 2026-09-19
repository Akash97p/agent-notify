using AgentNotify.Core.Config;

namespace AgentNotify.Insights.Quota;

/// <summary>Secret-free quota data shaped for the native macOS status item.</summary>
public static class MenuBarQuotaProjector
{
    public static MenuBarQuotaProjection Project(LiveQuotaReport report, MacMenuBarSettings settings)
    {
        var accounts = report.Providers
            .Where(provider => provider.Provider is "codex" or "claude_code")
            .ToArray();
        var selected = settings.AccountIds.Count == 0
            ? accounts
            : accounts.Where(account => settings.AccountIds.Contains(account.AccountId, StringComparer.Ordinal)).ToArray();
        var headline = selected
            .Where(account => account.Status is "ok" or "stale")
            .SelectMany(account => account.Windows
                .Where(window => window.DurationMinutes == 300)
                .Select(window => new MenuBarQuotaHeadline(
                    account.AccountId,
                    account.AccountLabel,
                    account.Provider,
                    window.Label,
                    window.RemainingPercent,
                    window.ResetsAt,
                    account.Status == "stale")))
            .OrderBy(item => item.RemainingPercent)
            .ThenBy(item => item.AccountLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.WindowLabel, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return new MenuBarQuotaProjection(
            "1",
            report.CheckedAt,
            new MenuBarQuotaSettings(settings.Enabled, settings.RefreshMinutes, settings.AccountIds.ToArray()),
            headline,
            accounts);
    }
}

public sealed record MenuBarQuotaProjection(
    string ContractVersion,
    DateTimeOffset CheckedAt,
    MenuBarQuotaSettings Settings,
    MenuBarQuotaHeadline? Headline,
    IReadOnlyList<LiveQuotaSnapshot> Accounts);

public sealed record MenuBarQuotaSettings(bool Enabled, int RefreshMinutes, IReadOnlyList<string> AccountIds);

public sealed record MenuBarQuotaHeadline(
    string AccountId,
    string AccountLabel,
    string Provider,
    string WindowLabel,
    double RemainingPercent,
    DateTimeOffset? ResetsAt,
    bool Stale);
