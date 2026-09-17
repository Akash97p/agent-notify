namespace AgentNotify.Core.Usage;

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

/// <summary>A subscription month that renews on a fixed day of the month at local midnight.</summary>
public static class OpenCodeGoBillingCycle
{
    public static bool IsValidRenewalDay(int? day) => day is >= 1 and <= 31;

    /// <summary>
    /// The billing period containing <paramref name="now"/>. A renewal day past the end of a short
    /// month falls on its last day, so a plan renewing on the 31st renews on 28 or 29 February.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Current(DateTimeOffset now, int renewalDay, TimeZoneInfo zone)
    {
        if (!IsValidRenewalDay(renewalDay)) throw new ArgumentOutOfRangeException(nameof(renewalDay));
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = Renewal(local.Year, local.Month, renewalDay, zone);
        if (start > now)
        {
            var previous = new DateTime(local.Year, local.Month, 1).AddMonths(-1);
            start = Renewal(previous.Year, previous.Month, renewalDay, zone);
        }
        var startLocal = TimeZoneInfo.ConvertTime(start, zone);
        var following = new DateTime(startLocal.Year, startLocal.Month, 1).AddMonths(1);
        return (start, Renewal(following.Year, following.Month, renewalDay, zone));
    }

    private static DateTimeOffset Renewal(int year, int month, int renewalDay, TimeZoneInfo zone)
    {
        var midnight = new DateTime(year, month, Math.Min(renewalDay, DateTime.DaysInMonth(year, month)));
        // A skipped local midnight (a daylight-saving change) uses the first valid time after it.
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(30);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }
}
