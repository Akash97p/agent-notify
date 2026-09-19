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
    private static async Task<int> RunRouter(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintRouterHelp();
            return args.Length == 0 ? 1 : 0;
        }
        return args[0].ToLowerInvariant() switch
        {
            "key" => RunRouterKey(args[1..]),
            "status" => RunRouterStatus(args[1..]),
            "agents" => await RunRouterAgents(),
            "connect" => await RunRouterConnect(args[1..]),
            "disconnect" => await RunRouterDisconnect(args[1..]),
            _ => Fail("Usage: agentnotify router <key|status|agents|connect|disconnect> [options]")
        };
    }

    private static int RunRouterKey(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h")) { PrintRouterHelp(); return 0; }
        try
        {
            var store = new ConfigStore();
            var config = store.Load();
            if (string.IsNullOrWhiteSpace(config.RouterKey))
            {
                Console.Error.WriteLine("The router has no key yet. Turn the router on in the web interface (agentnotify ui → Router).");
                return 1;
            }
            Console.WriteLine(config.RouterKey);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int RunRouterStatus(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h")) { PrintRouterHelp(); return 0; }
        try
        {
            var store = new ConfigStore();
            var config = store.Load();
            Console.WriteLine($"enabled: {(config.RouterEnabled ? "yes" : "no")}");
            Console.WriteLine($"base_url: http://127.0.0.1:{config.Port}/router/v1");
            Console.WriteLine($"anthropic_base_url: http://127.0.0.1:{config.Port}/router");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>Lists the agents on this machine and whether their own model picker routes here.</summary>
    private static async Task<int> RunRouterAgents()
    {
        var response = await RouterUiGetAsync("agents");
        if (response is null) return 1;
        var agents = response.Value.GetProperty("agents");
        foreach (var agent in agents.EnumerateArray())
        {
            var connected = agent.GetProperty("connected").GetBoolean();
            var blocked = agent.GetProperty("blocked");
            Console.WriteLine($"{agent.GetProperty("id").GetString()}  {(connected ? "connected" : "not connected")}");
            Console.WriteLine($"    config: {agent.GetProperty("config_path").GetString()}");
            if (connected && agent.GetProperty("selected_model").ValueKind == JsonValueKind.String)
                Console.WriteLine($"    model:  {agent.GetProperty("selected_model").GetString()}");
            if (blocked.ValueKind == JsonValueKind.String)
                Console.WriteLine($"    note:   {blocked.GetString()}");
        }

        var selectable = response.Value.GetProperty("selectable");
        if (selectable.GetArrayLength() > 0)
        {
            Console.WriteLine("selectable models:");
            foreach (var model in selectable.EnumerateArray())
                Console.WriteLine($"    {model.GetString()}");
        }
        return 0;
    }

    private static async Task<int> RunRouterConnect(string[] args)
    {
        if (args.Length == 0) return Fail("Usage: agentnotify router connect <agent> [--model <selector>]  (see 'agentnotify router agents' for IDs)");
        var agent = args[0];
        string? model = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--model" or "-m")
            {
                if (i + 1 >= args.Length) return Fail("--model needs a model selector.");
                model = args[++i];
            }
            else return Fail($"unknown option '{args[i]}'.");
        }

        var body = model is null ? "{}" : JsonSerializer.Serialize(new { model });
        return await RouterUiPostAsync($"agents/{Uri.EscapeDataString(agent)}/connect", body);
    }

    private static async Task<int> RunRouterDisconnect(string[] args)
    {
        if (args.Length == 0) return Fail("Usage: agentnotify router disconnect <agent>  (see 'agentnotify router agents' for IDs)");
        return await RouterUiPostAsync($"agents/{Uri.EscapeDataString(args[0])}/disconnect", "{}");
    }

    /// <summary>
    /// Calls the broker's own interface API. Connecting an agent writes files and must happen in the
    /// broker, which owns the router key and the generated catalogue, so the CLI asks rather than
    /// editing anything itself.
    /// </summary>
    private static async Task<JsonElement?> RouterUiGetAsync(string path)
    {
        try
        {
            var store = new ConfigStore();
            var config = store.Load();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("X-AgentNotify-UI", "1");
            var response = await client.GetAsync($"http://127.0.0.1:{config.Port}/ui/api/router/{path}");
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(ReadError(text) ?? $"The broker answered {(int)response.StatusCode}.");
                return null;
            }
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine("AgentNotify is not running, so the router cannot be reached.");
            return null;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return null;
        }
    }

    private static async Task<int> RouterUiPostAsync(string path, string body)
    {
        try
        {
            var store = new ConfigStore();
            var config = store.Load();
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("X-AgentNotify-UI", "1");
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"http://127.0.0.1:{config.Port}/ui/api/router/{path}", content);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(ReadError(text) ?? $"The broker answered {(int)response.StatusCode}.");
                return 1;
            }

            using var document = JsonDocument.Parse(text);
            Console.WriteLine(document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString()
                : "Done.");
            return 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine("AgentNotify is not running, so the router cannot be reached.");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static string? ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // ---- relay pairing ----

}
