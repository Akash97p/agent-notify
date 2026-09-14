namespace AgentNotify.Core.Usage;

/// <summary>Local OpenCode observations against Go's published per-model dollar caps, not provider quota.</summary>
public sealed record OpenCodeGoEstimate(string Status, IReadOnlyList<OpenCodeGoModelEstimate> Models, string Message)
{
    public string PricingAsOf => ApiPriceCatalog.OpenCodeGoAsOf;
}

public sealed record OpenCodeGoModelEstimate(string Model, IReadOnlyList<OpenCodeGoWindowEstimate> Windows);

public sealed record OpenCodeGoWindowEstimate(string Key, string Label, decimal ObservedUsd,
    decimal? LimitUsd, double? EstimatedUsedPercent, int Records, int UnpricedRecords);
