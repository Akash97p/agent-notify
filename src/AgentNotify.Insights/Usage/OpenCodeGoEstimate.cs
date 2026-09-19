namespace AgentNotify.Insights.Usage;

/// <summary>Local OpenCode observations against Go's published per-model dollar caps, not provider quota.</summary>
/// <param name="RenewalDay">The owner's plan renewal day of the month, when set; the monthly window follows it.</param>
public sealed record OpenCodeGoEstimate(string Status, IReadOnlyList<OpenCodeGoModelEstimate> Models, string Message,
    int? RenewalDay = null)
{
    public string PricingAsOf => ApiPriceCatalog.OpenCodeGoAsOf;
}

public sealed record OpenCodeGoModelEstimate(string Model, IReadOnlyList<OpenCodeGoWindowEstimate> Windows);

/// <param name="StartsAt">The earliest request the window counts.</param>
/// <param name="ResetsAt">When a fixed window starts over; null for a window that rolls with the clock.</param>
public sealed record OpenCodeGoWindowEstimate(string Key, string Label, decimal ObservedUsd,
    decimal? LimitUsd, double? EstimatedUsedPercent, int Records, int UnpricedRecords,
    DateTimeOffset StartsAt, DateTimeOffset? ResetsAt);

