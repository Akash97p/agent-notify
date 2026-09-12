using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentNotify.Core.Delivery.Channels;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery;

/// <summary>One Relay provider polled for interaction answers.</summary>
public sealed record RelayPollTarget(
    string ProviderId,
    string ProviderName,
    string RelayBaseUrl,
    bool AllowPrivateNetwork,
    string InstallationToken,
    string? InstallationId);

/// <summary>Outcome of ingesting one stored mobile answer.</summary>
public sealed record IngestedAnswer(
    string ResponseId,
    string InteractionId,
    bool Applied,
    string? Note);

/// <summary>Outcome of one provider poll.</summary>
public sealed record ProviderPollOutcome(
    string ProviderId,
    bool Succeeded,
    string? Error,
    string NextCursor,
    IReadOnlyList<IngestedAnswer> Answers);

/// <summary>
/// Fetches stored mobile answers from Relay and posts them to the broker.
/// </summary>
/// <remarks>
/// Relay is an untrusted store: every answer is revalidated by the broker
/// (digest, nonce, kind, expiry, first-wins), so a malicious or buggy Relay
/// can at worst delay answers, never forge them. Transport errors never move
/// the cursor; only a fully processed poll advances it.
/// </remarks>
public sealed class InteractionResponseSync
{
    private const int MaxBytes = 256 * 1024;

    private readonly HttpClient _relayClient;
    private readonly HttpClient _brokerClient;
    private readonly string _brokerBaseUrl;

    public InteractionResponseSync(HttpClient relayClient, HttpClient brokerClient, string brokerBaseUrl)
    {
        _relayClient = relayClient;
        _brokerClient = brokerClient;
        _brokerBaseUrl = brokerBaseUrl.TrimEnd('/');
    }

    /// <summary>Creates a sync pair with standard transports (10 s broker timeout).</summary>
    public static InteractionResponseSync Create(string brokerBaseUrl, string brokerToken)
    {
        var broker = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrWhiteSpace(brokerToken))
            broker.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", brokerToken);
        return new InteractionResponseSync(RelayHttpTransport.CreateClient(), broker, brokerBaseUrl);
    }

    public async Task<ProviderPollOutcome> PollOnceAsync(
        RelayPollTarget target,
        string sinceCursor,
        CancellationToken ct = default)
    {
        Uri relayBase;
        try
        {
            relayBase = RelayChannelAdapter.ValidateRelayUrl(target.RelayBaseUrl, target.AllowPrivateNetwork);
        }
        catch (ArgumentException ex)
        {
            return new ProviderPollOutcome(target.ProviderId, false, ex.Message, sinceCursor, []);
        }

        var endpoint = new Uri(relayBase,
            $"v1/interaction-responses?installation_id={Uri.EscapeDataString(target.InstallationId ?? "")}&since={Uri.EscapeDataString(sinceCursor)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        RelayHttpTransport.MarkValidated(request, target.AllowPrivateNetwork);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.InstallationToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));

        HttpResponseMessage response;
        try
        {
            response = await _relayClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return new ProviderPollOutcome(target.ProviderId, false, "relay unreachable", sinceCursor, []);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is 401 or 403)
                return new ProviderPollOutcome(target.ProviderId, false, "relay rejected the installation token", sinceCursor, []);
            if (status is < 200 or > 299)
                return new ProviderPollOutcome(target.ProviderId, false, $"relay returned {status}", sinceCursor, []);

            RelayResponsePollResult? poll;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, ct);
                    if (read == 0) break;
                    if (ms.Length + read > MaxBytes)
                        return new ProviderPollOutcome(target.ProviderId, false, "relay response too large", sinceCursor, []);
                    ms.Write(buffer, 0, read);
                }
                ms.Position = 0;
                poll = await JsonSerializer.DeserializeAsync<RelayResponsePollResult>(ms, Protocol.Json.Options, ct);
            }
            catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
            {
                return new ProviderPollOutcome(target.ProviderId, false, "relay returned invalid JSON", sinceCursor, []);
            }

            if (poll is null)
                return new ProviderPollOutcome(target.ProviderId, false, "relay returned invalid JSON", sinceCursor, []);

            var answers = new List<IngestedAnswer>();
            foreach (var item in poll.Responses)
                answers.Add(await IngestAsync(target, item, ct));
            return new ProviderPollOutcome(target.ProviderId, true, null, poll.NextCursor ?? sinceCursor, answers);
        }
    }

    private async Task<IngestedAnswer> IngestAsync(
        RelayPollTarget target,
        RelayInteractionResponse item,
        CancellationToken ct)
    {
        var responseId = (item.ResponseId ?? "").Trim();
        var interactionId = (item.InteractionId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(responseId) || string.IsNullOrWhiteSpace(interactionId))
            return new IngestedAnswer(responseId, interactionId, false, "missing response or interaction id");

        // Wrong-installation answers are dropped before touching the broker.
        if (!string.IsNullOrWhiteSpace(target.InstallationId) &&
            !string.IsNullOrWhiteSpace(item.InstallationId) &&
            !string.Equals(item.InstallationId!.Trim(), target.InstallationId, StringComparison.Ordinal))
            return new IngestedAnswer(responseId, interactionId, false, "wrong installation");

        var body = new RespondInteractionRequest
        {
            ResponseId = responseId,
            RequestDigest = (item.RequestDigest ?? "").Trim(),
            Nonce = string.IsNullOrWhiteSpace(item.Nonce) ? null : item.Nonce.Trim(),
            ChoiceId = string.IsNullOrWhiteSpace(item.ChoiceId) ? null : item.ChoiceId.Trim(),
            Text = string.IsNullOrWhiteSpace(item.Text) ? null : item.Text,
            Source = "relay",
            DeviceId = string.IsNullOrWhiteSpace(item.DeviceId) ? null : item.DeviceId.Trim()
        };

        HttpResponseMessage resp;
        try
        {
            var json = JsonSerializer.Serialize(body, Protocol.Json.Options);
            resp = await _brokerClient.PostAsync(
                $"{_brokerBaseUrl}/v1/interactions/{Uri.EscapeDataString(interactionId)}/respond",
                new StringContent(json, Encoding.UTF8, "application/json"), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return new IngestedAnswer(responseId, interactionId, false, "broker unreachable");
        }

        using (resp)
        {
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
                return new IngestedAnswer(responseId, interactionId, true, null);
            if (code == 409)
                return new IngestedAnswer(responseId, interactionId, false, "already answered by another response");
            var detail = await SafeErrorAsync(resp, ct);
            return new IngestedAnswer(responseId, interactionId, false, $"broker rejected ({code}): {detail}");
        }
    }

    private static async Task<string> SafeErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var message = (error.GetString() ?? "").Trim();
                if (message.Length == 0)
                    return "unknown";
                return message.Length > 120 ? message[..120] : message;
            }
            return "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
