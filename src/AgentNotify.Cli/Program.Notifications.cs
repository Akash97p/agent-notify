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

}
