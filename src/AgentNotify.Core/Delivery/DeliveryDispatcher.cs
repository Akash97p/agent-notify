using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotify.Core.Logging;

namespace AgentNotify.Core.Delivery;

/// <summary>
/// Single-worker durable outbox dispatcher. Calls are time-bounded and failures become
/// sanitized attempt rows with bounded retry or dead-letter state.
/// </summary>
public sealed partial class DeliveryDispatcher : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly IDeliveryRepository _repository;
    private readonly ProviderProfileService _profiles;
    private readonly IReadOnlyDictionary<string, IOutboundChannelAdapter> _adapters;
    private readonly IAppLogger? _logger;
    private readonly TimeSpan _deliveryTimeout;
    private readonly Func<double> _retryJitter;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _runTask;

    public DeliveryDispatcher(
        IDeliveryRepository repository,
        ProviderProfileService profiles,
        IEnumerable<IOutboundChannelAdapter> adapters,
        IAppLogger? logger = null,
        TimeSpan? deliveryTimeout = null,
        Func<double>? retryJitter = null)
    {
        _repository = repository;
        _profiles = profiles;
        _logger = logger;
        _deliveryTimeout = deliveryTimeout ?? TimeSpan.FromSeconds(15);
        if (_deliveryTimeout <= TimeSpan.Zero || _deliveryTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(deliveryTimeout));

        _retryJitter = retryJitter ?? (() => Random.Shared.NextDouble() * 0.4 - 0.2);
        _adapters = adapters
            .GroupBy(adapter => NormalizeKind(adapter.Kind), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    }

    public IReadOnlyList<string> RegisteredAdapters => _adapters.Keys.Order().ToArray();

    public void Start()
    {
        if (_runTask is not null)
            throw new InvalidOperationException("Delivery dispatcher is already running.");
        _runTask = Task.Run(() => RunAsync(_stop.Token));
    }

    public void Signal()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    public async Task StopAsync()
    {
        if (_runTask is null)
            return;
        _stop.Cancel();
        Signal();
        try
        {
            await _runTask;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    public async Task<bool> ProcessOneAsync(CancellationToken ct = default)
    {
        var item = await _repository.ClaimDueAsync(DateTimeOffset.UtcNow, ct);
        if (item is null)
            return false;

        await DeliverClaimedAsync(item, ct);
        return true;
    }

    public async Task<DeliveryDiagnosticSnapshot> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        var counts = await _repository.CountOutboxByStatusAsync(ct);
        return new DeliveryDiagnosticSnapshot(
            counts[OutboxStatus.Pending],
            counts[OutboxStatus.Processing],
            counts[OutboxStatus.Retry],
            counts[OutboxStatus.Delivered],
            counts[OutboxStatus.DeadLetter],
            RegisteredAdapters);
    }

    public async Task<DeliveryResult> TestProviderAsync(
        string profileId,
        string? payloadJson = null,
        CancellationToken ct = default)
    {
        var payload = string.IsNullOrWhiteSpace(payloadJson)
            ? "{\"title\":\"AgentNotify test\",\"message\":\"Your outbound channel is configured.\"}"
            : payloadJson.Trim();
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > 64 * 1024)
            throw new ArgumentException("Test payload is too large.", nameof(payloadJson));
        using (var document = JsonDocument.Parse(payload))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Test payload must be a JSON object.", nameof(payloadJson));
        }

        DeliveryProvider? provider;
        try
        {
            provider = await _profiles.GetForDeliveryAsync(profileId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CryptographicException)
        {
            return DeliveryResult.PermanentFailure("provider_secrets_unreadable");
        }
        catch (JsonException)
        {
            return DeliveryResult.PermanentFailure("provider_secrets_unreadable");
        }
        catch (Exception)
        {
            return DeliveryResult.Retry("provider_load_failed");
        }

        if (provider is null)
            return DeliveryResult.PermanentFailure("provider_missing");
        if (!_adapters.TryGetValue(provider.Profile.Kind, out var adapter))
            return DeliveryResult.PermanentFailure("adapter_unavailable");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_deliveryTimeout);
        try
        {
            var result = await adapter.DeliverAsync(
                new OutboundDelivery(
                    $"test-{Guid.NewGuid():N}",
                    "test",
                    payload,
                    provider.Profile,
                    provider.Secrets),
                timeout.Token);
            return result with { ErrorCode = SanitizeErrorCode(result.ErrorCode) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DeliveryResult.Retry("timeout");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DeliveryResult.Retry("adapter_exception");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
        _wake.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var recovered = await _repository.RecoverInterruptedAsync(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ct);
        if (recovered > 0)
            _logger?.Warn($"Recovered {recovered} interrupted outbound delivery item(s).");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                while (await ProcessOneAsync(ct))
                {
                }

                await _wake.WaitAsync(PollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Keep the worker alive without risking secret-bearing exception text in logs.
                _logger?.Warn("Outbound dispatcher recovered from an internal failure.");
                var now = DateTimeOffset.UtcNow;
                await _repository.RecoverInterruptedAsync(now, now, ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private async Task DeliverClaimedAsync(OutboxItem item, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        DeliveryResult? result = null;
        DeliveryProvider? provider = null;
        try
        {
            provider = await _profiles.GetForDeliveryAsync(item.ProviderId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CryptographicException)
        {
            result = DeliveryResult.PermanentFailure("provider_secrets_unreadable");
        }
        catch (JsonException)
        {
            result = DeliveryResult.PermanentFailure("provider_secrets_unreadable");
        }
        catch (Exception)
        {
            result = DeliveryResult.Retry("provider_load_failed");
        }

        if (result is null)
        {
            if (provider is null)
            {
                result = DeliveryResult.PermanentFailure("provider_missing");
            }
            else if (!provider.Profile.Enabled)
            {
                result = DeliveryResult.PermanentFailure("provider_disabled");
            }
            else if (!_adapters.TryGetValue(provider.Profile.Kind, out var adapter))
            {
                result = DeliveryResult.PermanentFailure("adapter_unavailable");
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_deliveryTimeout);
                try
                {
                    result = await adapter.DeliverAsync(
                        new OutboundDelivery(
                            item.Id,
                            item.NotificationId,
                            item.PayloadJson,
                            provider.Profile,
                            provider.Secrets),
                        timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = DeliveryResult.Retry("timeout");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Exception text may embed URLs, response bodies, or credentials.
                    result = DeliveryResult.Retry("adapter_exception");
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
        item.AttemptCount++;
        item.UpdatedAt = now;
        if (result.Succeeded)
        {
            item.Status = OutboxStatus.Delivered;
        }
        else if (result.Retryable && item.AttemptCount < RetrySchedule.MaximumAttempts)
        {
            item.Status = OutboxStatus.Retry;
            item.NextAttemptAt = now + RetrySchedule.DelayFor(item.AttemptCount, _retryJitter());
        }
        else
        {
            item.Status = OutboxStatus.DeadLetter;
        }

        var errorCode = SanitizeErrorCode(result.ErrorCode);
        await _repository.CompleteAttemptAsync(item, new DeliveryAttempt
        {
            OutboxId = item.Id,
            AttemptNumber = item.AttemptCount,
            Succeeded = result.Succeeded,
            StatusCode = result.StatusCode,
            ErrorCode = errorCode,
            StartedAt = startedAt,
            CompletedAt = now
        }, ct);

        if (!result.Succeeded)
            _logger?.Warn(
                $"Outbound delivery {item.Id} via provider {item.ProviderId} " +
                $"ended as {item.Status} ({errorCode ?? "unspecified"}).");
    }

    private static string NormalizeKind(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Adapter kind is required.", nameof(kind));
        var normalized = kind.Trim().ToLowerInvariant().Replace('-', '_');
        if (!SafeIdentifier().IsMatch(normalized))
            throw new ArgumentException("Adapter kind is invalid.", nameof(kind));
        return normalized;
    }

    private static string? SanitizeErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            return null;
        var normalized = errorCode.Trim().ToLowerInvariant().Replace('-', '_');
        return SafeIdentifier().IsMatch(normalized) ? normalized : "adapter_error";
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
