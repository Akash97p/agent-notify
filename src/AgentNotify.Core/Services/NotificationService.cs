using System.Text.Json;
using AgentNotify.Contracts;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Persistence;

namespace AgentNotify.Core.Services;

/// <summary>Result of a service operation.</summary>
public sealed class ServiceResult<T>
{
    public T? Value { get; init; }
    public string? Error { get; init; }
    public bool NotFound { get; init; }
    public bool WasCreated { get; init; }

    public static ServiceResult<T> Ok(T value, bool wasCreated = false) => new() { Value = value, WasCreated = wasCreated };
    public static ServiceResult<T> Fail(string error) => new() { Error = error };
    public static ServiceResult<T> NotExist() => new() { Error = "not found", NotFound = true };
}

/// <summary>Application logic for notification creation and lifecycle. The broker is the
/// source of truth; the CLI and UI only call into the API.</summary>
public sealed class NotificationService
{
    private readonly INotificationRepository _repository;
    private readonly SemaphoreSlim _dedupGate = new(1, 1);

    public NotificationService(INotificationRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Creates a notification, or — when a logical key is supplied and an active
    /// notification with that key already exists — updates it in place (deduplication).</summary>
    public async Task<ServiceResult<Notification>> CreateAsync(CreateNotificationRequest request, CancellationToken ct = default)
    {
        var validationError = NotificationValidator.Validate(request);
        if (validationError is not null)
            return ServiceResult<Notification>.Fail(validationError);

        var now = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(request.Key))
        {
            await _dedupGate.WaitAsync(ct);
            try
            {
                var existing = await _repository.FindActiveByKeyAsync(request.Key.Trim(), ct);
                if (existing is not null)
                {
                    ApplyToExisting(existing, request, now);
                    var updated = await _repository.UpdateAsync(existing, ct);
                    return updated is null
                        ? ServiceResult<Notification>.NotExist()
                        : ServiceResult<Notification>.Ok(updated);
                }

                var keyed = await _repository.CreateAsync(Notification.Create(request, now), ct);
                return ServiceResult<Notification>.Ok(keyed, wasCreated: true);
            }
            finally { _dedupGate.Release(); }
        }

        var created = await _repository.CreateAsync(Notification.Create(request, now), ct);
        return ServiceResult<Notification>.Ok(created, wasCreated: true);
    }

    public async Task<ServiceResult<Notification>> UpdateStatusAsync(string id, UpdateNotificationRequest request, CancellationToken ct = default)
    {
        var validationError = NotificationValidator.Validate(request);
        if (validationError is not null)
            return ServiceResult<Notification>.Fail(validationError);

        var existing = await _repository.GetByIdAsync(id, ct);
        if (existing is null)
            return ServiceResult<Notification>.NotExist();

        var status = request.Status!.Value;
        var transitionError = StatusTransitions.Validate(existing.Status, status);
        if (transitionError is not null)
            return ServiceResult<Notification>.Fail(transitionError);

        var updated = await _repository.UpdateStatusAsync(id, status, DateTimeOffset.UtcNow, ct);
        return updated is null
            ? ServiceResult<Notification>.NotExist()
            : ServiceResult<Notification>.Ok(updated);
    }

    private static void ApplyToExisting(Notification existing, CreateNotificationRequest request, DateTimeOffset now)
    {
        existing.Key = string.IsNullOrWhiteSpace(request.Key) ? null : request.Key.Trim();
        existing.Agent = string.IsNullOrWhiteSpace(request.Agent) ? "unknown" : request.Agent.Trim();
        existing.AgentInstance = string.IsNullOrWhiteSpace(request.AgentInstance) ? null : request.AgentInstance.Trim();
        existing.Project = string.IsNullOrWhiteSpace(request.Project) ? null : request.Project.Trim();
        existing.Type = request.Type;
        existing.Priority = request.Priority;
        existing.Title = request.Title.Trim();
        existing.Message = request.Message.Trim();
        existing.Cwd = string.IsNullOrWhiteSpace(request.Cwd) ? null : request.Cwd.Trim();
        existing.Pid = request.Pid;
        existing.Metadata = request.Metadata;
        existing.Status = NotificationStatus.Active;
        existing.UpdatedAt = now;
        existing.ResolvedAt = null;
    }
}
