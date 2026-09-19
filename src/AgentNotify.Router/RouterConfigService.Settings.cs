using System.Text.RegularExpressions;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Router;

public sealed partial class RouterConfigService
{
    public async Task<RouterSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        return await _repository.GetSettingsAsync(ct).ConfigureAwait(false);
    }

    public async Task SetDefaultRouteAsync(string? defaultRoute, CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var current = await _repository.GetSettingsAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(defaultRoute))
        {
            await _repository.SetSettingsAsync(current with { DefaultRoute = null }, ct).ConfigureAwait(false);
            Invalidate();
            return;
        }
        var trimmed = defaultRoute.Trim();
        ValidateDefaultRoute(trimmed, await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false),
            await _repository.ListRoutesAsync(ct).ConfigureAwait(false));
        await _repository.SetSettingsAsync(current with { DefaultRoute = trimmed }, ct).ConfigureAwait(false);
        Invalidate();
    }

    /// <summary>
    /// Keeps one ChatGPT-plan provider per signed-in Codex account, once the owner has added the plan
    /// at all. Codex accounts are one list (<c>QuotaAccountDefinition.Monitored</c>, what Insights
    /// shows), so a second login is a second provider without being added by hand; with smart routing
    /// on, the same model then moves from one account to the next when the first runs out. A provider
    /// that duplicates another's account is pointed at an account nothing uses yet, keeping its slug so
    /// routes and agent pickers that name it still work. Returns whether anything changed.
    /// </summary>
    public async Task<bool> SyncCodexAccountsAsync(IReadOnlyList<CodexPlanAccount> accounts, CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var plans = (await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false))
            .Where(upstream => upstream.Auth == RouterAuth.CodexChatGpt)
            .OrderBy(upstream => upstream.CreatedAt)
            .ToList();
        var signedIn = accounts.Where(account => account.SignedIn).ToList();
        if (plans.Count == 0 || signedIn.Count == 0) return false;

        var defaultDirectory = accounts.FirstOrDefault(account => account.IsDefault)?.Directory;
        string? DirectoryOf(StoredRouterUpstream plan) =>
            RouterCredentialRef.ProfileDirectory(plan.CredentialRef) ?? defaultDirectory;
        bool Same(string? a, string? b) =>
            a is not null && b is not null && QuotaAccountDefinition.SameDirectory(a, b);

        var changed = false;
        var used = new List<string>();
        var duplicates = new List<StoredRouterUpstream>();
        foreach (var plan in plans)
        {
            var directory = DirectoryOf(plan);
            if (directory is not null && used.Any(item => Same(item, directory))) duplicates.Add(plan);
            else if (directory is not null) used.Add(directory);
        }

        var unused = new Queue<CodexPlanAccount>(signedIn.Where(account => !used.Any(item => Same(item, account.Directory))));
        foreach (var duplicate in duplicates)
        {
            if (!unused.TryDequeue(out var account)) break;
            await UpdateUpstreamAsync(duplicate.Id, duplicate.Slug, PlanLabel(account), duplicate.Wire, duplicate.BaseUrl,
                null, duplicate.Models, ct: ct, credentialRef: RouterCredentialRef.ProfilePrefix + account.Directory,
                modelWires: duplicate.ModelWires).ConfigureAwait(false);
            changed = true;
        }

        var template = plans[0];
        var slugs = (await _repository.ListUpstreamsAsync(ct).ConfigureAwait(false)).Select(upstream => upstream.Slug).ToHashSet(StringComparer.Ordinal);
        while (unused.TryDequeue(out var account))
        {
            var slug = FreeSlug("chatgpt-" + SlugPart(account), slugs);
            slugs.Add(slug);
            await CreateUpstreamAsync(slug, PlanLabel(account), template.Wire, template.BaseUrl, null, template.Models,
                enabled: true, ct: ct, auth: RouterAuth.CodexChatGpt, modelWires: template.ModelWires,
                credentialRef: RouterCredentialRef.ProfilePrefix + account.Directory).ConfigureAwait(false);
            changed = true;
        }
        return changed;
    }

    private static string PlanLabel(CodexPlanAccount account) =>
        account.IsDefault ? "ChatGPT plan" : $"ChatGPT plan · {account.Label}";

    /// <summary><c>second</c> for <c>~/.codex-second</c>; otherwise the account's label, made slug-safe.</summary>
    private static string SlugPart(CodexPlanAccount account)
    {
        var name = Path.GetFileName(account.Directory.TrimEnd('/', '\\')).TrimStart('.');
        if (name.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) name = name[5..];
        if (string.IsNullOrWhiteSpace(name.Trim('-', '_'))) name = account.Label;
        var cleaned = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        return cleaned.Length == 0 ? "account" : cleaned[..Math.Min(cleaned.Length, 20)].Trim('-');
    }

    private static string FreeSlug(string wanted, ISet<string> taken)
    {
        if (!taken.Contains(wanted)) return wanted;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{wanted}-{n}")) return $"{wanted}-{n}";
    }

}
