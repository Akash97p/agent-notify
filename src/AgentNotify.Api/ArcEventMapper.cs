using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentNotify.Core;
using AgentNotify.Protocol;

namespace AgentNotify.Api;

internal enum ArcOperation
{
    Create,
    Update,
    Resolve
}

internal sealed record ArcMapping(
    ArcOperation Operation,
    string LocalKey,
    CreateNotificationRequest? Request,
    bool UsesEventIdentityKey);

/// <summary>Projects ARC 0.1 attention-request events into AgentNotify's local lifecycle.</summary>
internal static partial class ArcEventMapper
{
    public const string SupportedVersion = "0.1";
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
            usesEventIdentityKey);
        error = null;
        return true;
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
        if (source.EventType is not (ArcEventTypes.RequestCreated or ArcEventTypes.RequestUpdated or ArcEventTypes.RequestResolved))
            return "event_type must be request.created, request.updated, or request.resolved";
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
        if (source.Request is null)
            return "request is required";
        if (source.Request.UnknownFields is { Count: > 0 })
            return "request contains unknown fields";
        if (source.Request.Key is not null &&
            (string.IsNullOrWhiteSpace(source.Request.Key) || source.Request.Key.Length > 100))
            return "request.key must be non-empty and at most 100 characters";
        if (source.EventType != ArcEventTypes.RequestCreated && string.IsNullOrWhiteSpace(source.Request.Key))
            return "request.key is required for request.updated and request.resolved";

        if (source.EventType == ArcEventTypes.RequestResolved)
        {
            if (source.Request.Kind is not null || source.Request.Title is not null ||
                source.Request.Message is not null || source.Request.Priority is not null)
                return "request.resolved may only contain request.key";
            return ValidateExtensions(source.Extensions);
        }

        if (!ArcRequestKinds.All.Contains(source.Request.Kind, StringComparer.Ordinal))
            return "request.kind must be information, question, permission, blocked, failure, or completion";
        if (source.Request.Title is { Length: > 200 })
            return "request.title must be at most 200 characters";
        if (string.IsNullOrWhiteSpace(source.Request.Message) || source.Request.Message.Length > 4000)
            return "request.message is required and must be at most 4000 characters";
        return ValidateExtensions(source.Extensions);
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
