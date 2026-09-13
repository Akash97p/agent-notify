namespace AgentNotify.Core.Delivery.Forms;

/// <summary>
/// Saves a provider editor submission: validates it completely, then writes the profile and its
/// secret changes through <see cref="ProviderProfileService"/>.
/// </summary>
public sealed class ProviderFormService
{
    private readonly ProviderProfileService _profiles;

    public ProviderFormService(ProviderProfileService profiles) => _profiles = profiles;

    /// <summary>Creates a profile when <paramref name="id"/> is null, otherwise updates that profile.</summary>
    /// <exception cref="ArgumentException">The submission is invalid; nothing was written.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="id"/> names no profile.</exception>
    public async Task<ProviderProfile> SaveAsync(
        string? id,
        string kind,
        ProviderFormInput input,
        CancellationToken ct = default)
    {
        ProviderProfile? existing = null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            existing = (await _profiles.ListAsync(ct)).FirstOrDefault(profile => profile.Id == id)
                ?? throw new KeyNotFoundException("Provider profile not found.");
        }

        var result = ProviderFormBuilder.Build(existing?.Kind ?? kind, input, existing);

        if (existing is null)
        {
            return await _profiles.SaveAsync(
                null,
                input.Name,
                ProviderFormCatalog.Find(kind)!.Kind,
                input.Enabled,
                result.ConfigJson,
                result.SecretChanges,
                ct);
        }

        var saved = await _profiles.SaveAsync(existing.Id, input.Name, existing.Kind, input.Enabled, result.ConfigJson, secrets: null, ct);
        if (result.SecretChanges.Count > 0 || result.SecretRemovals.Count > 0)
        {
            await _profiles.UpdateSecretsAsync(saved.Id, result.SecretChanges, result.SecretRemovals, ct);
            saved = (await _profiles.ListAsync(ct)).First(profile => profile.Id == saved.Id);
        }

        return saved;
    }
}
