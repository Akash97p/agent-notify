using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotify.Contracts;

namespace AgentNotify.Core.Delivery;

/// <summary>
/// Validates provider profiles and keeps plaintext credentials outside persistence DTOs.
/// A null <c>secrets</c> argument preserves existing credentials; an empty dictionary clears them.
/// </summary>
public sealed partial class ProviderProfileService
{
    private const int MaximumConfigBytes = 64 * 1024;
    private const int MaximumSecretCount = 32;
    private const int MaximumSecretCharacters = 16 * 1024;

    private readonly IDeliveryRepository _repository;
    private readonly ISecretProtector _protector;

    public ProviderProfileService(IDeliveryRepository repository, ISecretProtector protector)
    {
        _repository = repository;
        _protector = protector;
    }

    public Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken ct = default) =>
        _repository.ListProvidersAsync(ct);

    public async Task<ProviderProfile> SaveAsync(
        string? id,
        string name,
        string kind,
        bool enabled,
        string configJson,
        IReadOnlyDictionary<string, string>? secrets,
        CancellationToken ct = default)
    {
        var normalizedName = ValidateName(name);
        var normalizedKind = ValidateKind(kind);
        var normalizedConfig = ValidateConfig(configJson);
        ValidateSecrets(secrets);

        var existing = string.IsNullOrWhiteSpace(id)
            ? null
            : await _repository.GetProviderAsync(id, ct);
        var encryptedSecrets = existing?.EncryptedSecrets ?? _protector.Protect("{}");
        IReadOnlyList<string> secretNames = existing?.SecretNames ?? [];

        if (secrets is not null)
        {
            encryptedSecrets = _protector.Protect(JsonSerializer.Serialize(secrets, Json.Options));
            secretNames = secrets.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        var now = DateTimeOffset.UtcNow;
        var stored = new StoredProviderProfile
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = normalizedName,
            Kind = normalizedKind,
            Enabled = enabled,
            ConfigJson = normalizedConfig,
            EncryptedSecrets = encryptedSecrets,
            SecretNames = secretNames,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };
        await _repository.UpsertProviderAsync(stored, ct);

        return new ProviderProfile
        {
            Id = stored.Id,
            Name = stored.Name,
            Kind = stored.Kind,
            Enabled = stored.Enabled,
            ConfigJson = stored.ConfigJson,
            SecretNames = stored.SecretNames,
            CreatedAt = stored.CreatedAt,
            UpdatedAt = stored.UpdatedAt
        };
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSecretsForDeliveryAsync(
        string profileId,
        CancellationToken ct = default)
    {
        var profile = await _repository.GetProviderAsync(profileId, ct) ??
            throw new KeyNotFoundException("Provider profile not found.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(
                   _protector.Unprotect(profile.EncryptedSecrets),
                   Json.Options)
               ?? new Dictionary<string, string>();
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new ArgumentException(
                "Provider name is required and must be at most 100 characters.",
                nameof(name));
        return name.Trim();
    }

    private static string ValidateKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Provider kind is required.", nameof(kind));

        var normalized = kind.Trim().ToLowerInvariant().Replace('-', '_');
        if (!Identifier().IsMatch(normalized))
            throw new ArgumentException("Provider kind is invalid.", nameof(kind));
        return normalized;
    }

    private static string ValidateConfig(string configJson)
    {
        var normalized = string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson.Trim();
        if (System.Text.Encoding.UTF8.GetByteCount(normalized) > MaximumConfigBytes)
            throw new ArgumentException("Provider configuration is too large.", nameof(configJson));

        using var document = JsonDocument.Parse(normalized);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Provider configuration must be a JSON object.", nameof(configJson));
        return normalized;
    }

    private static void ValidateSecrets(IReadOnlyDictionary<string, string>? secrets)
    {
        if (secrets is null)
            return;
        if (secrets.Count > MaximumSecretCount)
            throw new ArgumentException($"A provider may contain at most {MaximumSecretCount} secrets.", nameof(secrets));

        foreach (var (name, value) in secrets)
        {
            if (!Identifier().IsMatch(name))
                throw new ArgumentException($"Secret name '{name}' is invalid.", nameof(secrets));
            if (value is null || value.Length > MaximumSecretCharacters)
                throw new ArgumentException(
                    $"Secret '{name}' must be at most {MaximumSecretCharacters} characters.",
                    nameof(secrets));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
