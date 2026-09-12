using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentNotify.Protocol;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Persistence;

namespace AgentNotify.Core.Services;

/// <summary>
/// Application logic for waiting questions/permissions. The broker is the source
/// of truth: one durable interaction, at most one accepted response
/// (first valid wins), expiry, cancellation, and digest-bound answers.
/// </summary>
public sealed class InteractionService
{
    public const int DefaultTtlSeconds = 600;
    public const int MinTtlSeconds = 30;
    public const int MaxTtlSeconds = 3600;
    public const int DefaultTextMaxLength = 500;
    public const int MaxTextLength = 2000;
    public const int MaxPromptLength = 2000;
    public const int MaxChoices = 12;
    public const int MinChoices = 2;

    private static readonly Regex ChoiceIdPattern = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex SourcePattern = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);

    private readonly IInteractionRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource<Interaction>>> _waiters = new();

    public InteractionService(IInteractionRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Opens an interaction, or returns the still-pending one for a repeated key.</summary>
    public async Task<ServiceResult<Interaction>> RequestAsync(CreateInteractionRequest request, CancellationToken ct = default)
    {
        var error = ValidateRequest(request);
        if (error is not null)
            return ServiceResult<Interaction>.Fail(error);

        var now = DateTimeOffset.UtcNow;
        var normalized = Normalize(request, now);

        await _gate.WaitAsync(ct);
        try
        {
            if (normalized.Key is not null)
            {
                var existing = await _repository.FindPendingByKeyAsync(normalized.Key, ct);
                // A repeated key reuses the live waiter only when it asks the same
                // thing: a changed prompt/digest supersedes instead of merging.
                if (existing is not null)
                {
                    if (string.Equals(existing.RequestDigest, normalized.RequestDigest, StringComparison.Ordinal))
                        return ServiceResult<Interaction>.Ok(existing);
                    existing.Status = InteractionStatus.Superseded;
                    existing.UpdatedAt = now;
                    await _repository.UpdateAsync(existing, ct);
                    SignalWaiters(existing);
                }
            }

            var created = await _repository.CreateAsync(normalized, ct);
            return ServiceResult<Interaction>.Ok(created, wasCreated: true);
        }
        finally { _gate.Release(); }
    }

    public async Task<ServiceResult<Interaction>> GetAsync(string id, CancellationToken ct = default)
    {
        await SweepAsync(ct);
        var item = await _repository.GetByIdAsync(id, ct);
        return item is null ? ServiceResult<Interaction>.NotExist() : ServiceResult<Interaction>.Ok(item);
    }

    public async Task<IReadOnlyList<Interaction>> ListAsync(InteractionQuery query, CancellationToken ct = default)
    {
        await SweepAsync(ct);
        return await _repository.QueryAsync(query, ct);
    }

    /// <summary>
    /// Answers a pending interaction. The first valid response wins; a retried
    /// response id returns the original outcome; anything later is rejected.
    /// </summary>
    public async Task<ServiceResult<Interaction>> RespondAsync(string id, RespondInteractionRequest request, CancellationToken ct = default)
    {
        var error = ValidateResponseShape(request);
        if (error is not null)
            return ServiceResult<Interaction>.Fail(error);

        await _gate.WaitAsync(ct);
        try
        {
            var item = await _repository.GetByIdAsync(id, ct);
            if (item is null)
                return ServiceResult<Interaction>.NotExist();
            item = await ExpireIfDueAsync(item, ct);

            if (!string.Equals(item.RequestDigest, request.RequestDigest.Trim(), StringComparison.Ordinal))
                return ServiceResult<Interaction>.Fail("request digest mismatch: the question changed since it was asked");

            if (!string.IsNullOrWhiteSpace(item.Nonce) &&
                (string.IsNullOrWhiteSpace(request.Nonce) ||
                 !CryptographicOperations.FixedTimeEquals(
                     Encoding.UTF8.GetBytes(item.Nonce),
                     Encoding.UTF8.GetBytes(request.Nonce.Trim()))))
                return ServiceResult<Interaction>.Fail("nonce mismatch");

            // Idempotent retries still prove that they belong to this request before
            // the original outcome is returned.
            if (item.Response is not null &&
                string.Equals(item.Response.ResponseId, request.ResponseId.Trim(), StringComparison.Ordinal))
                return ServiceResult<Interaction>.Ok(item);

            if (item.Status != InteractionStatus.Pending)
                return ServiceResult<Interaction>.Fail(
                    item.Status == InteractionStatus.Answered
                        ? "interaction already answered"
                        : $"interaction is no longer pending ({item.Status.ToString().ToLowerInvariant()})");

            var answerError = ValidateAnswer(item, request);
            if (answerError is not null)
                return ServiceResult<Interaction>.Fail(answerError);

            var now = DateTimeOffset.UtcNow;
            item.Response = new InteractionResponse
            {
                ResponseId = request.ResponseId.Trim(),
                InteractionId = item.Id,
                ChoiceId = string.IsNullOrWhiteSpace(request.ChoiceId) ? null : request.ChoiceId.Trim(),
                Text = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim(),
                Source = string.IsNullOrWhiteSpace(request.Source) ? "unknown" : request.Source.Trim(),
                DeviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId.Trim(),
                CreatedAt = now
            };
            item.Status = InteractionStatus.Answered;
            item.AnsweredAt = now;
            item.UpdatedAt = now;

            var updated = await _repository.UpdateAsync(item, ct);
            if (updated is null)
                return ServiceResult<Interaction>.NotExist();
            SignalWaiters(updated);
            return ServiceResult<Interaction>.Ok(updated);
        }
        finally { _gate.Release(); }
    }

    public async Task<ServiceResult<Interaction>> CancelAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var item = await _repository.GetByIdAsync(id, ct);
            if (item is null)
                return ServiceResult<Interaction>.NotExist();
            if (item.Status != InteractionStatus.Pending)
                return ServiceResult<Interaction>.Ok(item);

            item.Status = InteractionStatus.Cancelled;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            var updated = await _repository.UpdateAsync(item, ct);
            if (updated is null)
                return ServiceResult<Interaction>.NotExist();
            SignalWaiters(updated);
            return ServiceResult<Interaction>.Ok(updated);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Returns the interaction now when settled, otherwise waits for an answer,
    /// cancellation, expiry, or the timeout — whichever comes first.
    /// </summary>
    public async Task<Interaction?> WaitAsync(string id, TimeSpan timeout, CancellationToken ct = default)
    {
        var current = await GetAsync(id, ct);
        if (current.Value is null || current.Value.Status != InteractionStatus.Pending)
            return current.Value;

        var waiter = new TaskCompletionSource<Interaction>(TaskCreationOptions.RunContinuationsAsynchronously);
        var list = _waiters.GetOrAdd(id, _ => []);
        lock (list) { list.Add(waiter); }
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await using var _ = linked.Token.Register(() => waiter.TrySetCanceled());
            try
            {
                return await waiter.Task;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                var latest = await _repository.GetByIdAsync(id, ct);
                return latest;
            }
        }
        finally
        {
            lock (list) { list.Remove(waiter); }
        }
    }

    private void SignalWaiters(Interaction item)
    {
        if (_waiters.TryGetValue(item.Id, out var list))
        {
            List<TaskCompletionSource<Interaction>> snapshot;
            lock (list) { snapshot = [.. list]; }
            foreach (var waiter in snapshot)
                waiter.TrySetResult(item);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var expired = await _repository.ExpireDueAsync(DateTimeOffset.UtcNow, ct);
        _ = expired;
    }

    private async Task<Interaction> ExpireIfDueAsync(Interaction item, CancellationToken ct)
    {
        if (item.Status == InteractionStatus.Pending && item.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            item.Status = InteractionStatus.Expired;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            var updated = await _repository.UpdateAsync(item, ct);
            if (updated is not null)
            {
                SignalWaiters(updated);
                return updated;
            }
        }
        return item;
    }

    private static Interaction Normalize(CreateInteractionRequest request, DateTimeOffset now)
    {
        var ttl = Math.Clamp(request.TtlSeconds ?? DefaultTtlSeconds, MinTtlSeconds, MaxTtlSeconds);
        var choices = (request.Choices ?? [])
            .Select(c => new InteractionChoice
            {
                Id = c.Id.Trim(),
                Label = c.Label.Trim(),
                Detail = string.IsNullOrWhiteSpace(c.Detail) ? null : c.Detail.Trim()
            })
            .ToList();

        var item = new Interaction
        {
            Key = string.IsNullOrWhiteSpace(request.Key) ? null : request.Key.Trim(),
            Agent = string.IsNullOrWhiteSpace(request.Agent) ? "unknown" : request.Agent.Trim(),
            AgentInstance = string.IsNullOrWhiteSpace(request.AgentInstance) ? null : request.AgentInstance.Trim(),
            Project = string.IsNullOrWhiteSpace(request.Project) ? null : request.Project.Trim(),
            SessionId = string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId.Trim(),
            TurnId = string.IsNullOrWhiteSpace(request.TurnId) ? null : request.TurnId.Trim(),
            NativeRequestId = string.IsNullOrWhiteSpace(request.NativeRequestId) ? null : request.NativeRequestId.Trim(),
            Kind = request.Kind,
            Prompt = request.Prompt.Trim(),
            Choices = choices,
            TextMaxLength = Math.Clamp(request.TextMaxLength ?? DefaultTextMaxLength, 1, MaxTextLength),
            Status = InteractionStatus.Pending,
            Nonce = NewNonce(),
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddSeconds(ttl)
        };
        item.RequestDigest = ComputeDigest(item);
        return item;
    }

    /// <summary>SHA-256 hex over the canonical request. Any displayed change => new digest.</summary>
    public static string ComputeDigest(Interaction item)
    {
        var canonical = new StringBuilder("v1|");
        canonical.Append(item.Kind).Append('|');
        canonical.Append(item.Agent).Append('|');
        canonical.Append(item.SessionId ?? "").Append('|');
        canonical.Append(item.Prompt).Append('|');
        canonical.Append(string.Join(",", item.Choices.Select(c => c.Id))).Append('|');
        canonical.Append(item.TextMaxLength);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static string NewNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string? ValidateRequest(CreateInteractionRequest request)
    {
        if (request.Key is { Length: > 100 }) return "key must be at most 100 characters";
        if (request.Agent is { Length: > 100 }) return "agent must be at most 100 characters";
        if (request.AgentInstance is { Length: > 100 }) return "agent_instance must be at most 100 characters";
        if (request.Project is { Length: > 200 }) return "project must be at most 200 characters";
        if (request.SessionId is { Length: > 200 }) return "session_id must be at most 200 characters";
        if (request.TurnId is { Length: > 200 }) return "turn_id must be at most 200 characters";
        if (request.NativeRequestId is { Length: > 200 }) return "native_request_id must be at most 200 characters";
        if (string.IsNullOrWhiteSpace(request.Prompt)) return "prompt is required";
        if (request.Prompt.Trim().Length > MaxPromptLength) return $"prompt must be at most {MaxPromptLength} characters";
        if (!Enum.IsDefined(request.Kind)) return "kind must be permission, single_choice, or text";

        if (request.Kind == InteractionKind.Text)
        {
            if (request.Choices is { Count: > 0 }) return "choices are not allowed for the text kind";
        }
        else
        {
            var count = request.Choices?.Count ?? 0;
            if (count < MinChoices || count > MaxChoices)
                return $"choices must contain {MinChoices} to {MaxChoices} entries for this kind";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var choice in request.Choices!)
            {
                if (choice is null) return "choice must be an object";
                if (!ChoiceIdPattern.IsMatch(choice.Id.Trim())) return "choice id must be 1-64 letters, digits, underscores, or hyphens";
                if (string.IsNullOrWhiteSpace(choice.Label) || choice.Label.Trim().Length > 200)
                    return "choice label must be 1-200 characters";
                if (choice.Detail is { Length: > 500 } && choice.Detail.Trim().Length > 500)
                    return "choice detail must be at most 500 characters";
                if (!seen.Add(choice.Id.Trim())) return "choice ids must be unique";
            }
        }
        return null;
    }

    private static string? ValidateResponseShape(RespondInteractionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ResponseId) || request.ResponseId.Trim().Length > 128)
            return "response_id is required (at most 128 characters)";
        if (string.IsNullOrWhiteSpace(request.RequestDigest))
            return "request_digest is required";
        if (request.ChoiceId is { Length: > 64 }) return "choice_id must be at most 64 characters";
        if (request.Text is { Length: > MaxTextLength } && request.Text.Trim().Length > MaxTextLength)
            return $"text must be at most {MaxTextLength} characters";
        if (request.Source is not null && !SourcePattern.IsMatch(request.Source.Trim()))
            return "source must be 1-64 letters, digits, underscores, or hyphens";
        if (request.DeviceId is { Length: > 128 }) return "device_id must be at most 128 characters";
        if (request.Nonce is { Length: > 128 }) return "nonce must be at most 128 characters";
        return null;
    }

    private static string? ValidateAnswer(Interaction item, RespondInteractionRequest request)
    {
        if (item.Kind == InteractionKind.Text)
        {
            if (string.IsNullOrWhiteSpace(request.Text)) return "text is required for this interaction";
            if (request.Text.Trim().Length > item.TextMaxLength)
                return $"text must be at most {item.TextMaxLength} characters";
            if (!string.IsNullOrWhiteSpace(request.ChoiceId)) return "choice_id is not allowed for the text kind";
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.ChoiceId)) return "choice_id is required for this interaction";
        if (!string.IsNullOrWhiteSpace(request.Text)) return "text is not allowed for this kind";
        var allowed = item.Choices.Any(c => string.Equals(c.Id, request.ChoiceId.Trim(), StringComparison.Ordinal));
        return allowed ? null : "choice_id is not one of the offered choices";
    }
}
