using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Protocol;
using AgentNotify.Core.Config;
using AgentNotify.Core.Skills;
using AgentNotify.Core.Wsl;
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Harness;

namespace AgentNotify.Cli;

internal static partial class Program
{
    private static int RunInstallSkill(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            PrintInstallSkillHelp();
            return args.Any(a => a is "--help" or "-h") ? 0 : 1;
        }

        // "claude-code" is accepted because that is what the product is called;
        // the id stays "claude" so existing scripts keep working.
        var requested = args[0].ToLowerInvariant() == "claude-code" ? "claude" : args[0];
        var agent = AgentSkillCatalog.Find(requested);
        if (agent is null || !agent.HasDefaultLocation && requested.ToLowerInvariant() != AgentSkillCatalog.Custom.Id)
            return Fail(
                "install-skill target must be one of: "
                + string.Join(", ", AgentSkillCatalog.WithKnownLocations.Select(t => t.Id))
                + ".");

        var projectScope = false;
        var force = false;
        var dryRun = false;
        string? path = null;
        string? wslName = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--scope":
                    if (i + 1 >= args.Length) return Fail("--scope requires user or project.");
                    var scope = args[++i].ToLowerInvariant();
                    if (scope is not ("user" or "project")) return Fail("--scope must be user or project.");
                    projectScope = scope == "project";
                    break;
                case "--path":
                    if (i + 1 >= args.Length) return Fail("--path requires a skills directory.");
                    path = args[++i];
                    break;
                case "--wsl":
                    if (i + 1 >= args.Length) return Fail("--wsl requires a WSL distribution name.");
                    wslName = args[++i];
                    break;
                case "--force": force = true; break;
                case "--dry-run": dryRun = true; break;
                case "--help": case "-h": PrintInstallSkillHelp(); return 0;
                default: return Fail($"unknown option '{args[i]}' for install-skill.");
            }
        }

        // A Windows agentnotify.exe started from WSL would otherwise install into the Windows profile,
        // where the Linux agent never looks. The WSL wrapper forwards WSL_DISTRO_NAME through WSLENV.
        var explicitWsl = wslName is not null;
        var callerDistribution = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        if (!explicitWsl && path is null && !projectScope && OperatingSystem.IsWindows() &&
            WslPath.IsValidDistributionName(callerDistribution))
            wslName = callerDistribution;
        WslHome? wslHome = null;
        if (wslName is not null)
        {
            if (!OperatingSystem.IsWindows()) return Fail("--wsl is only available on Windows.");
            if (path is not null || projectScope) return Fail("--wsl cannot be combined with --path or --scope project.");
            wslHome = WslDiscovery.Default.RunningHomes().FirstOrDefault(home =>
                string.Equals(home.Distribution, wslName, StringComparison.OrdinalIgnoreCase));
            if (wslHome is null)
                return Fail($"WSL distribution '{wslName}' is not running, or its home directory could not be read.");
            if (!explicitWsl)
                Console.WriteLine($"Started from WSL ({wslHome.Distribution}): installing under {wslHome.LinuxHome}. Pass --path to choose another folder.");
        }

        try
        {
            var skillsRoot = path ?? AgentSkillCatalog.DefaultSkillsRoot(
                agent,
                projectScope ? Directory.GetCurrentDirectory() : null,
                wslHome?.WindowsHome);
            var result = SkillInstaller.Install(
                agent.DisplayName, skillsRoot, SkillPayload.For(agent), force, dryRun);
            if (result.Success)
                Console.WriteLine(result.Message);
            else
                Console.Error.WriteLine(result.Message);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Fail($"Could not install the AgentNotify skill: {ex.Message}");
        }
    }

    private static int RunInstallHarness(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-'))
        {
            PrintInstallHarnessHelp();
            return args.Any(a => a is "--help" or "-h") ? 0 : 1;
        }

        // "claude-code" is accepted because that is what the product is called;
        // the id stays "claude" so existing scripts keep working.
        var lowered = args[0].ToLowerInvariant();
        var requested = lowered == "claude-code" ? "claude"
            : lowered is "github-copilot" or "copilot-cli" ? "copilot"
            : lowered is "muse-code" ? "muse"
            : lowered is "gemini-cli" ? "gemini"
            : lowered is "kilo-code" or "kilocode" ? "kilo"
            : lowered is "hermes-agent" ? "hermes"
            : lowered is "pi-agent" ? "pi"
            : args[0];
        var target = HarnessCatalog.Find(requested);
        if (target is null)
            return Fail(
                "install-harness target must be one of: "
                + string.Join(", ", HarnessCatalog.All.Select(t => t.Id))
                + ".");

        var projectScope = false;
        var force = false;
        var dryRun = false;
        var askMode = false;
        string? path = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--scope":
                    if (i + 1 >= args.Length) return Fail("--scope requires user or project.");
                    var scope = args[++i].ToLowerInvariant();
                    if (scope is not ("user" or "project")) return Fail("--scope must be user or project.");
                    projectScope = scope == "project";
                    break;
                case "--path":
                    if (i + 1 >= args.Length) return Fail("--path requires a directory.");
                    path = args[++i];
                    break;
                case "--force": force = true; break;
                case "--dry-run": dryRun = true; break;
                case "--ask": askMode = true; break;
                case "--help": case "-h": PrintInstallHarnessHelp(); return 0;
                default: return Fail($"unknown option '{args[i]}' for install-harness.");
            }
        }

        if (askMode && target.Id != HarnessCatalog.Codex.Id && target.Id != HarnessCatalog.ClaudeCode.Id)
            return Fail("--ask is only supported for codex and claude: only their permission-request decision schemas are verified.");

        if (path is null && OperatingSystem.IsWindows() &&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")))
            Console.Error.WriteLine(
                "warning: install-harness installs into the Windows profile. Agents running inside WSL will not load it.");

        try
        {
            var harnessDir = path ?? HarnessCatalog.DefaultHarnessDir(
                target,
                projectScope ? Directory.GetCurrentDirectory() : null);
            HarnessInstallResult result = target.Id switch
            {
                var id when id == HarnessCatalog.OpenCode.Id =>
                    HarnessInstaller.InstallOpenCodePlugin(
                        harnessDir, HarnessPayload.OpenCodePlugin(), force, dryRun),
                var id when id == HarnessCatalog.Codex.Id =>
                    HarnessInstaller.InstallCodexHarness(
                        harnessDir, HarnessPayload.HookScript(), force, dryRun, askMode),
                var id when id == HarnessCatalog.ClaudeCode.Id =>
                    HarnessInstaller.InstallClaudeHarness(
                        harnessDir, HarnessPayload.HookScript(), force, dryRun, askMode),
                var id when id == HarnessCatalog.Gemini.Id =>
                    HarnessInstaller.InstallGeminiHarness(
                        harnessDir, HarnessPayload.HookScript(), force, dryRun),
                var id when id == HarnessCatalog.Copilot.Id =>
                    HarnessInstaller.InstallCopilotHarness(
                        harnessDir, HarnessPayload.HookScript(), force, dryRun),
                var id when id == HarnessCatalog.Cursor.Id =>
                    HarnessInstaller.InstallCursorHarness(
                        harnessDir, HarnessPayload.HookScript(), force, dryRun),
                var id when id == HarnessCatalog.Muse.Id =>
                    HarnessInstaller.InstallMuseHarness(
                        harnessDir, HarnessPayload.HookScript(), force, dryRun, projectScope),
                var id when id == HarnessCatalog.Kilo.Id =>
                    HarnessInstaller.InstallKiloPlugin(
                        harnessDir, HarnessPayload.KiloPlugin(), force, dryRun),
                var id when id == HarnessCatalog.OpenClaw.Id =>
                    HarnessInstaller.InstallOpenClawBridge(
                        harnessDir, HarnessPayload.OpenClawWatcher(), force, dryRun),
                var id when id == HarnessCatalog.Hermes.Id =>
                    HarnessInstaller.InstallHermesPlugin(
                        harnessDir, HarnessPayload.HermesPluginYaml(), HarnessPayload.HermesPluginInit(), force, dryRun),
                var id when id == HarnessCatalog.Pi.Id =>
                    HarnessInstaller.InstallPiExtension(
                        harnessDir, HarnessPayload.PiExtension(), force, dryRun),
                _ => null!,
            };
            if (result is null)
                return Fail(
                    "install-harness target must be one of: "
                    + string.Join(", ", HarnessCatalog.All.Select(t => t.Id))
                    + ".");
            if (result.Success)
                Console.WriteLine(result.Message);
            else
                Console.Error.WriteLine(result.Message);
            return result.Success ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return Fail($"Could not install the AgentNotify harness: {ex.Message}");
        }
    }

    // ---- interactions ----

}
