using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Core;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Delivery.Forms;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Harness;
using AgentNotify.Core.Logging;
using AgentNotify.Core.Persistence;
using AgentNotify.Insights.Quota;
using AgentNotify.Core.Services;
using AgentNotify.Core.Skills;
using AgentNotify.Insights.Usage;
using AgentNotify.Core.Wsl;
using AgentNotify.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace AgentNotify.Api.WebUi;

public sealed partial class WebUiEndpoints
{
    /// <summary>
    /// Where an account's skill goes. Claude Code reads skills from its own profile directory, so each
    /// account has its own; Codex reads the user-wide <c>~/.agents/skills</c>, which every Codex account
    /// shares.
    /// </summary>
    private static string AccountSkillsRoot(AgentSkillTarget target, QuotaAccountDefinition account) =>
        account.Provider == "claude_code"
            ? Path.Combine(account.Directory, "skills")
            : AgentSkillCatalog.DefaultSkillsRoot(target);

    /// <summary>One account's notification setup: its skill, and whether the hook harness is in its profile.</summary>
    private static object AgentAccountJson(QuotaAccountDefinition account, string? home)
    {
        var isClaude = account.Provider == "claude_code";
        var target = AgentSkillCatalog.Find(isClaude ? "claude" : "codex")!;
        string? root = null;
        var state = "unavailable";
        try
        {
            root = AccountSkillsRoot(target, account);
            state = SkillInstaller.Inspect(root, WebUiSkill.Files(target)) switch
            {
                SkillInstallState.UpToDate => "up_to_date",
                SkillInstallState.Outdated => "outdated",
                _ => "not_installed"
            };
        }
        catch (InvalidOperationException) { }

        var hooksFile = Path.Combine(account.Directory, isClaude ? "settings.json" : "hooks.json");
        var script = Path.Combine(account.Directory, "agentnotify", HarnessCatalog.HookScriptFileName);
        bool harness;
        try { harness = File.Exists(script) && File.Exists(hooksFile) && File.ReadAllText(hooksFile).Contains(HarnessCatalog.HookScriptFileName, StringComparison.Ordinal); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { harness = false; }

        var host = isClaude ? "claude" : "codex";
        var isDefault = account.Id.EndsWith(":default", StringComparison.Ordinal);
        var path = isDefault ? "" : $" --path {QuoteArgument(account.Directory)}";
        return new
        {
            id = account.Id,
            provider = account.Provider,
            display_name = isClaude ? "Claude Code" : "Codex",
            label = account.Label,
            directory = account.Directory,
            display_directory = TildePath(account.Directory, home),
            is_default = isDefault,
            skill = new
            {
                id = target.Id,
                state,
                destination = root is null ? null : SkillInstaller.SkillDirectory(root),
                shared = !isClaude
            },
            harness = new
            {
                installed = harness,
                command = $"agentnotify install-harness {host}{path}",
                ask_command = $"agentnotify install-harness {host}{path} --ask"
            }
        };
    }

    /// <summary>A path under the home directory written as <c>~/…</c>, for display only.</summary>
    internal static string TildePath(string path, string? home)
    {
        if (string.IsNullOrEmpty(home)) return path;
        var relative = Path.GetRelativePath(home, path);
        return relative == "." ? "~" : relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? path : "~/" + relative.Replace('\\', '/');
    }

    private static string QuoteArgument(string value) =>
        value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or '$') ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
}
