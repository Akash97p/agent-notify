namespace AgentNotify.Core.Delivery;

/// <summary>A single bounded call to an outbound provider adapter.</summary>
public sealed record OutboundDelivery(
    string OutboxId,
    string NotificationId,
    string PayloadJson,
    ProviderProfile Profile,
    IReadOnlyDictionary<string, string> Secrets);

/// <summary>
/// Sanitized adapter outcome. ErrorCode is a stable machine-readable category, never a
/// provider response body, URL, credential, or exception message.
/// </summary>
public sealed record DeliveryResult(
    bool Succeeded,
    bool Retryable,
    int? StatusCode = null,
    string? ErrorCode = null)
{
    public static DeliveryResult Success(int? statusCode = null) =>
        new(true, false, statusCode);

    public static DeliveryResult Retry(string errorCode, int? statusCode = null) =>
        new(false, true, statusCode, errorCode);

    public static DeliveryResult PermanentFailure(string errorCode, int? statusCode = null) =>
        new(false, false, statusCode, errorCode);
}

public interface IOutboundChannelAdapter
{
    string Kind { get; }

    Task<DeliveryResult> DeliverAsync(
        OutboundDelivery delivery,
        CancellationToken cancellationToken);
}
