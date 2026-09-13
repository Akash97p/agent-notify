namespace AgentNotify.Core.Delivery.Forms;

/// <summary>How a provider field is edited. Secret fields are write-only.</summary>
public enum ProviderFieldType
{
    Text,
    Url,
    Number,
    Multiline,
    Checkbox,
    Select,
    Secret
}

public sealed record ProviderFieldOption(string Value, string Label);

/// <summary>Shows a field only while another field holds one of <paramref name="Values"/>.</summary>
public sealed record ProviderFieldCondition(string Key, IReadOnlyList<string> Values);

/// <summary>
/// One editable value of a provider profile.
/// </summary>
/// <remarks>
/// A <see cref="ProviderFieldType.Secret"/> field names an encrypted value. Its plaintext is never
/// read back: an editor learns only whether it is stored, and leaving it blank keeps what is stored.
/// <see cref="Clearable"/> marks the optional secrets a user may remove explicitly.
/// </remarks>
public sealed record ProviderField(string Key, string Label, ProviderFieldType Type)
{
    public string? Help { get; init; }
    public string? Placeholder { get; init; }
    public string? Default { get; init; }
    public bool Required { get; init; }
    public bool Clearable { get; init; }
    public bool Advanced { get; init; }
    public IReadOnlyList<ProviderFieldOption>? Options { get; init; }
    public ProviderFieldCondition? ShowWhen { get; init; }
}

/// <summary>Everything an editor needs to present one provider kind.</summary>
public sealed record ProviderKindDescriptor(
    string Kind,
    string DisplayName,
    string Category,
    string Summary,
    IReadOnlyList<ProviderField> Fields)
{
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string? Warning { get; init; }
    public bool Paid { get; init; }
    /// <summary>The kind is linked through an interactive device-grant pairing, not a pasted credential.</summary>
    public bool SupportsPairing { get; init; }
}

/// <summary>A credential obtained by pairing, applied to a Relay profile when it is saved.</summary>
public sealed record RelayPairingOutcome(
    string InstallationToken,
    string InstallationId,
    string? RelayName,
    string InstallId);

/// <summary>
/// Submitted editor state. Values are strings as a form would hold them (checkboxes are
/// <c>true</c>/<c>false</c>); a key that is absent takes the field default.
/// </summary>
public sealed record ProviderFormInput
{
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    public IReadOnlyDictionary<string, string?> Values { get; init; } = new Dictionary<string, string?>();
    /// <summary>Newly entered secret values. Blank entries keep the stored value.</summary>
    public IReadOnlyDictionary<string, string?> Secrets { get; init; } = new Dictionary<string, string?>();
    /// <summary>Clearable secrets the user asked to remove.</summary>
    public IReadOnlyCollection<string> ClearSecrets { get; init; } = [];
    public RelayPairingOutcome? Pairing { get; init; }
}

/// <summary>The persisted shape of a validated form.</summary>
public sealed record ProviderFormResult(
    string ConfigJson,
    IReadOnlyDictionary<string, string> SecretChanges,
    IReadOnlyList<string> SecretRemovals);
