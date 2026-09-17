namespace AgentNotify.Core.Billing;

/// <summary>A public API account: never carries a secret value.</summary>
public sealed record BillingAccount(
    string Id,
    string Provider,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A stored API account row, including the sealed key envelope.</summary>
public sealed record StoredBillingAccount(
    string Id,
    string Provider,
    string Label,
    string EncryptedKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record BillingBalance(string Kind, string? Currency, decimal Amount);

public sealed record BillingSpend(string Period, string? Currency, decimal Amount);

public sealed record BillingDaily(string Date, string? Currency, decimal Amount);

public sealed record BillingSnapshot(
    string Id,
    string Provider,
    string Label,
    string Status,
    DateTimeOffset? FetchedAt,
    string? Message,
    IReadOnlyList<BillingBalance> Balances,
    IReadOnlyList<BillingSpend> Spend,
    IReadOnlyList<BillingDaily> Daily,
    bool? Available)
{
    public static BillingSnapshot Unavailable(
        string id,
        string provider,
        string label,
        string status,
        string message) =>
        new(id, provider, label, status, null, message, [], [], [], null);
}

public sealed record BillingReport(DateTimeOffset CheckedAt, IReadOnlyList<BillingSnapshot> Accounts)
{
    public string ContractVersion => "1";
}
