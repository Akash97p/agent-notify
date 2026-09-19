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

/// <summary>Web UI routes: agents.</summary>
public sealed partial class WebUiEndpoints
{
    private void MapAgents()
    {
        // The Codex and Claude Code accounts on this computer: the same list Live quota monitors.
        IReadOnlyList<QuotaAccountDefinition> NativeAgentAccounts() =>
            QuotaAccountDefinition.Monitored(config, wsl, nativeHome).Where(account => account.IsNative).ToList();

        app.MapGet($"{BasePath}/api/agents", () => Results.Json(new
        {
            // Every Codex and Claude Code account, each with its own skill and harness state.
            accounts = NativeAgentAccounts().Select(account => AgentAccountJson(account, nativeHome)),
            // Skills for this user's other agents, then for agents inside each running WSL distribution.
            skills = AgentSkillCatalog.WithKnownLocations.Where(target => target.Id is not ("codex" or "claude"))
                .Select(target => SkillJson(target, null))
                .Concat(wsl.RunningHomes().SelectMany(home =>
                    AgentSkillCatalog.WithKnownLocations.Select(target => SkillJson(target, home)))),
            harnesses = HarnessCatalog.All.Select(target => new
            {
                id = target.Id,
                display_name = target.DisplayName,
                note = target.Note,
                command = $"agentnotify install-harness {target.Id}",
                ask_command = target.Id is "codex" or "claude" ? $"agentnotify install-harness {target.Id} --ask" : null
            })
        }, JsonOptions));

        app.MapPost($"{BasePath}/api/agents/skills/{{id}}", async (string id, HttpContext http) =>
        {
            var target = AgentSkillCatalog.Find(id);
            if (target is null || !target.HasDefaultLocation)
                return Error("Unknown agent.", StatusCodes.Status404NotFound);
            var body = await ReadAsync<SkillBody>(http);
            // A Claude Code account keeps its skills inside its own profile directory; the account is
            // named by ID and its directory comes from the account list, never from the request.
            if (!string.IsNullOrEmpty(body?.Account))
            {
                var account = NativeAgentAccounts().FirstOrDefault(item => item.Id == body.Account);
                if (account is null) return Error("That account is not on this computer.", StatusCodes.Status404NotFound);
                try
                {
                    var root = AccountSkillsRoot(target, account);
                    var installed = SkillInstaller.Install(target.DisplayName, root, WebUiSkill.Files(target), body.Force, dryRun: false);
                    return Results.Json(new { success = installed.Success, changed = installed.Changed, message = installed.Message },
                        statusCode: installed.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict, options: JsonOptions);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return Error(exception.Message);
                }
            }
            // A WSL install names the distribution, never a path; the home comes from discovery.
            WslHome? home = null;
            if (!string.IsNullOrEmpty(body?.Wsl))
            {
                home = wsl.RunningHomes().FirstOrDefault(item =>
                    string.Equals(item.Distribution, body.Wsl, StringComparison.OrdinalIgnoreCase));
                if (home is null) return Error("That WSL distribution is not running.", StatusCodes.Status404NotFound);
            }
            try
            {
                var root = AgentSkillCatalog.DefaultSkillsRoot(target, homeDirectory: home?.WindowsHome);
                var result = SkillInstaller.Install(target.DisplayName, root, WebUiSkill.Files(target), body?.Force == true, dryRun: false);
                return Results.Json(new { success = result.Success, changed = result.Changed, message = result.Message, skill = SkillJson(target, home) },
                    statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status409Conflict, options: JsonOptions);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Error(exception.Message);
            }
        });
    }
}
