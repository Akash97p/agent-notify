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
            "publish" => await RunInteractionsPublish(args[1..]),
            "poll-responses" => await RunInteractionsPollResponses(args[1..]),
            _ => Fail("Usage: agentnotify interactions <request|list|get|wait|respond|cancel|publish|poll-responses> [options]")
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
        if (args.Length == 0 || args[0].StartsWith('-')) return Fail("interactions respond requires an <id>. Usage: agentnotify interactions respond <id> --response-id R --digest D --nonce N [--choice C | --text T]");
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

    private static async Task<int> RunInteractionsPublish(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith('-')) return Fail("interactions publish requires an <id>. Usage: agentnotify interactions publish <id>");
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
            var resp = await client.PostAsync($"{baseUrl}/v1/interactions/{Uri.EscapeDataString(id)}/publish",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            return await HandleJsonResponse(resp);
        }
    }

    private static async Task<int> RunInteractionsPollResponses(string[] args)
    {
        string? providerFilter = null, portOverride = null, tokenOverride = null;
        var jsonOut = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--provider":
                    if (i + 1 >= args.Length) return Fail("--provider requires a provider id.");
                    providerFilter = args[++i];
                    break;
                case "--json": jsonOut = true; break;
                case "--port": if (i + 1 >= args.Length) return Fail("--port requires a number."); portOverride = args[++i]; break;
                case "--token": if (i + 1 >= args.Length) return Fail("--token requires a token."); tokenOverride = args[++i]; break;
                case "--help": case "-h": PrintInteractionsHelp(); return 0;
                default: return Fail($"unknown option '{args[i]}' for interactions poll-responses.");
            }
        }

        var configStore = new ConfigStore(applyEnvOverrides: true);
        var brokerConfig = configStore.Load();
        if (!string.IsNullOrWhiteSpace(portOverride) && int.TryParse(portOverride, out var p)) brokerConfig.Port = p;
        if (!string.IsNullOrWhiteSpace(tokenOverride)) brokerConfig.AuthToken = tokenOverride.Trim();
        if (string.IsNullOrWhiteSpace(brokerConfig.AuthToken))
            return Fail("No broker auth token found. Has AgentNotify run at least once?");
        var brokerBaseUrl = $"http://127.0.0.1:{brokerConfig.Port}";

        try
        {
            var profiles = await OpenProviderProfilesAsync(default);
            var relayProfiles = (await profiles.ListAsync())
                .Where(profile => profile.Kind == "relay" && profile.Enabled)
                .Where(profile => providerFilter is null || string.Equals(profile.Id, providerFilter, StringComparison.Ordinal))
                .ToArray();
            if (providerFilter is not null && relayProfiles.Length == 0)
                return Fail($"No enabled Relay provider with id '{providerFilter}'.");
            if (relayProfiles.Length == 0)
            {
                Console.Error.WriteLine("No enabled Relay provider is configured; nothing to poll.");
                return 1;
            }

            using var relayClient = RelayHttpTransport.CreateClient();
            using var brokerClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            brokerClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", brokerConfig.AuthToken);
            var sync = new InteractionResponseSync(relayClient, brokerClient, brokerBaseUrl);
            var cursors = new RelayCursorStore(configStore.ConfigDir);

            var exit = 0;
            foreach (var profile in relayProfiles)
            {
                RelayPollTarget target;
                try
                {
                    var configured = await RelayPollTarget.FromProfileAsync(profiles, profile);
                    if (configured is null)
                    {
                        Console.Error.WriteLine($"{profile.Name}: no installation_id; skipped.");
                        continue;
                    }
                    target = configured;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
                                           JsonException or CryptographicException or IOException or
                                           UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"{profile.Name}: {ex.Message}");
                    exit = 1;
                    continue;
                }

                var since = await cursors.GetAsync(profile.Id);
                ProviderPollOutcome outcome;
                try
                {
                    outcome = await sync.PollOnceAsync(target, since);
                }
                catch (Exception ex) when (ex is OperationCanceledException)
                {
                    Console.Error.WriteLine($"{profile.Name}: poll timed out.");
                    exit = 1;
                    continue;
                }

                if (!outcome.Succeeded)
                {
                    Console.Error.WriteLine($"{profile.Name}: {outcome.Error}");
                    exit = 1;
                    continue;
                }

                await cursors.SetAsync(profile.Id, outcome.NextCursor);
                if (jsonOut)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        provider_id = profile.Id,
                        provider_name = profile.Name,
                        applied = outcome.Answers.Count(a => a.Applied),
                        answers = outcome.Answers.Select(a => new
                        {
                            response_id = a.ResponseId,
                            interaction_id = a.InteractionId,
                            applied = a.Applied,
                            note = a.Note
                        })
                    }, Json.Options));
                }
                else
                {
                    var applied = outcome.Answers.Count(a => a.Applied);
                    Console.WriteLine($"{profile.Name}: {applied} answer(s) applied out of {outcome.Answers.Count} fetched.");
                    foreach (var answer in outcome.Answers.Where(a => !a.Applied))
                        Console.WriteLine($"  {answer.ResponseId} -> {answer.InteractionId}: {answer.Note}");
                }
            }
            return exit;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or JsonException or
                                          CryptographicException or UnauthorizedAccessException)
        {
            return Fail(exception.Message);
        }
    }

    private static async Task<int> HandleJsonResponse(HttpResponseMessage resp)    {
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

    // ---- web interface ----

}
