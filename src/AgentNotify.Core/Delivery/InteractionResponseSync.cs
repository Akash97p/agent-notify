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
    string InstallationId)
{
    /// <summary>Builds a target from the same encrypted Relay profile used for delivery.</summary>
    public static async Task<RelayPollTarget?> FromProfileAsync(
        ProviderProfileService profiles,
        ProviderProfile profile,
        CancellationToken ct = default)
    {
        // Older manually-created profiles can predate installation identity. They
        // cannot safely consume installation-bound answers and are intentionally skipped.
        using (var document = JsonDocument.Parse(profile.ConfigJson))
        {
            var root = document.RootElement;
            var installationId = GetString(root, "installation_id") ?? GetString(root, "installationId");
            if (string.IsNullOrWhiteSpace(installationId))
                return null;
        }

        var provider = await profiles.GetForDeliveryAsync(profile.Id, ct);
        if (provider is null || !provider.Profile.Enabled || provider.Profile.Kind != "relay")
            return null;

        var config = RelayChannelAdapter.ParseAndValidateConfiguration(
            provider.Profile.ConfigJson,
            provider.Secrets);
        if (string.IsNullOrWhiteSpace(config.InstallationId))
            return null;
        if (!provider.Secrets.TryGetValue(config.InstallationTokenSecretName, out var token) ||
            !RelayChannelAdapter.IsInstallationToken(token))
            throw new InvalidOperationException("Relay profile has no valid installation credential.");

        return new RelayPollTarget(
            profile.Id,
            profile.Name,
            config.RelayUrl!,
            config.AllowPrivateNetwork,
            token,
            config.InstallationId);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

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
/// The broker revalidates every answer (digest, nonce, kind, expiry,
/// first-wins), so a merged Relay cannot replay or stale-serve an answer into
/// a host. It can, however, alter the choice or text of an answer it receives,
/// because v1 answers are plaintext to the trusted hosted Relay; sealing
/// answers to an installation key is future work. Transport errors and any
/// local broker rejection that may be temporary never move the cursor; only a
/// fully processed poll advances it.
/// </remarks>
public sealed class InteractionResponseSync
{
    private const int MaxBytes = 64 * 1024;
    private const int MaxCursorLength = 2048;

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
        if (string.IsNullOrWhiteSpace(target.InstallationId))
            return new ProviderPollOutcome(target.ProviderId, false, "relay installation identity is missing", sinceCursor, []);

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
            $"v1/interaction-responses?installation_id={Uri.EscapeDataString(target.InstallationId)}&since={Uri.EscapeDataString(sinceCursor)}");
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
            {
                return new ProviderPollOutcome(target.ProviderId, false, "relay returned invalid JSON", sinceCursor, []);
            }

            if (poll?.Responses is null || poll.NextCursor is null || poll.NextCursor.Length > MaxCursorLength)
                return new ProviderPollOutcome(target.ProviderId, false, "relay returned invalid JSON", sinceCursor, []);

            var answers = new List<IngestedAnswer>();
            foreach (var item in poll.Responses)
            {
                var ingested = await IngestAsync(target, item, ct);
                answers.Add(ingested.Answer);
                if (ingested.Retry)
                    return new ProviderPollOutcome(
                        target.ProviderId,
                        false,
                        "local broker did not accept the response batch",
                        sinceCursor,
                        answers);
            }
            return new ProviderPollOutcome(target.ProviderId, true, null, poll.NextCursor, answers);
        }
    }

    private async Task<IngestResult> IngestAsync(
        RelayPollTarget target,
        RelayInteractionResponse item,
        CancellationToken ct)
    {
        var responseId = (item.ResponseId ?? "").Trim();
        var interactionId = (item.InteractionId ?? "").Trim();
        var malformed = Validate(item, responseId, interactionId, target.InstallationId);
        if (malformed is not null)
            return new IngestResult(
                new IngestedAnswer(responseId, interactionId, false, malformed),
                Retry: false);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return Retry("broker unreachable");
        }

        using (resp)
        {
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
                return Terminal(applied: true, note: null);
            if (code == 400)
                return Terminal(applied: false, note: "broker rejected stale or malformed answer");
            if (code == 404)
                return Terminal(applied: false, note: "interaction is no longer available");
            if (code == 409)
                return Terminal(applied: false, note: "already answered by another response");
            return Retry($"broker temporarily rejected response ({code})");
        }

        IngestResult Terminal(bool applied, string? note) =>
            new(new IngestedAnswer(responseId, interactionId, applied, note), Retry: false);
        IngestResult Retry(string note) =>
            new(new IngestedAnswer(responseId, interactionId, false, note), Retry: true);
    }

    private static string? Validate(
        RelayInteractionResponse item,
        string responseId,
        string interactionId,
        string targetInstallationId)
    {
        if (!string.Equals(item.ContractVersion, InteractionRelayContract.Version, StringComparison.Ordinal))
            return "unsupported or missing contract version";
        if (!IsRequiredId(responseId, 128) || !IsRequiredId(interactionId, 128))
            return "missing or invalid response or interaction id";

        var digest = (item.RequestDigest ?? "").Trim();
        if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigit))
            return "request digest must be 64 hexadecimal characters";

        var nonce = (item.Nonce ?? "").Trim();
        if (nonce.Length is < 1 or > 128 || nonce.Any(char.IsControl))
            return "nonce is missing or invalid";

        var installationId = (item.InstallationId ?? "").Trim();
        if (!string.Equals(installationId, targetInstallationId, StringComparison.Ordinal))
            return "wrong or missing installation";

        var hasChoice = !string.IsNullOrWhiteSpace(item.ChoiceId);
        var hasText = !string.IsNullOrWhiteSpace(item.Text);
        if (hasChoice == hasText || item.ChoiceId?.Trim().Length > 64 || item.Text?.Trim().Length > 2000)
            return "answer must contain exactly one bounded choice or text value";
        if (item.DeviceId?.Length > 128)
            return "device id is invalid";
        return null;
    }

    private static bool IsRequiredId(string value, int maximumLength) =>
        value.Length is > 0 && value.Length <= maximumLength && !value.Any(char.IsControl);

    private sealed record IngestResult(IngestedAnswer Answer, bool Retry);
}
