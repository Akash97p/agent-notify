using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotify.Core;
using AgentNotify.Core.Services;
using AgentNotify.Protocol;

namespace AgentNotify.Api;

internal enum ArcOperation
{
    Create,
    Update,
    Resolve,
    Respond
}

internal sealed record ArcMapping(
    ArcOperation Operation,
    string LocalKey,
    CreateNotificationRequest? Request,
    bool UsesEventIdentityKey,
    CreateInteractionRequest? Interaction = null,
    RespondInteractionRequest? Response = null);

/// <summary>Projects ARC 0.2 attention-request events into AgentNotify's local lifecycle.</summary>
internal static partial class ArcEventMapper
{
    public const string SupportedVersion = "0.2";
    public const string AgentNotifyExtensionName = "x-agentnotify";

    public static bool TryMap(ArcEvent? source, out ArcMapping? mapping, out string? error)
    {
        mapping = null;
        error = ValidateEnvelope(source);
        if (error is not null)
            return false;

        var value = source!;
        if (!TryReadAgentNotifyExtension(value.Extensions, out var extension, out error))
            return false;

        var operation = value.EventType switch
        {
            ArcEventTypes.RequestUpdated => ArcOperation.Update,
            ArcEventTypes.RequestResolved => ArcOperation.Resolve,
            ArcEventTypes.ResponseSubmitted => ArcOperation.Respond,
            _ => ArcOperation.Create
        };
        var usesEventIdentityKey = string.IsNullOrWhiteSpace(value.Request!.Key);
        var localKey = usesEventIdentityKey
            ? StableEventKey(value.Sender!.Id, value.EventId)
            : value.Request.Key!.Trim();

        if (operation == ArcOperation.Resolve)
        {
            mapping = new ArcMapping(operation, localKey, null, false);
            error = null;
            return true;
        }

        if (operation == ArcOperation.Respond)
        {
            var answer = value.Response!;
            mapping = new ArcMapping(operation, localKey, null, false, Response: new RespondInteractionRequest
            {
                ResponseId = answer.ResponseId!.Trim(),
                RequestDigest = answer.Digest!.Trim(),
                Nonce = answer.Nonce?.Trim(),
                ChoiceId = answer.ChoiceId?.Trim(),
                Text = answer.Text,
                Source = answer.Source?.Trim(),
                DeviceId = answer.DeviceId?.Trim()
            });
            error = null;
            return true;
        }

        var kind = value.Request.Kind!;
        var (notificationType, defaultPriority, defaultTitle) = DefaultsFor(kind, value.Sender!);
        if (!string.IsNullOrWhiteSpace(extension?.NotificationType))
            notificationType = NotificationTypes.Normalize(extension.NotificationType) ?? "";

        mapping = new ArcMapping(
            operation,
            localKey,
            new CreateNotificationRequest
            {
                Key = localKey,
                Agent = value.Sender!.Id,
                AgentInstance = value.Sender.InstanceId ?? value.Context?.SessionId,
                Project = value.Context?.Project ?? ProjectName(value.Context?.Cwd),
                Type = notificationType,
                Priority = value.Request.Priority ?? defaultPriority,
                Title = string.IsNullOrWhiteSpace(value.Request.Title) ? defaultTitle : value.Request.Title,
                Message = value.Request.Message!,
                Cwd = value.Context?.Cwd,
                Pid = value.Context?.Pid,
                Metadata = CreateMetadata(value)
            },
            usesEventIdentityKey,
            Interaction: BuildInteraction(value, localKey));
        error = null;
        return true;
    }

    /// <summary>
    /// An answerable request projects into a durable interaction alongside its notification.
    /// They share the ARC condition key, so the pair is findable from either side.
    /// </summary>
    private static CreateInteractionRequest? BuildInteraction(ArcEvent value, string localKey)
    {
        var spec = value.Request!.Response;
        if (spec is null)
            return null;

        var ttl = (int)Math.Round((spec.ExpiresAt!.Value - DateTimeOffset.UtcNow).TotalSeconds);
        return new CreateInteractionRequest
        {
            Key = localKey,
            Agent = value.Sender!.Id,
            AgentInstance = value.Sender.InstanceId ?? value.Context?.SessionId,
            Project = value.Context?.Project ?? ProjectName(value.Context?.Cwd),
            SessionId = value.Context?.SessionId,
            TurnId = value.Context?.TurnId,
            NativeRequestId = value.Context?.NativeRequestId,
            Kind = spec.Kind switch
            {
                ArcResponseKinds.SingleChoice => InteractionKind.SingleChoice,
                ArcResponseKinds.Text => InteractionKind.Text,
                _ => InteractionKind.Permission
            },
            Prompt = value.Request.Message!,
            Choices = spec.Choices?
                .Select(choice => new InteractionChoice
                {
                    Id = choice.Id!.Trim(),
                    Label = choice.Label!.Trim(),
                    Detail = choice.Detail
                })
                .ToList(),
            TextMaxLength = spec.TextMaxLength,
            // The producer states an absolute deadline; the broker stores a TTL. Clamping is
            // the service's job, so a deadline already past becomes the shortest legal wait
            // rather than a rejection of an otherwise valid event.
            TtlSeconds = Math.Clamp(ttl, InteractionService.MinTtlSeconds, InteractionService.MaxTtlSeconds)
        };
    }

    private static string? ValidateEnvelope(ArcEvent? source)
    {
        if (source is null)
            return "body is required";
        if (source.UnknownFields is { Count: > 0 })
            return "event contains unknown fields";
        if (!string.Equals(source.ArcVersion, SupportedVersion, StringComparison.Ordinal))
            return $"arc_version must be {SupportedVersion}";
        if (string.IsNullOrWhiteSpace(source.EventId) || source.EventId.Length > 512)
            return "event_id is required and must be at most 512 characters";
        if (source.EventType is not (ArcEventTypes.RequestCreated or ArcEventTypes.RequestUpdated or
            ArcEventTypes.RequestResolved or ArcEventTypes.ResponseSubmitted))
            return "event_type must be request.created, request.updated, request.resolved, or response.submitted";
        if (source.OccurredAt is null)
            return "occurred_at is required and must be an RFC 3339 timestamp";
        if (source.Sender is null || string.IsNullOrWhiteSpace(source.Sender.Id))
            return "sender.id is required";
        if (source.Sender.UnknownFields is { Count: > 0 })
            return "sender contains unknown fields";
        if (source.Sender.Id.Length > 100)
            return "sender.id must be at most 100 characters";
        if (source.Sender.Name is { Length: > 160 })
            return "sender.name must be at most 160 characters";
        if (source.Sender.InstanceId is { Length: > 100 })
            return "sender.instance_id must be at most 100 characters";
        if (source.Context?.UnknownFields is { Count: > 0 })
            return "context contains unknown fields";
        if (source.Context?.SessionId is { Length: > 100 })
            return "context.session_id must be at most 100 characters";
        if (source.Context?.CorrelationId is { Length: > 200 })
            return "context.correlation_id must be at most 200 characters";
        if (source.Context?.Project is { Length: > 200 })
            return "context.project must be at most 200 characters";
        if (source.Context?.Cwd is { Length: > 1024 })
            return "context.cwd must be at most 1024 characters";
        if (source.Context?.Pid is < 0)
            return "context.pid must be a non-negative integer";
        if (source.Context?.TurnId is { Length: > 200 })
            return "context.turn_id must be at most 200 characters";
        if (source.Context?.NativeRequestId is { Length: > 200 })
            return "context.native_request_id must be at most 200 characters";
        if (source.Request is null)
            return "request is required";
        if (source.Request.UnknownFields is { Count: > 0 })
            return "request contains unknown fields";
        if (source.Request.Key is not null &&
            (string.IsNullOrWhiteSpace(source.Request.Key) || source.Request.Key.Length > 100))
            return "request.key must be non-empty and at most 100 characters";
        if (source.EventType != ArcEventTypes.RequestCreated && string.IsNullOrWhiteSpace(source.Request.Key))
            return "request.key is required for request.updated, request.resolved, and response.submitted";

        if (source.EventType == ArcEventTypes.ResponseSubmitted)
        {
            if (source.Request.Kind is not null || source.Request.Title is not null ||
                source.Request.Message is not null || source.Request.Priority is not null ||
                source.Request.Response is not null || source.Request.Outcome is not null)
                return "response.submitted may only contain request.key";
            return ValidateResponse(source.Response) ?? ValidateExtensions(source.Extensions);
        }

        if (source.Response is not null)
            return "response is only allowed on response.submitted";

        if (source.EventType == ArcEventTypes.RequestResolved)
        {
            if (source.Request.Kind is not null || source.Request.Title is not null ||
                source.Request.Message is not null || source.Request.Priority is not null ||
                source.Request.Response is not null)
                return "request.resolved may only contain request.key and request.outcome";
            if (source.Request.Outcome is not null && !ArcOutcomes.All.Contains(source.Request.Outcome, StringComparer.Ordinal))
                return "request.outcome must be answered, expired, cancelled, superseded, or not_needed";
            return ValidateExtensions(source.Extensions);
        }

        if (source.Request.Outcome is not null)
            return "request.outcome is only allowed on request.resolved";

        if (!ArcRequestKinds.All.Contains(source.Request.Kind, StringComparer.Ordinal))
            return "request.kind must be information, question, permission, blocked, failure, or completion";
        if (source.Request.Title is { Length: > 200 })
            return "request.title must be at most 200 characters";
        if (string.IsNullOrWhiteSpace(source.Request.Message) || source.Request.Message.Length > 4000)
            return "request.message is required and must be at most 4000 characters";
        return ValidateResponseSpec(source.Request.Response) ?? ValidateExtensions(source.Extensions);
    }

    /// <summary>
    /// Validates only what ARC itself bounds. Choice and text rules that the broker already
    /// enforces on every interaction are left to <see cref="InteractionService"/> rather than
    /// duplicated here, so the two cannot drift apart.
    /// </summary>
    private static string? ValidateResponseSpec(ArcResponseSpec? spec)
    {
        if (spec is null)
            return null;
        if (spec.UnknownFields is { Count: > 0 })
            return "request.response contains unknown fields";
        if (!ArcResponseKinds.All.Contains(spec.Kind, StringComparer.Ordinal))
            return "request.response.kind must be permission, single_choice, or text";
        if (spec.ExpiresAt is null)
            return "request.response.expires_at is required and must be an RFC 3339 timestamp";
        if (spec.Choices is { Count: > 0 } &&
            spec.Choices.Any(choice => choice is null || choice.UnknownFields is { Count: > 0 }))
            return "request.response.choices contains unknown fields";
        if (spec.Choices is { Count: > 0 } &&
            spec.Choices.Any(choice => string.IsNullOrWhiteSpace(choice.Id) || string.IsNullOrWhiteSpace(choice.Label)))
            return "each request.response choice needs an id and a label";
        return null;
    }

    private static string? ValidateResponse(ArcResponse? response)
    {
        if (response is null)
            return "response is required for response.submitted";
        if (response.UnknownFields is { Count: > 0 })
            return "response contains unknown fields";
        if (string.IsNullOrWhiteSpace(response.ResponseId) || response.ResponseId.Length > 128)
            return "response.response_id is required and must be at most 128 characters";
        if (string.IsNullOrWhiteSpace(response.Digest))
            return "response.digest is required";
        var hasChoice = !string.IsNullOrWhiteSpace(response.ChoiceId);
        var hasText = !string.IsNullOrWhiteSpace(response.Text);
        if (hasChoice == hasText)
            return "response must carry exactly one of choice_id or text";
        return null;
    }

    private static string? ValidateExtensions(Dictionary<string, JsonElement>? extensions)
    {
        if (extensions is null)
            return null;
        return extensions.Keys.Any(key => !ExtensionNameRegex().IsMatch(key))
            ? "extension names must use the x-<vendor> form"
            : null;
    }

    private static bool TryReadAgentNotifyExtension(
        Dictionary<string, JsonElement>? extensions,
        out AgentNotifyArcExtension? extension,
        out string? error)
    {
        extension = null;
        error = null;
        if (extensions is null || !extensions.TryGetValue(AgentNotifyExtensionName, out var element))
            return true;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"extensions.{AgentNotifyExtensionName} must be an object";
            return false;
        }

        try
        {
            extension = element.Deserialize<AgentNotifyArcExtension>(Json.Options);
            if (extension is null)
                error = $"extensions.{AgentNotifyExtensionName} must be an object";
            else if (extension.UnknownFields is { Count: > 0 })
                error = $"extensions.{AgentNotifyExtensionName} contains unknown fields";
        }
        catch (JsonException)
        {
            error = $"extensions.{AgentNotifyExtensionName} is invalid";
        }
        return error is null;
    }

    private static (string Type, NotificationPriority Priority, string Title) DefaultsFor(
        string kind,
        ArcSender sender)
    {
        var displayName = !string.IsNullOrWhiteSpace(sender.Name)
            ? sender.Name.Trim()
            : sender.Id.Trim();
        return kind switch
        {
            ArcRequestKinds.Question => (NotificationTypes.InputRequired, NotificationPriority.High, $"{displayName} needs input"),
            ArcRequestKinds.Permission => (NotificationTypes.PermissionRequired, NotificationPriority.High, $"{displayName} needs permission"),
            ArcRequestKinds.Blocked => (NotificationTypes.Blocked, NotificationPriority.High, $"{displayName} is blocked"),
            ArcRequestKinds.Failure => (NotificationTypes.Error, NotificationPriority.High, $"{displayName} reported a failure"),
            ArcRequestKinds.Completion => (NotificationTypes.Completed, NotificationPriority.Normal, $"{displayName} completed work"),
            _ => (NotificationTypes.Info, NotificationPriority.Normal, $"Update from {displayName}")
        };
    }

    private static string StableEventKey(string senderId, string eventId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{senderId}\n{eventId}"));
        return $"arc:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static string? ProjectName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var name = SafeFileName.Last(path.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static Dictionary<string, JsonElement> CreateMetadata(ArcEvent source)
    {
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["arcVersion"] = JsonSerializer.SerializeToElement(source.ArcVersion),
            ["arcEventId"] = JsonSerializer.SerializeToElement(source.EventId),
            ["arcEventType"] = JsonSerializer.SerializeToElement(source.EventType),
            ["arcOccurredAt"] = JsonSerializer.SerializeToElement(source.OccurredAt),
            ["arcRequestKind"] = JsonSerializer.SerializeToElement(source.Request!.Kind)
        };
        if (!string.IsNullOrWhiteSpace(source.Context?.SessionId))
            metadata["arcSessionId"] = JsonSerializer.SerializeToElement(source.Context.SessionId);
        if (!string.IsNullOrWhiteSpace(source.Context?.CorrelationId))
            metadata["arcCorrelationId"] = JsonSerializer.SerializeToElement(source.Context.CorrelationId);
        return metadata;
    }

    [GeneratedRegex("^x-[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionNameRegex();
}
