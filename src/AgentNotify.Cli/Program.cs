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

/// <summary>Hand-rolled CLI — no extra package so the publish stays lean.
/// Every command talks to the local broker via HTTP; the broker is source of truth.</summary>
internal static partial class Program
{
    private static readonly string[] KnownCommands = ["send", "list", "get", "resolve", "dismiss", "health", "relay", "token", "install-skill", "install-harness", "install", "interactions", "ui", "router", "help", "--help", "-h", "--version"];

    internal static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        try
        {
            // Shorthand: `agentnotify "Title" ["Message"] [--type ...]` without a subcommand.
            var first = args[0].TrimStart('-');
            if (!KnownCommands.Contains(args[0], StringComparer.OrdinalIgnoreCase) &&
                !KnownCommands.Contains(first, StringComparer.OrdinalIgnoreCase))
            {
                return await RunSend(args);
            }

            var cmd = args[0].ToLowerInvariant().TrimStart('-');
            return cmd switch
            {
                "send" => await RunSend(args[1..]),
                "list" => await RunList(args[1..]),
                "get" => await RunGet(args[1..]),
                "resolve" => await RunResolve(args[1..]),
                "dismiss" => await RunDismiss(args[1..]),
                "health" => await RunHealth(args[1..]),
                "relay" => await RunRelay(args[1..]),
                "token" => RunToken(args[1..]),
                "install-skill" => RunInstallSkill(args[1..]),
                "install-harness" => RunInstallHarness(args[1..]),
                "install" when args.Length > 1 && args[1].Equals("skill", StringComparison.OrdinalIgnoreCase) => RunInstallSkill(args[2..]),
                "install" when args.Length > 1 && args[1].Equals("harness", StringComparison.OrdinalIgnoreCase) => RunInstallHarness(args[2..]),
                "install" => Fail("Usage: agentnotify install <skill|harness> <agent> [options]"),
                "interactions" => await RunInteractions(args[1..]),
                "router" => await RunRouter(args[1..]),
                "ui" => await RunUi(args[1..]),
                "help" or "--help" or "h" => RunHelp(args.Length > 1 ? args[1] : null),
                "version" => RunVersion(),
                _ => Fail($"unknown command '{args[0]}'. Run 'agentnotify help' for usage.")
            };
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Could not reach AgentNotify: {ex.Message}\nIs the tray app running?");
        }
        catch (TaskCanceledException)
        {
            return Fail("AgentNotify did not respond before the request timed out.");
        }
        catch (RelayPairingException ex)
        {
            return Fail(ex.Message);
        }
    }

    // ---- send ----

    private static async Task<int> RunSend(string[] args)
    {
        string? title = null, message = null, agent = null, agentInstance = null, project = null, key = null, cwd = null;
        string type = NotificationTypes.Info;
        NotificationPriority? priority = null;
        long? pid = null;
        string? portOverride = null, tokenOverride = null;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--title": title = Next(); break;
                case "--message": case "--msg": message = Next(); break;
                case "--type":
                    type = NotificationTypes.Normalize(Next()) ?? "";
                    if (type.Length == 0) return Fail("--type must be a valid identifier containing letters, numbers, underscores, or hyphens (maximum 64 characters).");
                    break;
                case "--priority":
                    if (!TryParseEnum<NotificationPriority>(Next(), out var parsedPriority)) return Fail("--priority must be low, normal, high, or critical.");
                    priority = parsedPriority;
                    break;
                case "--agent": agent = Next(); break;
                case "--agent-instance": agentInstance = Next(); break;
                case "--project": project = Next(); break;
                case "--key": key = Next(); break;
                case "--cwd": cwd = Next(); break;
                case "--pid": pid = long.TryParse(Next(), out var p) ? p : null; break;
                case "--port": portOverride = Next(); break;
                case "--token": tokenOverride = Next(); break;
                case "--help": case "-h": PrintSendHelp(); return 0;
                default:
                    if (a.StartsWith('-')) return Fail($"unknown option '{a}' for send. Run 'agentnotify help send'.");
                    positional.Add(a);
                    break;
            }
        }

        // Shorthand positional: title [message]
        if (positional.Count >= 1 && title is null) title = positional[0];
        if (positional.Count >= 2 && message is null) message = positional[1];
        // Single positional becomes both title and message if only title was set
        if (title is not null && message is null && positional.Count == 1)
            message = title;

        if (string.IsNullOrWhiteSpace(title)) return Fail("send requires --title (or a positional title).");
        if (string.IsNullOrWhiteSpace(message)) return Fail("send requires --message (or a second positional).");

        // Auto-fill agent/cwd like a real agent would, but don't overwrite explicit values.
        agent ??= Environment.GetEnvironmentVariable("AGENTNOTIFY_AGENT") ?? "cli";
        cwd ??= TryGetCwd();

        var req = new CreateNotificationRequest
        {
            Title = title.Trim(),
            Message = message.Trim(),
            Type = type,
            Priority = priority,
            Agent = agent,
            AgentInstance = agentInstance,
            Project = project,
            Key = key,
            Cwd = cwd,
            Pid = pid
        };

        var (client, baseUrl) = CreateClient(portOverride, tokenOverride);
        using (client)
        {
            var json = JsonSerializer.Serialize(req, Json.Options);
            var resp = await client.PostAsync($"{baseUrl}/v1/notifications",
                new StringContent(json, Encoding.UTF8, "application/json"));
            return await HandleCreateResponse(resp);
        }
    }

    private static async Task<int> HandleCreateResponse(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
            return (int)resp.StatusCode is 401 or 403 ? 2 : 1;
        }
        // Pretty-print the created notification
        try
        {
            using var doc = JsonDocument.Parse(body);
            Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { Console.WriteLine(body); }
        return 0;
    }

    // ---- list ----

}
