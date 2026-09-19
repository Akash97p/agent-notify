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
    private static async Task<int> RunRelay(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintRelayHelp();
            return args.Length == 0 ? 1 : 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "pair" => await RunRelayPair(args[1..]),
            "status" => await RunRelayStatus(args[1..]),
            _ => Fail("Usage: agentnotify relay <pair|status> [options]")
        };
    }

    private static async Task<int> RunRelayPair(string[] args)
    {
        string? url = null;
        string? profileName = null;
        string? senderName = null;
        var json = false;
        var allowPrivate = false;
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--url": url = Next(); break;
                case "--name": profileName = Next(); break;
                case "--sender-name": senderName = Next(); break;
                case "--json": json = true; break;
                case "--allow-private": allowPrivate = true; break;
                case "--help": case "-h": PrintRelayHelp(); return 0;
                default: return Fail($"unknown option '{args[i]}' for relay pair.");
            }
        }

        // Relay is hosted-only: --url stays for pointing a test build at a stub Relay.
        url = string.IsNullOrWhiteSpace(url) ? RelayChannelAdapter.HostedBaseUrl : url;
        if (profileName is { Length: > 100 })
            return Fail("--name must be at most 100 characters.");

        Uri baseUri;
        try
        {
            baseUri = RelayChannelAdapter.ValidateRelayUrl(url.Trim(), allowPrivate);
        }
        catch (ArgumentException exception)
        {
            return Fail(exception.Message);
        }

        senderName = string.IsNullOrWhiteSpace(senderName) ? Environment.MachineName : senderName.Trim();
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            using var client = new RelayPairingClient(allowPrivateNetwork: allowPrivate);
            await client.DiscoverAsync(baseUri, cancellation.Token);
            WriteRelayState(json, "discovered", "Relay discovered.");

            var pairing = await client.BeginAsync(
                baseUri,
                senderName,
                CurrentPlatform(),
                CurrentVersion(),
                cancellation.Token);
            if (json)
            {
                WriteRelayState(
                    true,
                    "pending",
                    "Approve the pairing request.",
                    new { verification_uri = pairing.VerificationUri.AbsoluteUri, user_code = pairing.UserCode });
            }
            else
            {
                Console.WriteLine(RelayPairingPresentation.ManualApprovalText(pairing));
            }

            var poll = await client.WaitForApprovalAsync(
                baseUri,
                pairing,
                progress =>
                {
                    if (!json)
                        Console.Error.WriteLine($"Waiting for approval — {RelayPairingPresentation.FormatRemaining(progress.Remaining)} left");
                    return Task.CompletedTask;
                },
                cancellation.Token);

            if (poll.Status == "denied")
            {
                if (json)
                    WriteRelayState(true, "denied", "Rejected in the browser.");
                return json ? 1 : Fail("Rejected in the browser.");
            }
            if (poll.Status == "expired")
            {
                if (json)
                    WriteRelayState(true, "expired", "Request expired — run relay pair again.");
                return json ? 1 : Fail("Request expired — run relay pair again.");
            }
            if (poll.Status != "approved" || poll.InstallationToken is null || poll.InstallationId is null)
                return Fail("The relay returned an invalid pairing result.");

            WriteRelayState(json, "approved", "Pairing approved.");
            var installation = await client.VerifyAsync(baseUri, poll.InstallationToken, cancellation.Token);
            WriteRelayState(json, "verified", "Relay credential verified.");

            var profiles = await OpenProviderProfilesAsync(cancellation.Token);
            var existing = (await profiles.ListAsync(cancellation.Token))
                .FirstOrDefault(profile => profile.Kind == "relay" &&
                                           RelayProfileMatches(profile.ConfigJson, baseUri));
            var effectiveName = NormalizeProfileName(
                profileName,
                installation.DisplayName,
                poll.RelayName,
                existing?.Name);
            var config = JsonSerializer.Serialize(new
            {
                relay_url = baseUri.AbsoluteUri.TrimEnd('/'),
                sender_name = senderName,
                allowPrivateNetwork = allowPrivate,
                installation_id = poll.InstallationId,
                relay_name = poll.RelayName
            }, Json.Options);
            var saved = await profiles.SaveAsync(
                existing?.Id,
                effectiveName,
                "relay",
                existing?.Enabled ?? false,
                config,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["installation_token"] = poll.InstallationToken
                },
                cancellation.Token);

            WriteRelayState(
                json,
                "saved",
                $"Relay provider '{saved.Name}' saved. Enable it and add a route when ready.",
                new { provider_id = saved.Id, provider_name = saved.Name, enabled = saved.Enabled });
            return 0;
        }
        catch (OperationCanceledException)
        {
            if (json)
                WriteRelayState(true, "cancelled", "Relay pairing cancelled.");
            return json ? 1 : Fail("Relay pairing cancelled.");
        }
        catch (RelayPairingException exception)
        {
            if (json)
                WriteRelayState(true, "error", exception.Message, new { code = exception.Code });
            return json ? 1 : Fail(exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IOException or JsonException or CryptographicException or
                                          UnauthorizedAccessException)
        {
            return Fail(exception.Message);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunRelayStatus(string[] args)
    {
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--json": json = true; break;
                case "--help": case "-h": PrintRelayHelp(); return 0;
                default: return Fail($"unknown option '{args[i]}' for relay status.");
            }
        }

        try
        {
            var profiles = await OpenProviderProfilesAsync(default);
            var relayProfiles = (await profiles.ListAsync()).Where(profile => profile.Kind == "relay").ToArray();
            if (relayProfiles.Length == 0)
            {
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(new { status = "not_configured" }, Json.Options));
                else
                    Console.WriteLine("No Relay provider is configured.");
                return 1;
            }

            var anyConnected = false;
            foreach (var profile in relayProfiles)
            {
                var state = "not_connected";
                string detail;
                try
                {
                    using var document = JsonDocument.Parse(profile.ConfigJson);
                    var root = document.RootElement;
                    var url = GetJsonString(root, "relay_url") ?? GetJsonString(root, "relayUrl") ??
                              RelayChannelAdapter.HostedBaseUrl;
                    var allowPrivate = GetJsonBoolean(root, "allowPrivateNetwork") ||
                                       GetJsonBoolean(root, "allow_private_network");
                    var secrets = await profiles.GetSecretsForDeliveryAsync(profile.Id);
                    if (!secrets.TryGetValue("installation_token", out var credential))
                    {
                        detail = "No saved Relay credential.";
                    }
                    else
                    {
                        var baseUri = RelayChannelAdapter.ValidateRelayUrl(url, allowPrivate);
                        using var pairingClient = new RelayPairingClient(allowPrivateNetwork: allowPrivate);
                        var installation = await pairingClient.VerifyAsync(baseUri, credential, default);
                        state = "connected";
                        detail = installation.DisplayName ?? installation.InstallationId;
                        anyConnected = true;
                    }
                }
                catch (Exception exception) when (exception is RelayPairingException or ArgumentException or
                                                  JsonException or CryptographicException)
                {
                    state = "error";
                    detail = exception.Message;
                }

                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        provider_id = profile.Id,
                        provider_name = profile.Name,
                        enabled = profile.Enabled,
                        status = state,
                        detail
                    }, Json.Options));
                }
                else
                {
                    Console.WriteLine($"{profile.Name}: {state} — {detail}" +
                                      (profile.Enabled ? "" : " (provider disabled)"));
                }
            }
            return anyConnected ? 0 : 1;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException or
                                          CryptographicException or UnauthorizedAccessException)
        {
            return Fail(exception.Message);
        }
    }

    private static async Task<ProviderProfileService> OpenProviderProfilesAsync(CancellationToken ct)
    {
        var store = new ConfigStore(applyEnvOverrides: false);
        if (!Directory.Exists(store.ConfigDir))
            store.Save(store.Load());
        var repository = new SqliteDeliveryRepository(store.DbPath);
        await repository.InitializeAsync(ct);
        return new ProviderProfileService(repository, SecretProtectorFactory.Create(store.ConfigDir));
    }

    private static bool RelayProfileMatches(string configJson, Uri baseUri)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            var configured = GetJsonString(document.RootElement, "relay_url") ??
                             GetJsonString(document.RootElement, "relayUrl") ??
                             RelayChannelAdapter.HostedBaseUrl;
            return Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var candidate) &&
                   candidate == baseUri;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeProfileName(params string?[] candidates)
    {
        var value = candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate))?.Trim() ??
                    "AgentNotify Relay";
        return value.Length <= 100 ? value : value[..100];
    }

    private static string CurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        return "linux";
    }

    private static string CurrentVersion()
    {
        var assembly = typeof(Program).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                      assembly.GetName().Version?.ToString(3) ??
                      "0.0.0";
        return version.Length <= 32 ? version : version[..32];
    }

    private static void WriteRelayState(bool json, string status, string message, object? details = null)
    {
        if (!json)
        {
            Console.WriteLine(message);
            return;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { status, message, details }, Json.Options));
    }

    private static string? GetJsonString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetJsonBoolean(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;

}
