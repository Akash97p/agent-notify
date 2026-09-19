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

}
