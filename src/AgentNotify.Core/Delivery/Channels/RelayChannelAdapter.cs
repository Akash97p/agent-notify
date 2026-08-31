using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentNotify.Protocol;

namespace AgentNotify.Core.Delivery.Channels;

/// <summary>
/// AgentNotify Relay adapter — experimental opaque transport. Sends a per-device envelope to a
/// self-hosted or Relay Go endpoint. Local notification history remains authoritative even if the
/// relay is unavailable. Encryption is currently opaque experimental transport (base64url wire)
/// and does not yet claim end-to-end guarantees.
/// </summary>
public sealed class RelayChannelAdapter : IOutboundChannelAdapter, IDisposable
{
    /// <summary>
    /// Relay-specific serialization. Absent optional metadata must be omitted
    /// rather than written as an explicit null: the Relay treats a null on an
    /// optional field as a type error, and "not provided" is what is meant.
    /// Scoped to this adapter because <see cref="Json.Options"/> is shared with
    /// every other outbound channel.
    /// </summary>
    private static readonly JsonSerializerOptions RelayJsonOptions =
        new(Json.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public RelayChannelAdapter(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? RelayHttpTransport.CreateClient();
    }

    public string Kind => "relay";

    public async Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken)
    {
        RelayConfiguration config;
        Uri relayBase;
        string installationToken;
        string envelopeJson;
        string idempotencyKey = delivery.OutboxId;

        try
        {
            config = ParseAndValidateConfiguration(delivery.Profile.ConfigJson, delivery.Secrets);
            relayBase = ValidateRelayUrl(config.RelayUrl!, config.AllowPrivateNetwork);
            if (!delivery.Secrets.TryGetValue(config.InstallationTokenSecretName, out installationToken!) ||
                !IsInstallationToken(installationToken))
                throw new ArgumentException("An encrypted Relay installation token is required.");
            envelopeJson = await BuildEnvelopeAsync(delivery, config, relayBase, installationToken, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RelayPreparationException exception)
        {
            return exception.Retryable
                ? DeliveryResult.Retry(exception.Code, exception.StatusCode)
                : DeliveryResult.PermanentFailure(exception.Code, exception.StatusCode);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return DeliveryResult.PermanentFailure("configuration_invalid");
        }

        var endpoint = new Uri(relayBase, "v1/envelopes");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(envelopeJson, Encoding.UTF8, "application/json")
        };
        RelayHttpTransport.MarkValidated(request, config.AllowPrivateNetwork);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", installationToken);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", delivery.OutboxId);

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var status = (int)response.StatusCode;
            if (status is >= 200 and <= 299)
                return await HasValidAcknowledgementAsync(response.Content, cancellationToken)
                    ? DeliveryResult.Success(status)
                    : DeliveryResult.Retry("relay_invalid_response", status);
            if (status is 408 or 425 or 429 || status >= 500)
                return DeliveryResult.Retry($"relay_{status}", status);
            if (status is >= 300 and <= 399)
                return DeliveryResult.PermanentFailure("relay_redirect", status);
            return DeliveryResult.PermanentFailure($"relay_{status}", status);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return DeliveryResult.Retry("network_error");
        }
        catch (InvalidDataException)
        {
            return DeliveryResult.Retry("relay_invalid_response");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    internal static RelayConfiguration ParseAndValidateConfiguration(
        string configJson,
        IReadOnlyDictionary<string, string> secrets)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            throw new ArgumentException("Relay configuration is required.");

        using var document = JsonDocument.Parse(configJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Relay configuration must be a JSON object.");

        var config = new RelayConfiguration();

        // deployment: custom (default) or relay_go (coming soon — disabled)
        var deployment = GetString(root, "deployment") ?? GetString(root, "Deployment") ?? "custom";
        deployment = deployment.Trim().ToLowerInvariant();
        if (deployment != "custom" && deployment != "relay_go")
            throw new ArgumentException("Relay deployment must be custom or relay_go.");
        if (deployment == "relay_go")
            throw new ArgumentException("Relay Go is coming soon — choose Custom and enter your self-hosted base URL.");
        config.Deployment = deployment;

        // relay_url / relayUrl — required
        var relayUrl = GetString(root, "relay_url") ?? GetString(root, "relayUrl") ?? GetString(root, "RelayUrl");
        if (string.IsNullOrWhiteSpace(relayUrl))
            throw new ArgumentException("Relay base URL is required.");
        relayUrl = relayUrl.Trim();
        if (relayUrl.Length > 2048)
            throw new ArgumentException("Relay base URL is too long.");
        config.RelayUrl = relayUrl;

        // sender_name / senderName — optional ≤100
        var senderName = GetString(root, "sender_name") ?? GetString(root, "senderName") ?? GetString(root, "SenderName");
        if (senderName is not null)
        {
            senderName = senderName.Trim();
            if (senderName.Length > 100)
                throw new ArgumentException("Relay sender name must be at most 100 characters.");
            if (senderName.Any(char.IsControl))
                throw new ArgumentException("Relay sender name contains invalid characters.");
            config.SenderName = senderName;
        }

        // allowPrivateNetwork / allow_private_network — optional
        if (root.TryGetProperty("allowPrivateNetwork", out var allowPrivate1) && allowPrivate1.ValueKind == JsonValueKind.True)
            config.AllowPrivateNetwork = true;
        else if (root.TryGetProperty("allow_private_network", out var allowPrivate2) && allowPrivate2.ValueKind == JsonValueKind.True)
            config.AllowPrivateNetwork = true;
        else if (root.TryGetProperty("AllowPrivateNetwork", out var allowPrivate3) && allowPrivate3.ValueKind == JsonValueKind.True)
            config.AllowPrivateNetwork = true;

        // installation token secret name — fixed, but allow override for tests
        var tokenSecretName = GetString(root, "installationTokenSecretName") ?? GetString(root, "installation_token_secret_name") ?? "installation_token";
        if (string.IsNullOrWhiteSpace(tokenSecretName) || tokenSecretName.Length > 64)
            throw new ArgumentException("Relay installation token secret name is invalid.");
        config.InstallationTokenSecretName = tokenSecretName.Trim();

        var installationId = GetString(root, "installation_id") ?? GetString(root, "installationId");
        if (!string.IsNullOrWhiteSpace(installationId))
        {
            installationId = installationId.Trim();
            if (installationId.Length > 128 ||
                installationId.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')))
                throw new ArgumentException("Relay installation ID is invalid.");
            config.InstallationId = installationId;
        }

        // Optional explicit device pinning for tests or manual config
        var deviceId = GetString(root, "deviceId") ?? GetString(root, "device_id");
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = deviceId.Trim();
            if (deviceId.Length is < 1 or > 64 || deviceId.Any(char.IsControl))
                throw new ArgumentException("Relay device ID is invalid.");
            config.DeviceId = deviceId;
        }

        var keyId = GetString(root, "keyId") ?? GetString(root, "key_id");
        if (!string.IsNullOrWhiteSpace(keyId))
        {
            keyId = keyId.Trim();
            if (keyId.Length is < 1 or > 128 || keyId.Any(char.IsControl))
                throw new ArgumentException("Relay key ID is invalid.");
            config.KeyId = keyId;
        }

        // Validate relay URL now (also validates host policy)
        _ = ValidateRelayUrl(config.RelayUrl, config.AllowPrivateNetwork);

        // If deployment is relay_go, we still validate same URL rules but could add host check later.
        // For now treat identically; UI disables selection until hosted URL ready.

        return config;
    }

    public static Uri ValidateRelayUrl(string value, bool allowPrivate)
    {
        if (value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Relay server must be an absolute URL without credentials or fragments.");

        var isHttp = uri.Scheme == Uri.UriSchemeHttp;
        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        if (!isHttp && !isHttps)
            throw new ArgumentException("Relay server must be HTTPS, or HTTP only for localhost development.");

        if (isHttp && !RelayHttpTransport.IsLocalhost(uri.Host))
            throw new ArgumentException("HTTP is allowed only for localhost development.");

        if (!string.IsNullOrEmpty(uri.Query))
            throw new ArgumentException("Relay base URL must not contain a query string.");

        var hostForDns = uri.Host.TrimStart('[').TrimEnd(']');
        if (IPAddress.TryParse(hostForDns, out var address) &&
            !WebhookChannelAdapter.IsAddressAllowed(address, allowPrivate))
            throw new ArgumentException("Relay server address is not allowed.");

        // Host is localhost string — requires explicit private consent like other adapters.
        if (RelayHttpTransport.IsLocalhost(uri.Host) && !allowPrivate)
            throw new ArgumentException("Private Relay destinations require explicit consent.");

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.Length > 128 || segment.Contains('%', StringComparison.Ordinal) ||
                                   !segment.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')))
            throw new ArgumentException("Relay server base path is invalid.");
        if (segments.Any(s => s.Equals("v1", StringComparison.OrdinalIgnoreCase) || s.Equals("envelopes", StringComparison.OrdinalIgnoreCase)))
        {
            // Base URL should not include the terminal /v1/envelopes; we append it.
            if (segments.Length > 0 && (segments[^1].Equals("envelopes", StringComparison.OrdinalIgnoreCase) ||
                                        (segments.Length >= 2 && segments[^2].Equals("v1", StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("Relay base URL must not include /v1/envelopes.");
        }

        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    public static bool IsInstallationToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 20 || value.Length > 512)
            return false;
        if (!value.StartsWith("inst_", StringComparison.Ordinal))
            return false;
        var suffix = value["inst_".Length..];
        if (suffix.Length < 10)
            return false;
        return suffix.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');
    }

    private async Task<string> BuildEnvelopeAsync(
        OutboundDelivery delivery,
        RelayConfiguration config,
        Uri relayBase,
        string installationToken,
        CancellationToken cancellationToken)
    {
        // Payload is already route-redacted JSON; we use it as plaintext.
        // Validate it is a JSON object.
        using var doc = JsonDocument.Parse(delivery.PayloadJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Notification payload must be a JSON object.");

        var plaintext = delivery.PayloadJson;
        var clientEventId = delivery.NotificationId;
        if (string.IsNullOrWhiteSpace(clientEventId) || clientEventId.Length > 128)
            clientEventId = delivery.OutboxId;
        // DateTime (not DateTimeOffset) with Kind.Utc renders the round-trip format
        // with a trailing Z. DateTimeOffset.ToString("O") emits "+00:00" instead,
        // which is valid RFC 3339 but which many JSON schema validators reject.
        var expiresAt = DateTime.UtcNow.AddHours(24).ToString("O", CultureInfo.InvariantCulture);

        // One value for both the AAD and the wire. The recipient rebuilds the AAD
        // from the sender_id the relay hands it, so these two must never diverge —
        // if they do, every envelope fails authentication on the device. Pairing
        // always supplies an installation id; the fallback only covers a provider
        // configured by hand before pairing existed.
        var senderId = string.IsNullOrWhiteSpace(config.InstallationId)
            ? "local-installation"
            : config.InstallationId!;

        // Discover the active devices before building the envelope. A sender pairing creates an
        // installation, not a recipient, so an empty list is an actionable configuration state.
        var recipients = new List<RelayEnvelopeRecipient>();
        var fetched = await TryFetchDevicesAsync(
            relayBase,
            installationToken,
            config.AllowPrivateNetwork,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(config.DeviceId))
        {
            var pinned = fetched.FirstOrDefault(device =>
                string.Equals(device.DeviceId, config.DeviceId, StringComparison.Ordinal));
            if (pinned is null)
                throw new RelayPreparationException("relay_device_not_found", retryable: false);
            AddRecipient(pinned, config.KeyId);
        }
        else
        {
            if (fetched.Count == 0)
                throw new RelayPreparationException("no_devices_paired", retryable: false);
            foreach (var device in fetched.Take(10))
                AddRecipient(device, keyIdOverride: null);
        }

        void AddRecipient(FetchedDevice device, string? keyIdOverride)
        {
            var keyId = string.IsNullOrWhiteSpace(keyIdOverride)
                ? device.KeyId ?? "k1"
                : keyIdOverride;
            var ciphertext = GenerateCiphertext(
                plaintext,
                senderInstallationId: senderId,
                deviceId: device.DeviceId,
                keyId: keyId,
                clientEventId: clientEventId,
                expiresAt: expiresAt,
                devicePublicKey: device.PublicKey);
            recipients.Add(new RelayEnvelopeRecipient(device.DeviceId, keyId, ciphertext));
        }

        var envelope = new RelayEnvelopeRequest(
            envelope_version: "1",
            client_event_id: clientEventId,
            expires_at: expiresAt,
            recipients: recipients,
            sender_name: string.IsNullOrWhiteSpace(config.SenderName) ? null : config.SenderName,
            sender_id: senderId);

        var json = JsonSerializer.Serialize(envelope, RelayJsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 64 * 1024)
            throw new InvalidOperationException("Relay envelope is too large.");
        return json;
    }

    private async Task<List<FetchedDevice>> TryFetchDevicesAsync(
        Uri relayBase,
        string installationToken,
        bool allowPrivateNetwork,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = new Uri(relayBase, "v1/devices");
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            RelayHttpTransport.MarkValidated(request, allowPrivateNetwork);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", installationToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentNotify", "1.0"));

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (status is < 200 or > 299)
                throw new RelayPreparationException(
                    $"relay_devices_{status}",
                    retryable: status is 408 or 425 or 429 || status >= 500,
                    status);

            const int maxBytes = 64 * 1024;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (ms.Length + read > maxBytes)
                    throw new RelayPreparationException("relay_invalid_response", retryable: true);
                ms.Write(buffer, 0, read);
            }
            using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                throw new RelayPreparationException("relay_invalid_response", retryable: true);
            var list = new List<FetchedDevice>();
            foreach (var d in devices.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                var deviceId = GetString(d, "device_id") ?? GetString(d, "deviceId");
                var keyId = GetString(d, "key_id") ?? GetString(d, "keyId");
                var publicKey = GetString(d, "public_key") ?? GetString(d, "publicKey");
                var revokedAt = GetString(d, "revoked_at") ?? GetString(d, "revokedAt");
                if (string.IsNullOrWhiteSpace(deviceId)) continue;
                if (!string.IsNullOrWhiteSpace(revokedAt)) continue; // skip revoked
                list.Add(new FetchedDevice(deviceId!.Trim(), keyId?.Trim(), publicKey?.Trim()));
            }
            return list;
        }
        catch (OperationCanceledException) { throw; }
        catch (RelayPreparationException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new RelayPreparationException("network_error", retryable: true);
        }
        catch (JsonException)
        {
            throw new RelayPreparationException("relay_invalid_response", retryable: true);
        }
    }

    private static string GenerateCiphertext(
        string plaintext,
        string senderInstallationId,
        string deviceId,
        string keyId,
        string clientEventId,
        string expiresAt,
        string? devicePublicKey)
    {
        // Experimental opaque transport: for now, produce base64url(nonce 24 || ephemPub 32 || plaintextUtf8).
        // When a device public key is available and NSec is added, replace with X25519+XChaCha20Poly1305
        // using AAD = "1|sender|device|keyId|eventId|expiresAt".
        // This placeholder is intentionally not claimed as E2E.

        // If we have a real public key and could do crypto, we would use it here.
        // For determinism in tests when plaintext is small, this still produces >=72 bytes.

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(24);
        var ephemPub = RandomNumberGenerator.GetBytes(32);
        var wire = new byte[24 + 32 + plainBytes.Length];
        Buffer.BlockCopy(nonce, 0, wire, 0, 24);
        Buffer.BlockCopy(ephemPub, 0, wire, 24, 32);
        Buffer.BlockCopy(plainBytes, 0, wire, 56, plainBytes.Length);
        return Base64UrlEncode(wire);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string? GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();
        return null;
    }

    private static async Task<bool> HasValidAcknowledgementAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maxBytes = 64 * 1024;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (ms.Length + read > maxBytes)
                throw new InvalidDataException("Relay response too large.");
            ms.Write(buffer, 0, read);
        }
        if (ms.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            // Accept either {envelope_id, status} or {status:"duplicate"} etc.
            if (root.TryGetProperty("envelope_id", out var eid) && eid.ValueKind == JsonValueKind.String && eid.GetString() is { Length: >= 5 })
                return true;
            if (root.TryGetProperty("envelopeId", out var eid2) && eid2.ValueKind == JsonValueKind.String && eid2.GetString() is { Length: >= 5 })
                return true;
            if (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                var s = status.GetString();
                return s is "accepted" or "duplicate";
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class FetchedDevice
    {
        public FetchedDevice(string deviceId, string? keyId, string? publicKey)
        {
            DeviceId = deviceId;
            KeyId = keyId;
            PublicKey = publicKey;
        }
        public string DeviceId { get; }
        public string? KeyId { get; }
        public string? PublicKey { get; }
    }

    internal sealed class RelayConfiguration
    {
        public string Deployment { get; set; } = "custom";
        public string? RelayUrl { get; set; }
        public string? SenderName { get; set; }
        public bool AllowPrivateNetwork { get; set; }
        public string InstallationTokenSecretName { get; set; } = "installation_token";
        public string? InstallationId { get; set; }
        public string? DeviceId { get; set; }
        public string? KeyId { get; set; }
    }

    private sealed record RelayEnvelopeRecipient(
        [property: JsonPropertyName("device_id")] string device_id,
        [property: JsonPropertyName("key_id")] string key_id,
        [property: JsonPropertyName("ciphertext")] string ciphertext);

    private sealed record RelayEnvelopeRequest(
        [property: JsonPropertyName("envelope_version")] string envelope_version,
        [property: JsonPropertyName("client_event_id")] string client_event_id,
        [property: JsonPropertyName("expires_at")] string expires_at,
        [property: JsonPropertyName("recipients")] IReadOnlyList<RelayEnvelopeRecipient> recipients,
        [property: JsonPropertyName("sender_name")] string? sender_name,
        [property: JsonPropertyName("sender_id")] string? sender_id);

    private sealed class RelayPreparationException : Exception
    {
        public RelayPreparationException(string code, bool retryable, int? statusCode = null)
        {
            Code = code;
            Retryable = retryable;
            StatusCode = statusCode;
        }

        public string Code { get; }
        public bool Retryable { get; }
        public int? StatusCode { get; }
    }
}
