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
    /// <summary>Opens the broker's web interface in the default browser.</summary>
    private static async Task<int> RunUi(string[] args)
    {
        string? portOverride = null;
        var printOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length: portOverride = args[++i]; break;
                case "--print" or "--no-open": printOnly = true; break;
                case "--help" or "-h": PrintUiHelp(); return 0;
                default: return Fail($"unknown option '{args[i]}' for ui.");
            }
        }

        var (client, baseUrl) = CreateClient(portOverride, tokenOverride: null, required: false);
        using (client)
        {
            var url = $"{baseUrl}/ui/";
            try
            {
                var response = await client.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return Fail("This broker has no web interface. Update AgentNotify and restart it.");
                if (!response.IsSuccessStatusCode)
                    return Fail($"Error {(int)response.StatusCode} {response.StatusCode} from {url}");
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"Could not reach AgentNotify at {baseUrl}: {ex.Message}");
                Console.Error.WriteLine("Is the broker running?");
                return 1;
            }

            if (printOnly || !TryOpenBrowser(url))
                Console.WriteLine(url);
            else
                Console.WriteLine($"Opened the AgentNotify web interface at {url}");
            return 0;
        }
    }

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            ProcessStartInfo start;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                start = new ProcessStartInfo(url) { UseShellExecute = true };
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                start = new ProcessStartInfo("open", [url]);
            else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                     !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                start = new ProcessStartInfo("xdg-open", [url]);
            else
                return false;

            start.RedirectStandardOutput = !start.UseShellExecute;
            start.RedirectStandardError = !start.UseShellExecute;
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void PrintUiHelp()
    {
        Console.WriteLine("""
            agentnotify ui — open the web interface

            Usage:
              agentnotify ui [--print] [--port N]

            Opens the broker's local web interface in your browser. No sign-in is needed.

            Options:
              --print      Print the address instead of opening a browser
            """);
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
                case "relay": PrintRelayHelp(); return 0;
                case "router": PrintRouterHelp(); return 0;
                case "install-skill": case "install": PrintInstallSkillHelp(); return 0;
                case "install-harness": case "harness": PrintInstallHarnessHelp(); return 0;
                case "interactions": PrintInteractionsHelp(); return 0;
                case "ui": PrintUiHelp(); return 0;
            }
        }
        PrintUsage();
        return 0;
    }

    private static int RunVersion()
    {
        var assembly = typeof(Program).Assembly;
        var v = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.1";
        Console.WriteLine($"agentnotify {v}");
        return 0;
    }

    // ---- helpers ----

}
