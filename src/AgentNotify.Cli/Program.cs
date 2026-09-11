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
using AgentNotify.Core.Delivery;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Core.Harness;

namespace AgentNotify.Cli;

/// <summary>Hand-rolled CLI — no extra package so the publish stays lean.
/// Every command talks to the local broker via HTTP; the broker is source of truth.</summary>
internal static class Program
{
    private static readonly string[] KnownCommands = ["send", "list", "get", "resolve", "dismiss", "health", "relay", "token", "install-skill", "install-harness", "install", "interactions", "help", "--help", "-h", "--version"];

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

    // ---- relay pairing ----

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

        if (string.IsNullOrWhiteSpace(url))
            return Fail("relay pair requires --url.");
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
                deployment = "custom",
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
                    var url = GetJsonString(root, "relay_url") ?? GetJsonString(root, "relayUrl");
                    var allowPrivate = GetJsonBoolean(root, "allowPrivateNetwork") ||
                                       GetJsonBoolean(root, "allow_private_network");
                    var secrets = await profiles.GetSecretsForDeliveryAsync(profile.Id);
                    if (url is null || !secrets.TryGetValue("installation_token", out var credential))
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
                             GetJsonString(document.RootElement, "relayUrl");
            return configured is not null &&
                   Uri.TryCreate(configured.TrimEnd('/') + "/", UriKind.Absolute, out var candidate) &&
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
                case "--force": force = true; break;
                case "--dry-run": dryRun = true; break;
                case "--help": case "-h": PrintInstallSkillHelp(); return 0;
                default: return Fail($"unknown option '{args[i]}' for install-skill.");
            }
        }

        try
        {
            var skillsRoot = path ?? AgentSkillCatalog.DefaultSkillsRoot(
                agent,
                projectScope ? Directory.GetCurrentDirectory() : null);
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

    private static async Task<int> RunInteractions(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintInteractionsHelp();
            return args.Length == 0 ? 1 : 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "request" => await RunInteractionsRequest(args[1..]),
            "list" => await RunInteractionsList(args[1..]),
            "get" => await RunInteractionsGet(args[1..]),
            "wait" => await RunInteractionsWait(args[1..]),
            "respond" => await RunInteractionsRespond(args[1..]),
            "cancel" => await RunInteractionsCancel(args[1..]),
            _ => Fail("Usage: agentnotify interactions <request|list|get|wait|respond|cancel> [options]")
        };
    }

    private static async Task<int> RunInteractionsRequest(string[] args)
    {
        string? kind = null, prompt = null, agent = null, agentInstance = null, project = null;
        string? session = null, turn = null, nativeRequest = null, key = null;
        string? portOverride = null, tokenOverride = null;
        int? textMax = null, ttl = null;
        var choices = new List<InteractionChoice>();
        var details = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--kind": kind = Next(); break;
                case "--prompt": prompt = Next(); break;
                case "--choice":
                    var spec = Next();
                    if (spec is null) return Fail("--choice requires ID:LABEL.");
                    var colon = spec.IndexOf(':');
                    if (colon <= 0) return Fail("--choice requires ID:LABEL.");
                    choices.Add(new InteractionChoice { Id = spec[..colon], Label = spec[(colon + 1)..] });
                    break;
                case "--choice-detail":
                    var detail = Next();
                    if (detail is null) return Fail("--choice-detail requires ID:DETAIL.");
                    var dcolon = detail.IndexOf(':');
                    if (dcolon <= 0) return Fail("--choice-detail requires ID:DETAIL.");
                    details[detail[..dcolon]] = detail[(dcolon + 1)..];
                    break;
                case "--text-max": textMax = int.TryParse(Next(), out var tm) ? tm : null; break;
                case "--ttl": ttl = int.TryParse(Next(), out var tt) ? tt : null; break;
                case "--key": key = Next(); break;
                case "--agent": agent = Next(); break;
                case "--agent-instance": agentInstance = Next(); break;
                case "--project": project = Next(); break;
                case "--session": session = Next(); break;
                case "--turn": turn = Next(); break;
                case "--native-request": nativeRequest = Next(); break;
                case "--port": portOverride = Next(); break;
                case "--token": tokenOverride = Next(); break;
                case "--help": case "-h": PrintInteractionsHelp(); return 0;
                default:
                    if (a.StartsWith('-')) return Fail($"unknown option '{a}' for interactions request. Run 'agentnotify help interactions'.");
                    if (prompt is null) prompt = a;
                    else return Fail($"unexpected argument '{a}' for interactions request.");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(prompt)) return Fail("interactions request requires --prompt (or a positional prompt).");
        if (!TryParseEnum<InteractionKind>(kind ?? "permission", out var parsedKind))
            return Fail("--kind must be permission, single_choice, or text.");
        foreach (var (id, text) in details)
        {
            var match = choices.FirstOrDefault(c => c.Id == id);
            if (match is null) return Fail($"--choice-detail id '{id}' matches no --choice.");
            match.Detail = text;
        }

        agent ??= Environment.GetEnvironmentVariable("AGENTNOTIFY_AGENT") ?? "cli";
        var req = new CreateInteractionRequest
        {
            Key = key, Agent = agent, AgentInstance = agentInstance, Project = project,
            SessionId = session, TurnId = turn, NativeRequestId = nativeRequest,
            Kind = parsedKind, Prompt = prompt.Trim(), Choices = choices,
            TextMaxLength = textMax, TtlSeconds = ttl
        };

        var (client, baseUrl) = CreateClient(portOverride, tokenOverride);
        using (client)
        {
            var json = JsonSerializer.Serialize(req, Json.Options);
            var resp = await client.PostAsync($"{baseUrl}/v1/interactions/request",
                new StringContent(json, Encoding.UTF8, "application/json"));
            return await HandleJsonResponse(resp);
        }
    }

    private static async Task<int> RunInteractionsList(string[] args)
    {
        string? status = null, agent = null, project = null, session = null;
        string? portOverride = null, tokenOverride = null;
        string? pending = null;
        int limit = 20;
        bool jsonOut = false;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--status": status = Next(); break;
                case "--pending":
                    if (i + 1 < args.Length && bool.TryParse(args[i + 1], out var pendingValue))
                    {
                        i++;
                        pending = pendingValue ? "true" : "false";
                    }
                    else pending = "true";
                    break;
                case "--agent": agent = Next(); break;
                case "--project": project = Next(); break;
                case "--session": session = Next(); break;
                case "--limit": int.TryParse(Next(), out limit); break;
                case "--json": jsonOut = true; break;
                case "--port": portOverride = Next(); break;
                case "--token": tokenOverride = Next(); break;
                case "--help": case "-h": PrintInteractionsHelp(); return 0;
                default:
                    if (a.StartsWith('-')) return Fail($"unknown option '{a}' for interactions list.");
                    break;
            }
        }

        var qs = new List<string>();
        if (pending is not null) qs.Add($"pending={Uri.EscapeDataString(pending)}");
        if (status is not null) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (agent is not null) qs.Add($"agent={Uri.EscapeDataString(agent)}");
        if (project is not null) qs.Add($"project={Uri.EscapeDataString(project)}");
        if (session is not null) qs.Add($"session={Uri.EscapeDataString(session)}");
        qs.Add($"limit={limit}");
        var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";

        var (client, baseUrl) = CreateClient(portOverride, tokenOverride);
        using (client)
        {
            var resp = await client.GetAsync($"{baseUrl}/v1/interactions{query}");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
                return 1;
            }
            if (jsonOut)
            {
                PrintPrettyJson(body);
                return 0;
            }
            try
            {
                var items = JsonSerializer.Deserialize<List<InteractionDto>>(body, Json.Options) ?? [];
                if (items.Count == 0)
                    Console.WriteLine("(no interactions)");
                else
                    foreach (var n in items)
                        Console.WriteLine($"{n.Id}  [{n.Kind}/{n.Status}] {TruncateOneLine(n.Prompt, 80)}  ({n.Agent})");
            }
            catch { Console.WriteLine(body); }
            return 0;
        }
    }

    private static async Task<int> RunInteractionsGet(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-')) return Fail("interactions get requires an <id>. Usage: agentnotify interactions get <id>");
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
            var resp = await client.GetAsync($"{baseUrl}/v1/interactions/{Uri.EscapeDataString(id)}");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
                return resp.StatusCode == HttpStatusCode.NotFound ? 3 : 1;
            }
            PrintPrettyJson(body);
            return 0;
        }
    }

    private static async Task<int> RunInteractionsWait(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-')) return Fail("interactions wait requires an <id>. Usage: agentnotify interactions wait <id> [--timeout N]");
        var id = args[0];
        var timeoutSeconds = 60;
        string? portOverride = null, tokenOverride = null;
        for (var i = 1; i < args.Length; i++)
        {
            if ((args[i] == "--timeout" || args[i] == "--ttl") && i + 1 < args.Length && int.TryParse(args[++i], out var t))
                timeoutSeconds = Math.Clamp(t, 1, 300);
            else if (args[i] == "--port" && i + 1 < args.Length) portOverride = args[++i];
            else if (args[i] == "--token" && i + 1 < args.Length) tokenOverride = args[++i];
        }

        // Waiting is the point: allow the full broker wait plus margin.
        var store = new ConfigStore(applyEnvOverrides: true);
        var config = store.Load();
        if (!string.IsNullOrWhiteSpace(portOverride) && int.TryParse(portOverride, out var p)) config.Port = p;
        if (!string.IsNullOrWhiteSpace(tokenOverride)) config.AuthToken = tokenOverride.Trim();
        var baseUrl = $"http://127.0.0.1:{config.Port}";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds + 30) };
        if (!string.IsNullOrWhiteSpace(config.AuthToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.AuthToken);

        try
        {
            var resp = await client.GetAsync($"{baseUrl}/v1/interactions/{Uri.EscapeDataString(id)}/wait?timeout={timeoutSeconds}");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
                return resp.StatusCode == HttpStatusCode.NotFound ? 3 : 1;
            }
            PrintPrettyJson(body);
            return 0;
        }
        catch (TaskCanceledException)
        {
            return Fail("Timed out waiting for the broker. The interaction may still be pending; check with 'interactions get'.");
        }
    }

    private static async Task<int> RunInteractionsRespond(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-')) return Fail("interactions respond requires an <id>. Usage: agentnotify interactions respond <id> --response-id R --digest D [--choice C | --text T]");
        var id = args[0];
        string? responseId = null, digest = null, choice = null, text = null;
        string? nonce = null, source = null, device = null;
        string? portOverride = null, tokenOverride = null;

        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a.ToLowerInvariant())
            {
                case "--response-id": responseId = Next(); break;
                case "--digest": digest = Next(); break;
                case "--choice": choice = Next(); break;
                case "--text": text = Next(); break;
                case "--nonce": nonce = Next(); break;
                case "--source": source = Next(); break;
                case "--device": device = Next(); break;
                case "--port": portOverride = Next(); break;
                case "--token": tokenOverride = Next(); break;
                case "--help": case "-h": PrintInteractionsHelp(); return 0;
                default: return Fail($"unknown option '{a}' for interactions respond.");
            }
        }

        if (string.IsNullOrWhiteSpace(responseId)) return Fail("interactions respond requires --response-id.");
        if (string.IsNullOrWhiteSpace(digest)) return Fail("interactions respond requires --digest (from 'interactions get').");
        source ??= "cli";

        var req = new RespondInteractionRequest
        {
            ResponseId = responseId.Trim(), RequestDigest = digest.Trim(),
            ChoiceId = choice, Text = text, Nonce = nonce, Source = source, DeviceId = device
        };
        var (client, baseUrl) = CreateClient(portOverride, tokenOverride);
        using (client)
        {
            var json = JsonSerializer.Serialize(req, Json.Options);
            var resp = await client.PostAsync($"{baseUrl}/v1/interactions/{Uri.EscapeDataString(id)}/respond",
                new StringContent(json, Encoding.UTF8, "application/json"));
            return await HandleJsonResponse(resp);
        }
    }

    private static async Task<int> RunInteractionsCancel(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-')) return Fail("interactions cancel requires an <id>. Usage: agentnotify interactions cancel <id>");
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
            var resp = await client.PostAsync($"{baseUrl}/v1/interactions/{Uri.EscapeDataString(id)}/cancel",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            return await HandleJsonResponse(resp);
        }
    }

    private static async Task<int> HandleJsonResponse(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Error {((int)resp.StatusCode)} {resp.StatusCode}: {PrettyError(body)}");
            return resp.StatusCode == HttpStatusCode.NotFound ? 3 : 1;
        }
        PrintPrettyJson(body);
        return 0;
    }

    private static void PrintPrettyJson(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            Console.WriteLine(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { Console.WriteLine(body); }
    }

    private static string TruncateOneLine(string value, int max)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..(max - 1)] + "…";
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
                case "install-skill": case "install": PrintInstallSkillHelp(); return 0;
                case "install-harness": case "harness": PrintInstallHarnessHelp(); return 0;
                case "interactions": PrintInteractionsHelp(); return 0;
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
              install-skill  Install the bundled skill for Codex, Claude Code, or OpenCode
              install-harness  Install the auto-notify harness for OpenCode, Codex, or Claude Code
              interactions  Ask a waiting question/permission and collect the answer
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
              agentnotify interactions respond <id> --response-id R --digest D [--choice C | --text T] [--nonce N] [--source S] [--device D]
              agentnotify interactions cancel <id>

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
            outcome. Answers must echo the request digest from 'interactions get'.
            'wait' blocks until the interaction settles or --timeout (1-300s, default 60).
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
              --force               Replace changed AgentNotify skill files
              --dry-run             Print the destination without writing files

            Default user locations:
              Codex        ~/.agents/skills/agentnotify
              Claude Code  ~/.claude/skills/agentnotify
              OpenCode     ~/.config/opencode/skill/agentnotify

            Any other agent: pass --path with the folder it loads skills from.
            """);
    }

    private static void PrintRelayHelp()
    {
        Console.WriteLine("""
            agentnotify relay — connect this computer to an AgentNotify Relay

            Usage:
              agentnotify relay pair --url URL [--name NAME] [--sender-name NAME] [--allow-private] [--json]
              agentnotify relay status [--json]

            Pair options:
              --url URL          Relay base URL (HTTPS, or HTTP localhost with --allow-private)
              --name NAME        Provider profile name
              --sender-name NAME Sender label shown by the Relay (default machine name)
              --allow-private    Explicitly allow private/loopback Relay destinations
              --json             Emit one JSON object per state transition

            Pairing prints a verification URL and short code, then stores the one-time
            installation credential in AgentNotify's protected provider secret store.
            """);
    }
}
