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
    private static (HttpClient client, string baseUrl) CreateClient(string? portOverride, string? tokenOverride, bool required = true)
    {
        var store = new ConfigStore(applyEnvOverrides: true);
        var config = store.Load();
        if (!string.IsNullOrWhiteSpace(portOverride) && int.TryParse(portOverride, out var p)) config.Port = p;
        if (!string.IsNullOrWhiteSpace(tokenOverride)) config.AuthToken = tokenOverride.Trim();

        var baseUrl = $"http://127.0.0.1:{config.Port}";
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrWhiteSpace(config.AuthToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.AuthToken);
        else if (required)
        {
            Console.Error.WriteLine($"No auth token found at {store.ConfigPath}. Has AgentNotify run at least once?");
            Console.Error.WriteLine("Set AGENTNOTIFY_TOKEN or pass --token.");
        }
        return (client, baseUrl);
    }

    private static bool HasTokenFile()
    {
        try { return !string.IsNullOrWhiteSpace(new ConfigStore(applyEnvOverrides: false).Load().AuthToken); } catch { return false; }
    }

    private static bool TryParseEnum<T>(string? value, out T parsed) where T : struct, Enum
    {
        var normalized = value?.Replace('-', '_').Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out parsed);
    }

    private static string? TryGetCwd()
    {
        try { return Directory.GetCurrentDirectory(); } catch { return null; }
    }

    private static string PrettyError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? body;
            return body;
        }
        catch { return string.IsNullOrWhiteSpace(body) ? "(empty response)" : body; }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            agentnotify — talk to the local AgentNotify broker

            Usage:
              agentnotify <command> [options]
              agentnotify "Title" ["Message"] [options]    shorthand for: send

            Commands:
              send       Create a notification
              list       List notifications
              get        Fetch one notification by id
              resolve    Mark a notification resolved
              dismiss    Dismiss a notification
              health     Check broker health
              relay      Pair with a Relay or verify configured Relay providers
              token      Print the local bearer token
              router     Manage the provider router (router key, router status)
              install-skill  Install the bundled skill for Codex, Claude Code, or OpenCode
              install-harness  Install the auto-notify harness for OpenCode, Codex, or Claude Code
              interactions  Ask a waiting question/permission and collect the answer
              ui         Open the web interface in your browser
              help       Show help (help <command> for details)

            Global options (for send/list/get/...):
              --port N     Override broker port (default 47821)
              --token T    Override bearer token (default from %LOCALAPPDATA%\AgentNotify\config.json)

            Examples:
              agentnotify send --title "Build done" --message "All tests passed" --type success
              agentnotify "Need input" "Which branch should I use?" --type input_required --key my-task
              agentnotify list --unresolved true --limit 20
              agentnotify resolve abc123
              agentnotify health
              agentnotify ui
              agentnotify relay pair --url https://relay.example.com
              agentnotify install-skill codex
              agentnotify install-harness opencode
              agentnotify interactions request --kind permission --prompt "Deploy to prod?" --choice allow-once:"Allow once" --choice deny:"Deny"
            """);
    }

    private static void PrintInstallHarnessHelp()
    {
        Console.WriteLine("""
            agentnotify install-harness — install the auto-notify harness

            Usage:
              agentnotify install-harness <agent> [options]
              agentnotify install harness <agent> [options]

            Agents: opencode, codex, claude, gemini, copilot, cursor, muse, kilo, openclaw, hermes, pi.

            The harness notifies automatically at attention boundaries, without
            relying on the model to remember the skill: permission prompts,
            questions, session completion, and session errors.

            Options:
              --scope user|project   Install for the current user (default) or current project
              --path DIRECTORY      Override the host's harness directory:
                                      OpenCode: the plugin directory itself
                                      Codex:    the .codex directory
                                      Claude:   the .claude directory
                                      Gemini:   the .gemini directory
                                      Copilot:  the .copilot directory (.github for projects)
                                      Cursor:   the .cursor directory
                                      Muse:     the .config/muse directory (.muse for projects)
                                      Kilo:     the plugin directory itself
                                      OpenClaw: the .openclaw directory
                                      Hermes:  the .hermes/plugins directory
                                      Pi:       the extensions directory itself
              --force               Replace changed harness files / rewrite invalid hook JSON
              --dry-run             Print the destination without writing files
              --ask                 Codex/Claude only: permission prompts wait for a broker
                                    answer and return it (verified decision schemas).
                                    Without --ask, permission hooks only notify.

            Default user locations:
              OpenCode     ~/.config/opencode/plugins/agentnotify.js
              Codex        ~/.codex/agentnotify/agentnotify_hook.py + ~/.codex/hooks.json
              Claude Code  ~/.claude/agentnotify/agentnotify_hook.py + ~/.claude/settings.json
              Gemini CLI   ~/.gemini/agentnotify/agentnotify_hook.py + ~/.gemini/settings.json
              Copilot CLI  ~/.copilot/agentnotify/agentnotify_hook.py + ~/.copilot/hooks/agentnotify.json
              Cursor       ~/.cursor/agentnotify/agentnotify_hook.py + ~/.cursor/hooks.json
              Muse Code    ~/.config/muse/agentnotify/agentnotify_hook.py + ~/.config/muse/settings.json
              Kilo Code    ~/.config/kilo/plugin/agentnotify.js
              OpenClaw     ~/.openclaw/agentnotify/agentnotify_openclaw.py (run: python3 ... watch)
              Hermes Agent ~/.hermes/plugins/agentnotify/ (plus config.yaml edits)
              Pi           ~/.pi/agent/extensions/agentnotify.ts

            Hooks only notify; they never approve, deny, or block. Existing hook
            entries are preserved. Restart the host session after installing.
            Muse Code is beta: after installing, start one session and confirm
            no hooks warning appears.
            """);
    }

    private static void PrintInteractionsHelp()
    {
        Console.WriteLine("""
            agentnotify interactions — ask a waiting question and collect the answer

            Usage:
              agentnotify interactions request --prompt TEXT [options]
              agentnotify interactions list [--pending] [--status STATUS] [--agent A] [--project P] [--session S] [--limit N] [--json]
              agentnotify interactions get <id>
              agentnotify interactions wait <id> [--timeout SECONDS]
              agentnotify interactions respond <id> --response-id R --digest D --nonce N [--choice C | --text T] [--source S] [--device D]
              agentnotify interactions cancel <id>
              agentnotify interactions publish <id>
              agentnotify interactions poll-responses [--provider ID] [--json]

            request options:
              --kind permission|single_choice|text   What is being asked (default permission)
              --prompt TEXT            Required. The exact question shown to the human.
              --choice ID:LABEL        Repeatable. 2-12 required unless --kind text.
              --choice-detail ID:DETAIL  Repeatable. Extra detail for one choice.
              --text-max N             Max answer chars for --kind text (default 500)
              --ttl SECONDS            Expiry in 30-3600s (default 600)
              --key KEY                Reuse the pending interaction for a repeated key
              --agent NAME --agent-instance ID --project NAME --session ID --turn ID --native-request ID

            The first valid response wins. A repeated --response-id replays the original
            outcome. Answers must echo the request digest and nonce from 'interactions get'.
            'wait' blocks until the interaction settles or --timeout (1-300s, default 60).
            'publish' re-sends the question to Relay-enabled routes (requests auto-publish).
            'poll-responses' fetches mobile answers from Relay into the broker; run it on
            a schedule until the dispatcher absorbs it (see RELAY_INTERACTIONS.md).
            """);
    }

    private static void PrintSendHelp()
    {
        Console.WriteLine("""
            agentnotify send — create a notification

            Usage:
              agentnotify send --title T --message M [options]
              agentnotify "Title" ["Message"] [options]      shorthand

            Options:
              --title TEXT            Required. Short title.
              --message TEXT          Required. Body.
              --type TYPE             info|success|warning|error|input_required|permission_required|completed|blocked (default info)
              --priority PRI          low|normal|high|critical (default normal)
              --agent NAME            Agent name (default cli)
              --agent-instance ID     Per-run instance id
              --project NAME          Project/repo name
              --key KEY               Deduplication key (updates in place when an active one matches)
              --cwd PATH              Working directory (default current directory)
              --pid N                 Agent process id
              --port N                Override broker port
              --token T               Override bearer token
            """);
    }

    private static void PrintListHelp()
    {
        Console.WriteLine("""
            agentnotify list — list notifications

            Usage:
              agentnotify list [options]

            Options:
              --unresolved BOOL       Only active notifications when true
              --type TYPE             Filter by type
              --status STATUS         Filter by status (active|dismissed|resolved)
              --project NAME          Filter by project
              --agent NAME            Filter by agent
              --limit N               Max rows (default 20, max 500)
              --json                  Output raw JSON
              --port N                Override broker port
              --token T               Override bearer token
            """);
    }

    private static void PrintInstallSkillHelp()
    {
        Console.WriteLine("""
            agentnotify install-skill — install the bundled AgentNotify skill

            Usage:
              agentnotify install-skill <codex|claude|opencode> [options]
              agentnotify install skill <codex|claude|opencode> [options]

            Options:
              --scope user|project   Install for the current user (default) or current project
              --path DIRECTORY      Override the agent's skills root directory
              --wsl DISTRIBUTION    Install for the agent inside a running WSL distribution (Windows)
              --force               Replace changed AgentNotify skill files
              --dry-run             Print the destination without writing files

            Default user locations:
              Codex        ~/.agents/skills/agentnotify
              Claude Code  ~/.claude/skills/agentnotify
              OpenCode     ~/.config/opencode/skill/agentnotify

            Any other agent: pass --path with the folder it loads skills from.

            Run from WSL through the agentnotify wrapper, the skill goes into that distribution's
            home instead of the Windows profile.
            """);
    }

    private static void PrintRelayHelp()
    {
        Console.WriteLine("""
            agentnotify relay — connect this computer to the hosted AgentNotify Relay

            Usage:
              agentnotify relay pair [--name NAME] [--sender-name NAME] [--json]
              agentnotify relay status [--json]

            Pair options:
              --name NAME        Provider profile name
              --sender-name NAME Sender label shown by the Relay (default machine name)
              --json             Emit one JSON object per state transition

            Advanced:
              --url URL          Point at a different Relay build (defaults to the hosted Relay)
              --allow-private    Allow a private/loopback Relay destination, for local testing

            Pairing prints a verification URL and short code, then stores the one-time
            installation credential in AgentNotify's protected provider secret store.
            """);
    }

    private static void PrintRouterHelp()
    {
        Console.WriteLine("""
            agentnotify router — the local provider router

            Usage:
              agentnotify router status
              agentnotify router key
              agentnotify router agents
              agentnotify router connect <agent> [--model <selector>]   codex, claude_code, or an account ID
              agentnotify router disconnect <codex|claude_code>

            status      Whether the router is on, and the base URLs to point an agent at.
                        Read from local config; no network call.
            key         Print the key an agent authenticates with. Empty until the router
                        is turned on in the web interface (agentnotify ui -> Model router).
            agents      Which agents on this machine route through AgentNotify, and every
                        model selector they can be pointed at.
            connect     Write that agent's own configuration so its model picker lists the
                        router's models. A copy of the file is kept first, and the settings
                        it replaces are restored by disconnect. Needs the broker running.
            disconnect  Put that agent's own model settings back.

            Examples:
              agentnotify router status
              agentnotify router agents
              agentnotify router connect codex --model combo/coding
              agentnotify router disconnect claude_code
            """);
    }
}
