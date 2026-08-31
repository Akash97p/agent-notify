using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentNotify.Core.Delivery.Channels;

public sealed record RelayDiscovery(string Service, IReadOnlyList<string> ApiVersions);

public sealed record RelayPairingRequest(
    string PairingId,
    string UserCode,
    Uri VerificationUri,
    Uri VerificationUriComplete,
    string PollToken,
    int ExpiresIn,
    int Interval);

public sealed record RelayPairingPoll(
    string Status,
    string? InstallationId = null,
    string? InstallationToken = null,
    string? RelayName = null,
    int? RetryAfterMilliseconds = null);

public sealed record RelayInstallation(
    string InstallationId,
    string? DisplayName,
    string? CreatedAt,
    string? LastSeenAt);

public sealed record RelayDeviceSummary(int ActiveDeviceCount);

public sealed record RelayPairingProgress(
    TimeSpan Remaining,
    TimeSpan PollInterval,
    int ConsecutiveNetworkFailures);

public static class RelayPairingPresentation
{
    public static string ManualApprovalText(RelayPairingRequest pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        return $"Open this on any device and enter the code:{Environment.NewLine}" +
               $"{pairing.VerificationUri.AbsoluteUri}{Environment.NewLine}{pairing.UserCode}";
    }

    public static string FormatRemaining(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{Math.Max(0, (int)remaining.TotalMinutes)}:{Math.Max(0, remaining.Seconds):00}";
}

/// <summary>A sanitized Relay pairing failure. Credential values are never included.</summary>
public sealed class RelayPairingException : Exception
{
    public RelayPairingException(string code, string message, int? retryAfterMilliseconds = null)
        : base(message)
    {
        Code = code;
        RetryAfterMilliseconds = retryAfterMilliseconds;
    }

    public string Code { get; }
    public int? RetryAfterMilliseconds { get; }
}

/// <summary>
/// Implements the Relay sender device-authorization handshake without UI or persistence concerns.
/// </summary>
public sealed class RelayPairingClient : IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly bool _allowPrivateNetwork;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTimeOffset> _utcNow;

    public RelayPairingClient(HttpClient? client = null, bool allowPrivateNetwork = false)
        : this(
            client,
            allowPrivateNetwork,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            static () => DateTimeOffset.UtcNow)
    {
    }

    internal RelayPairingClient(
        HttpClient? client,
        bool allowPrivateNetwork,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<DateTimeOffset> utcNow)
    {
        _ownsClient = client is null;
        _client = client ?? RelayHttpTransport.CreateClient();
        _allowPrivateNetwork = allowPrivateNetwork;
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    public async Task<RelayDiscovery> DiscoverAsync(Uri baseUri, CancellationToken ct)
    {
        var normalizedBase = ValidateBaseUri(baseUri);
        using var request = CreateRequest(HttpMethod.Get, new Uri(normalizedBase, ".well-known/agentnotify-relay"));
        using var response = await SendAsync(request, "discovery_failed", ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new RelayPairingException("not_relay", "That URL is not an AgentNotify Relay.");

        using var document = await ReadJsonAsync(response.Content, "not_relay", ct).ConfigureAwait(false);
        var root = document.RootElement;
        var service = GetString(root, "service");
        var versions = root.ValueKind == JsonValueKind.Object &&
                       root.TryGetProperty("api_versions", out var apiVersions) &&
                       apiVersions.ValueKind == JsonValueKind.Array
            ? apiVersions.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray()
            : [];

        if (!string.Equals(service, "agentnotify-relay", StringComparison.Ordinal) ||
            !versions.Contains("v1", StringComparer.Ordinal))
            throw new RelayPairingException("not_relay", "That URL is not an AgentNotify Relay.");

        return new RelayDiscovery(service!, versions);
    }

    public async Task<RelayPairingRequest> BeginAsync(
        Uri baseUri,
        string? senderName,
        string platform,
        string clientVersion,
        CancellationToken ct)
    {
        var normalizedBase = ValidateBaseUri(baseUri);
        senderName = NormalizeOptional(senderName);
        if (senderName is { Length: > 100 } || senderName?.Any(char.IsControl) == true)
            throw new ArgumentException("Relay sender name must be at most 100 characters without control characters.", nameof(senderName));
        if (platform is not ("windows" or "macos" or "linux"))
            throw new ArgumentException("Relay platform is invalid.", nameof(platform));
        if (string.IsNullOrWhiteSpace(clientVersion) || clientVersion.Length > 32 || clientVersion.Any(char.IsControl))
            throw new ArgumentException("Relay client version is invalid.", nameof(clientVersion));

        var payload = JsonSerializer.Serialize(new BeginRequest(senderName, platform, clientVersion));
        using var request = CreateRequest(HttpMethod.Post, new Uri(normalizedBase, "v1/pairing/sender"));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, "pairing_failed", ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = await ReadRetryAfterAsync(response.Content, ct).ConfigureAwait(false);
            throw new RelayPairingException(
                "rate_limited",
                "The relay is rate-limiting pairing requests.",
                retry);
        }
        if (response.StatusCode != HttpStatusCode.Created)
            throw new RelayPairingException("pairing_failed", "The relay could not start a pairing request.");

        using var document = await ReadJsonAsync(response.Content, "pairing_failed", ct).ConfigureAwait(false);
        var root = document.RootElement;
        var pairingId = RequireSafeValue(root, "pairing_id", 1, 128, "pairing identifier");
        var userCode = RequireSafeValue(root, "user_code", 3, 32, "user code", allowDashOnly: true);
        var pollToken = RequireSafeValue(root, "poll_token", 10, 1024, "poll credential");
        var verificationUri = RequireAbsoluteUri(root, "verification_uri");
        var verificationUriComplete = RequireAbsoluteUri(root, "verification_uri_complete");
        EnsureSameOrigin(normalizedBase, verificationUri);
        EnsureSameOrigin(normalizedBase, verificationUriComplete);

        var expiresIn = RequireInt32(root, "expires_in");
        var interval = RequireInt32(root, "interval");
        return new RelayPairingRequest(
            pairingId,
            userCode,
            verificationUri,
            verificationUriComplete,
            pollToken,
            expiresIn,
            interval);
    }

    public async Task<RelayPairingPoll> PollAsync(
        Uri baseUri,
        string pairingId,
        string pollToken,
        CancellationToken ct)
    {
        var normalizedBase = ValidateBaseUri(baseUri);
        ValidatePathValue(pairingId, "pairing identifier", 128);
        ValidateCredential(pollToken, "poll credential");

        using var request = CreateRequest(
            HttpMethod.Get,
            new Uri(normalizedBase, $"v1/pairing/{Uri.EscapeDataString(pairingId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pollToken);
        using var response = await SendAsync(request, "network_error", ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var (code, retry) = await ReadErrorAsync(response.Content, ct).ConfigureAwait(false);
            if (string.Equals(code, "slow_down", StringComparison.Ordinal))
                return new RelayPairingPoll("slow_down", RetryAfterMilliseconds: retry);
            throw new RelayPairingException("rate_limited", "The relay is rate-limiting pairing polls.", retry);
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new RelayPairingException("unauthorized", "The relay rejected the pairing poll credential.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new RelayPairingException("not_found", "The relay could not find this pairing request.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new RelayPairingException("poll_failed", "The relay could not check this pairing request.");

        using var document = await ReadJsonAsync(
            response.Content,
            "poll_failed",
            ct,
            transportFailureCode: "network_error").ConfigureAwait(false);
        var root = document.RootElement;
        var status = GetString(root, "status")?.Trim().ToLowerInvariant();
        if (status is "pending" or "denied" or "expired" or "consumed")
            return new RelayPairingPoll(status);
        if (status != "approved")
            throw new RelayPairingException("poll_failed", "The relay returned an invalid pairing state.");

        var installationId = RequireSafeValue(root, "installation_id", 1, 128, "installation identifier");
        var installationToken = GetString(root, "installation_token") ?? "";
        if (!RelayChannelAdapter.IsInstallationToken(installationToken))
            throw new RelayPairingException("invalid_credential", "The relay returned an invalid installation credential.");
        var relayName = NormalizeOptional(GetString(root, "relay_name"));
        if (relayName is { Length: > 100 } || relayName?.Any(char.IsControl) == true)
            throw new RelayPairingException("poll_failed", "The relay returned invalid installation details.");

        return new RelayPairingPoll("approved", installationId, installationToken, relayName);
    }

    public async Task<RelayInstallation> VerifyAsync(
        Uri baseUri,
        string installationToken,
        CancellationToken ct)
    {
        var normalizedBase = ValidateBaseUri(baseUri);
        if (!RelayChannelAdapter.IsInstallationToken(installationToken))
            throw new ArgumentException("The Relay installation credential is invalid.", nameof(installationToken));

        using var request = CreateRequest(HttpMethod.Get, new Uri(normalizedBase, "v1/installation"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", installationToken);
        using var response = await SendAsync(request, "verification_failed", ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new RelayPairingException(
                "credential_rejected",
                "The relay did not accept the paired installation credential.");

        using var document = await ReadJsonAsync(response.Content, "verification_failed", ct).ConfigureAwait(false);
        var root = document.RootElement;
        var installationId = RequireSafeValue(root, "installation_id", 1, 128, "installation identifier");
        var displayName = NormalizeOptional(GetString(root, "display_name"));
        if (displayName is { Length: > 100 } || displayName?.Any(char.IsControl) == true)
            throw new RelayPairingException("verification_failed", "The relay returned invalid installation details.");
        return new RelayInstallation(
            installationId,
            displayName,
            GetString(root, "created_at"),
            GetString(root, "last_seen_at"));
    }

    public async Task<RelayDeviceSummary> GetDevicesAsync(
        Uri baseUri,
        string installationToken,
        CancellationToken ct)
    {
        var normalizedBase = ValidateBaseUri(baseUri);
        if (!RelayChannelAdapter.IsInstallationToken(installationToken))
            throw new ArgumentException("The Relay installation credential is invalid.", nameof(installationToken));

        using var request = CreateRequest(HttpMethod.Get, new Uri(normalizedBase, "v1/devices"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", installationToken);
        using var response = await SendAsync(request, "device_discovery_failed", ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new RelayPairingException(
                "device_discovery_failed",
                "The relay could not list paired devices.");

        using var document = await ReadJsonAsync(response.Content, "device_discovery_failed", ct)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("devices", out var devices) ||
            devices.ValueKind != JsonValueKind.Array)
            throw new RelayPairingException(
                "device_discovery_failed",
                "The relay returned an invalid device list.");

        var count = devices.EnumerateArray().Count(device =>
            device.ValueKind == JsonValueKind.Object &&
            !string.IsNullOrWhiteSpace(GetString(device, "device_id")) &&
            string.IsNullOrWhiteSpace(GetString(device, "revoked_at")));
        return new RelayDeviceSummary(count);
    }

    public async Task<RelayPairingPoll> WaitForApprovalAsync(
        Uri baseUri,
        RelayPairingRequest pairing,
        Func<RelayPairingProgress, Task>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        var interval = TimeSpan.FromSeconds(Math.Clamp(pairing.Interval, 1, 60));
        var deadline = _utcNow().AddSeconds(Math.Clamp(pairing.ExpiresIn, 30, 1800));
        var consecutiveNetworkFailures = 0;

        while (!ct.IsCancellationRequested && _utcNow() < deadline)
        {
            var remaining = deadline - _utcNow();
            if (progress is not null)
                await progress(new RelayPairingProgress(
                    remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining,
                    interval,
                    consecutiveNetworkFailures)).ConfigureAwait(false);

            var delay = interval < remaining ? interval : remaining;
            await _delayAsync(delay, ct).ConfigureAwait(false);
            if (_utcNow() >= deadline)
                break;
            try
            {
                var poll = await PollAsync(baseUri, pairing.PairingId, pairing.PollToken, ct)
                    .ConfigureAwait(false);
                consecutiveNetworkFailures = 0;
                switch (poll.Status)
                {
                    case "pending":
                        continue;
                    case "slow_down":
                        interval = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds + 5, 60));
                        continue;
                    case "approved":
                    case "denied":
                    case "expired":
                        return poll;
                    case "consumed":
                        throw new RelayPairingException(
                            "consumed",
                            "The pairing result was already collected and cannot be recovered.");
                    default:
                        throw new RelayPairingException("poll_failed", "The relay returned an invalid pairing state.");
                }
            }
            catch (RelayPairingException exception) when (exception.Code == "network_error")
            {
                consecutiveNetworkFailures++;
                if (consecutiveNetworkFailures >= 5)
                    throw new RelayPairingException(
                        "network_error",
                        "The relay could not be reached after several pairing checks.");
            }
        }

        ct.ThrowIfCancellationRequested();
        return new RelayPairingPoll("expired");
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    public static void EnsureSameOrigin(Uri relayBaseUri, Uri verificationUri)
    {
        ArgumentNullException.ThrowIfNull(relayBaseUri);
        ArgumentNullException.ThrowIfNull(verificationUri);
        if (!verificationUri.IsAbsoluteUri ||
            !string.Equals(relayBaseUri.Scheme, verificationUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(relayBaseUri.Host, verificationUri.Host, StringComparison.OrdinalIgnoreCase) ||
            relayBaseUri.Port != verificationUri.Port)
            throw new RelayPairingException(
                "unexpected_verification_uri",
                "The relay returned an unexpected verification URL.");
    }

    private Uri ValidateBaseUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        return RelayChannelAdapter.ValidateRelayUrl(baseUri.AbsoluteUri, _allowPrivateNetwork);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        RelayHttpTransport.MarkValidated(request, _allowPrivateNetwork);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string failureCode,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RelayPairingException(failureCode, "The relay request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new RelayPairingException(failureCode, "The relay could not be reached.");
        }
        catch (IOException)
        {
            throw new RelayPairingException(failureCode, "The relay connection failed.");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpContent content,
        string failureCode,
        CancellationToken ct,
        string? transportFailureCode = null)
    {
        try
        {
            var bytes = await ReadBoundedAsync(
                content,
                transportFailureCode ?? failureCode,
                ct).ConfigureAwait(false);
            return JsonDocument.Parse(bytes);
        }
        catch (RelayPairingException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new RelayPairingException(failureCode, "The relay returned an invalid response.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        string failureCode,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(block, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (buffer.Length + read > MaximumResponseBytes)
                    throw new RelayPairingException("response_too_large", "The relay response was too large.");
                buffer.Write(block, 0, read);
            }
            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RelayPairingException(failureCode, "The relay response timed out.");
        }
        catch (IOException)
        {
            throw new RelayPairingException(failureCode, "The relay response could not be read.");
        }
        catch (HttpRequestException)
        {
            throw new RelayPairingException(failureCode, "The relay response could not be read.");
        }
    }

    private static async Task<int?> ReadRetryAfterAsync(HttpContent content, CancellationToken ct)
    {
        var (_, retry) = await ReadErrorAsync(content, ct).ConfigureAwait(false);
        return retry;
    }

    private static async Task<(string? Code, int? RetryAfter)> ReadErrorAsync(
        HttpContent content,
        CancellationToken ct)
    {
        try
        {
            using var document = await ReadJsonAsync(content, "pairing_failed", ct).ConfigureAwait(false);
            var root = document.RootElement;
            var error = root.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("error", out var value) &&
                        value.ValueKind == JsonValueKind.Object
                ? value
                : root;
            var code = GetString(error, "code");
            int? retry = error.TryGetProperty("retry_after_ms", out var retryValue) &&
                         retryValue.TryGetInt32(out var retryMilliseconds)
                ? Math.Clamp(retryMilliseconds, 0, 600_000)
                : null;
            return (code, retry);
        }
        catch (RelayPairingException)
        {
            return (null, null);
        }
    }

    private static string RequireSafeValue(
        JsonElement root,
        string propertyName,
        int minimumLength,
        int maximumLength,
        string label,
        bool allowDashOnly = false)
    {
        var value = GetString(root, propertyName);
        if (value is null || value.Length < minimumLength || value.Length > maximumLength ||
            value.Any(character => allowDashOnly
                ? !(char.IsAsciiLetterOrDigit(character) || character == '-')
                : !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
            throw new RelayPairingException("pairing_failed", $"The relay returned an invalid {label}.");
        return value;
    }

    private static Uri RequireAbsoluteUri(JsonElement root, string propertyName)
    {
        var value = GetString(root, propertyName);
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new RelayPairingException("pairing_failed", "The relay returned an invalid verification URL.");
        return uri;
    }

    private static int RequireInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || !value.TryGetInt32(out var number))
            throw new RelayPairingException("pairing_failed", "The relay returned invalid pairing timing.");
        return number;
    }

    private static void ValidatePathValue(string value, string label, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
            throw new ArgumentException($"Relay {label} is invalid.");
    }

    private static void ValidateCredential(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')))
            throw new ArgumentException($"Relay {label} is invalid.");
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? GetString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record BeginRequest(
        [property: JsonPropertyName("sender_name")] string? SenderName,
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("client_version")] string ClientVersion);
}
