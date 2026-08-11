using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentNotify.Contracts;
using AgentNotify.Core.Config;

namespace AgentNotify.Cli;

/// <summary>Hand-rolled CLI — no extra package so the publish stays lean.
/// Every command talks to the local broker via HTTP; the broker is source of truth.</summary>
internal static class Program
{
    private static readonly string[] KnownCommands = ["send", "list", "get", "resolve", "dismiss", "health", "token", "help", "--help", "-h", "--version"];

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
                "token" => RunToken(args[1..]),
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
    }

    // ---- send ----

    private static async Task<int> RunSend(string[] args)
    {
        string? title = null, message = null, agent = null, agentInstance = null, project = null, key = null, cwd = null;
        NotificationType type = NotificationType.Info;
        NotificationPriority priority = NotificationPriority.Normal;
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
                    if (!TryParseEnum(Next(), out type)) return Fail("--type must be info, success, warning, error, input_required, permission_required, completed, or blocked.");
                    break;
                case "--priority":
                    if (!TryParseEnum(Next(), out priority)) return Fail("--priority must be low, normal, high, or critical.");
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

    private static async Task<int> RunList(string[] args)
    {
        string? type = null, status = null, project = null, agent = null, portOverride = null, tokenOverride = null;
        string? unresolved = null;
        int limit = 20;
        bool jsonOut = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--type": type = Next(); break;
                case "--status": status = Next(); break;
                case "--project": project = Next(); break;
                case "--agent": agent = Next(); break;
                case "--unresolved":
                    if (i + 1 < args.Length && bool.TryParse(args[i + 1], out var unresolvedValue))
                    {
                        i++;
                        unresolved = unresolvedValue ? "true" : "false";
                    }
                    else
                    {
                        unresolved = "true";
                    }
                    break;
                case "--limit": int.TryParse(Next(), out limit); break;
                case "--json": jsonOut = true; break;
                case "--port": portOverride = Next(); break;
                case "--token": tokenOverride = Next(); break;
                case "--help": case "-h": PrintListHelp(); return 0;
                default:
                    if (a.StartsWith('-')) return Fail($"unknown option '{a}' for list.");
                    break;
            }
        }

        // Handle --unresolved without value as true
        var qs = new List<string>();
        if (unresolved is not null) qs.Add($"unresolved={Uri.EscapeDataString(unresolved)}");
        if (type is not null) qs.Add($"type={Uri.EscapeDataString(type)}");
        if (status is not null) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (project is not null) qs.Add($"project={Uri.EscapeDataString(project)}");
        if (agent is not null) qs.Add($"agent={Uri.EscapeDataString(agent)}");
        qs.Add($"limit={limit}");
        var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";

        var (client, baseUrl) = CreateClient(portOverride, tokenOverride);
        using (client)
        {
            var resp = await client.GetAsync($"{baseUrl}/v1/notifications{query}");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
                return 1;
            }
            if (jsonOut)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { Console.WriteLine(body); }
            }
            else
            {
                try
                {
                    var items = JsonSerializer.Deserialize<List<NotificationDto>>(body, Json.Options) ?? [];
                    if (items.Count == 0)
                        Console.WriteLine("(no notifications)");
                    else
                        foreach (var n in items)
                            Console.WriteLine($"{n.Id}  [{n.Type}/{n.Priority}] {n.Status,-9}  {n.Title}  ({n.Agent})");
                }
                catch { Console.WriteLine(body); }
            }
            return 0;
        }
    }

    // ---- get ----

    private static async Task<int> RunGet(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-')) return Fail("get requires an <id>. Usage: agentnotify get <id>");
        var id = args[0];
        string? portOverride = null, tokenOverride = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length) portOverride = args[++i];
            else if (args[i] == "--token" && i + 1 < args.Length) tokenOverride = args[++i];
        }
        var (client, baseUrl) = CreateClient(portOverride, tokenOverride);
        using (client)
        {
            var resp = await client.GetAsync($"{baseUrl}/v1/notifications/{Uri.EscapeDataString(id)}");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
                return resp.StatusCode == HttpStatusCode.NotFound ? 3 : 1;
            }
            try
            {
                using var doc = JsonDocument.Parse(body);
                Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { Console.WriteLine(body); }
            return 0;
        }
    }

    // ---- resolve / dismiss ----

    private static async Task<int> RunResolve(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-')) return Fail("resolve requires an <id>. Usage: agentnotify resolve <id>");
        return await RunPatchStatus(args[0], NotificationStatus.Resolved, args[1..]);
    }

    private static async Task<int> RunDismiss(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-')) return Fail("dismiss requires an <id>. Usage: agentnotify dismiss <id>");
        var id = args[0];
        string? portOverride = null, tokenOverride = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length) portOverride = args[++i];
            else if (args[i] == "--token" && i + 1 < args.Length) tokenOverride = args[++i];
        }
        var (client, baseUrl) = CreateClient(portOverride, tokenOverride);
        using (client)
        {
            // Preferred convenience endpoint; fall back to PATCH if not present.
            var resp = await client.PostAsync($"{baseUrl}/v1/notifications/{Uri.EscapeDataString(id)}/dismiss",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                // Try PATCH as fallback.
                return await RunPatchStatus(id, NotificationStatus.Dismissed, args[1..]);
            }
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
                return 1;
            }
            try { using var doc = JsonDocument.Parse(body); Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true })); } catch { Console.WriteLine(body); }
            return 0;
        }
    }

    private static async Task<int> RunPatchStatus(string id, NotificationStatus status, string[] extraArgs)
    {
        string? portOverride = null, tokenOverride = null;
        for (var i = 0; i < extraArgs.Length; i++)
        {
            if (extraArgs[i] == "--port" && i + 1 < extraArgs.Length) portOverride = extraArgs[++i];
            else if (extraArgs[i] == "--token" && i + 1 < extraArgs.Length) tokenOverride = extraArgs[++i];
        }
        var (client, baseUrl) = CreateClient(portOverride, tokenOverride);
        using (client)
        {
            var req = new UpdateNotificationRequest { Status = status };
            var json = JsonSerializer.Serialize(req, Json.Options);
            var msg = new HttpRequestMessage(new HttpMethod("PATCH"), $"{baseUrl}/v1/notifications/{Uri.EscapeDataString(id)}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            var resp = await client.SendAsync(msg);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
                return resp.StatusCode == HttpStatusCode.NotFound ? 3 : 1;
            }
            try { using var doc = JsonDocument.Parse(body); Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true })); } catch { Console.WriteLine(body); }
            return 0;
        }
    }

    // ---- health / token / help ----

    private static async Task<int> RunHealth(string[] args)
    {
        string? portOverride = null, tokenOverride = null;
        foreach (var a in args)
        {
            if (a == "--port") { /* handled positionally below */ }
        }
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length) portOverride = args[++i];
            else if (args[i] == "--token" && i + 1 < args.Length) tokenOverride = args[++i];
        }
        var wantAuth = tokenOverride is not null || HasTokenFile();
        var (client, baseUrl) = CreateClient(portOverride, tokenOverride, required: wantAuth);
        using (client)
        {
            var path = wantAuth ? $"{baseUrl}/v1/health" : $"{baseUrl}/health";
            try
            {
                var resp = await client.GetAsync(path);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    // If /v1/health 401'd, try the unauthenticated probe.
                    if (wantAuth && resp.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        var probe = await client.GetAsync($"{baseUrl}/health");
                        var pbody = await probe.Content.ReadAsStringAsync();
                        Console.WriteLine(pbody);
                        return probe.IsSuccessStatusCode ? 0 : 1;
                    }
                    Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
                    return 1;
                }
                try { using var doc = JsonDocument.Parse(body); Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true })); } catch { Console.WriteLine(body); }
                return 0;
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Could not reach AgentNotify at {baseUrl}: {ex.Message}");
                Console.Error.WriteLine("Is the app running? Check the tray icon.");
                return 1;
            }
        }
    }

    private static int RunToken(string[] args)
    {
        try
        {
            var store = new ConfigStore();
            var config = store.Load();
            if (string.IsNullOrWhiteSpace(config.AuthToken))
            {
                Console.Error.WriteLine("No token found. Has AgentNotify run at least once? Look at: " + store.ConfigPath);
                return 1;
            }
            Console.WriteLine(config.AuthToken);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunHelp(string? topic)
    {
        if (topic is not null)
        {
            switch (topic.ToLowerInvariant())
            {
                case "send": PrintSendHelp(); return 0;
                case "list": PrintListHelp(); return 0;
                case "get": Console.WriteLine("Usage: agentnotify get <id> [--port N] [--token T]"); return 0;
                case "resolve": Console.WriteLine("Usage: agentnotify resolve <id> [--port N] [--token T]"); return 0;
                case "dismiss": Console.WriteLine("Usage: agentnotify dismiss <id> [--port N] [--token T]"); return 0;
            }
        }
        PrintUsage();
        return 0;
    }

    private static int RunVersion()
    {
        var v = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        Console.WriteLine($"agentnotify {v}");
        return 0;
    }

    // ---- helpers ----

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
              token      Print the local bearer token
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
}
