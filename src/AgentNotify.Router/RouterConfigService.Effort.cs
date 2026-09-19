using System.Text.RegularExpressions;
using AgentNotify.Core.Config;
using AgentNotify.Core.Delivery;

namespace AgentNotify.Router;

public sealed partial class RouterConfigService
{
    public async Task<IReadOnlyList<RouterEffortCapability>> ListEffortCapabilitiesAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var overrides = snapshot.EffortMappings ?? [];
        var familyOverrides = snapshot.EffortFamilyOverrides ?? [];
        return snapshot.Upstreams.Where(upstream => upstream.Enabled)
            .SelectMany(upstream => upstream.Models.Select(model =>
                RouterEffortCatalog.Resolve(upstream, model, overrides, familyOverrides)))
            .ToList();
    }

    /// <summary>
    /// The routed models grouped by family, with the effective capability each family gets. A family
    /// override beats the automatic vocabulary; a per-model override beats both and is counted.
    /// </summary>
    public async Task<IReadOnlyList<RouterEffortFamily>> ListEffortFamiliesAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var overrides = snapshot.EffortMappings ?? [];
        var familyOverrides = snapshot.EffortFamilyOverrides ?? [];
        var families = new Dictionary<string, RouterEffortFamily>();
        foreach (var upstream in snapshot.Upstreams.Where(upstream => upstream.Enabled))
        {
            foreach (var model in upstream.Models)
            {
                var family = RouterEffortCatalog.Family(model);
                var (supported, map, _) = RouterEffortCatalog.FamilyAutomatic(family);
                var familyOverride = familyOverrides.FirstOrDefault(item => item.Family == family);
                if (!families.TryGetValue(family, out var entry))
                {
                    families[family] = entry = new RouterEffortFamily(family,
                        familyOverride is null ? "automatic" : "family",
                        familyOverride?.SupportedValues ?? supported,
                        familyOverride?.LevelMap ?? map,
                        familyOverride?.DefaultValue, [], 0);
                }
                var concrete = new RouterEffortFamilyModel(upstream.Id, upstream.Slug, model, upstream.WireFor(model));
                families[family] = entry with { Models = [.. entry.Models, concrete] };
            }
        }
        foreach (var family in families.Keys.ToList())
        {
            var modelOverrides = overrides.Count(mapping =>
                families[family].Models.Any(concrete =>
                    concrete.UpstreamId == mapping.UpstreamId && concrete.Model == mapping.Model));
            families[family] = families[family] with { ModelOverrideCount = modelOverrides };
        }
        return families.Values.OrderBy(item => item.Family, StringComparer.Ordinal).ToList();
    }

    public async Task SetEffortMappingAsync(string upstreamId, string model,
        IReadOnlyList<string>? supportedValues, IReadOnlyList<string>? levelMap, string? defaultValue,
        CancellationToken ct = default)
    {
        defaultValue = string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue.Trim();
        RouterEffortCatalog.Validate(supportedValues, levelMap, defaultValue);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        var upstream = await _repository.GetUpstreamAsync(upstreamId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("That upstream was not found.");
        if (!upstream.Models.Contains(model, StringComparer.Ordinal))
            throw new ArgumentException("That model is not declared by the upstream.");
        await _repository.SetEffortMappingAsync(
            new RouterEffortMapping(upstreamId, model, supportedValues!, levelMap!, defaultValue), ct).ConfigureAwait(false);
        Invalidate();
    }

    public async Task ResetEffortMappingAsync(string upstreamId, string model, CancellationToken ct = default)
    {
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        await _repository.DeleteEffortMappingAsync(upstreamId, model, ct).ConfigureAwait(false);
        Invalidate();
    }

    public async Task SetEffortFamilyMappingAsync(string family,
        IReadOnlyList<string>? supportedValues, IReadOnlyList<string>? levelMap, string? defaultValue,
        CancellationToken ct = default)
    {
        family = family.Trim();
        RouterEffortCatalog.ValidateFamily(family);
        defaultValue = string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue.Trim();
        RouterEffortCatalog.Validate(supportedValues, levelMap, defaultValue);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        await _repository.SetEffortFamilyOverrideAsync(
            new RouterEffortFamilyOverride(family, supportedValues!, levelMap!, defaultValue), ct).ConfigureAwait(false);
        Invalidate();
    }

    public async Task ResetEffortFamilyMappingAsync(string family, CancellationToken ct = default)
    {
        family = family.Trim();
        RouterEffortCatalog.ValidateFamily(family);
        await _repository.InitializeAsync(ct).ConfigureAwait(false);
        await _repository.DeleteEffortFamilyOverrideAsync(family, ct).ConfigureAwait(false);
        Invalidate();
    }

}
