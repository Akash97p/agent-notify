using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentNotify.Core;
using AgentNotify.Protocol;

namespace AgentNotify.Api;

/// <summary>Projects the supported AEP 0.1 attention events into AgentNotify's local model.</summary>
internal static class AepAttentionProfileMapper
{
    public const string SupportedVersion = "0.1";
    public const string ExtensionName = "x-agentnotify";

    public static bool TryMap(
        AepEvent? source,
        out CreateNotificationRequest? request,
        out bool usesEventIdentityKey,
        out string? error)
    {
        request = null;
        usesEventIdentityKey = false;
        error = ValidateEnvelope(source);
        if (error is not null)
            return false;

        var value = source!;
        var contentType = value.Type == AepEventTypes.QuestionAsked
            ? AepContentTypes.Question
            : AepContentTypes.Notification;
        var content = value.Content?.FirstOrDefault(item => item.Type == contentType);
        if (content is null || string.IsNullOrWhiteSpace(content.Text))
        {
            error = $"content must include a non-empty {contentType} entry for {value.Type}";
            return false;
        }

        if (!TryReadExtension(value.Extensions, out var extension, out error))
            return false;

        usesEventIdentityKey = string.IsNullOrWhiteSpace(extension?.Key);

        var agent = value.Agent!;
        var notificationType = value.Type == AepEventTypes.QuestionAsked
            ? NotificationTypes.InputRequired
            : NotificationTypes.Info;
        if (!string.IsNullOrWhiteSpace(extension?.NotificationType))
            notificationType = NotificationTypes.Normalize(extension.NotificationType) ?? "";

        var displayAgent = !string.IsNullOrWhiteSpace(agent.DisplayName) && agent.DisplayName.Trim().Length <= 160
            ? agent.DisplayName.Trim()
            : agent.Slug.Trim();
        var defaultTitle = value.Type == AepEventTypes.QuestionAsked
            ? $"{displayAgent} needs input"
            : $"Notification from {displayAgent}";

        request = new CreateNotificationRequest
        {
            Key = usesEventIdentityKey
                ? StableEventKey(agent.Slug, value.Id)
                : extension!.Key,
            Agent = agent.Slug,
            AgentInstance = agent.InstanceId ?? value.Session?.Id,
            Project = extension?.Project ?? ProjectName(value.Workspace),
            Type = notificationType,
            Priority = extension?.Priority ?? (value.Type == AepEventTypes.QuestionAsked
                ? NotificationPriority.High
                : null),
            Title = string.IsNullOrWhiteSpace(extension?.Title) ? defaultTitle : extension.Title,
            Message = content.Text,
            Cwd = value.Workspace?.Cwd,
            Pid = extension?.Pid,
            Metadata = CreateMetadata(value)
        };

        error = null;
        return true;
    }

    private static string? ValidateEnvelope(AepEvent? source)
    {
        if (source is null)
            return "body is required";
        if (!string.Equals(source.AepVersion, SupportedVersion, StringComparison.Ordinal))
            return $"aep_version must be {SupportedVersion}";
        if (string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 512)
            return "id is required and must be at most 512 characters";
        if (source.Type is not (AepEventTypes.NotificationSent or AepEventTypes.QuestionAsked))
            return "type must be notification.sent or question.asked";
        if (source.Time is null)
            return "time is required and must be an RFC 3339 timestamp";
        if (source.Agent is null || string.IsNullOrWhiteSpace(source.Agent.Slug))
            return "agent.slug is required";
        if (source.Agent.Slug.Length > 100)
            return "agent.slug must be at most 100 characters";
        return null;
    }

    private static bool TryReadExtension(
        Dictionary<string, JsonElement>? extensions,
        out AgentNotifyAepExtension? extension,
        out string? error)
    {
        extension = null;
        error = null;
        if (extensions is null || !extensions.TryGetValue(ExtensionName, out var element))
            return true;

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"extensions.{ExtensionName} must be an object";
            return false;
        }

        try
        {
            extension = element.Deserialize<AgentNotifyAepExtension>(Json.Options);
            if (extension is null)
                error = $"extensions.{ExtensionName} must be an object";
            else if (extension.UnknownFields is { Count: > 0 })
                error = $"extensions.{ExtensionName} contains unknown fields";
        }
        catch (JsonException)
        {
            error = $"extensions.{ExtensionName} is invalid";
        }

        return error is null;
    }

    private static string StableEventKey(string agentSlug, string eventId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{agentSlug}\n{eventId}"));
        return $"aep:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static string? ProjectName(AepWorkspace? workspace)
    {
        var path = workspace?.ProjectPath;
        if (string.IsNullOrWhiteSpace(path))
            path = workspace?.Cwd;
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var name = SafeFileName.Last(path.TrimEnd('/', '\\'));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static Dictionary<string, JsonElement> CreateMetadata(AepEvent source)
    {
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["aepVersion"] = JsonSerializer.SerializeToElement(source.AepVersion),
            ["aepEventId"] = JsonSerializer.SerializeToElement(source.Id),
            ["aepEventType"] = JsonSerializer.SerializeToElement(source.Type),
            ["aepTime"] = JsonSerializer.SerializeToElement(source.Time)
        };
        if (!string.IsNullOrWhiteSpace(source.Hook))
            metadata["aepHook"] = JsonSerializer.SerializeToElement(source.Hook);
        if (!string.IsNullOrWhiteSpace(source.Session?.ConversationId))
            metadata["aepConversationId"] = JsonSerializer.SerializeToElement(source.Session.ConversationId);
        if (!string.IsNullOrWhiteSpace(source.Session?.TurnId))
            metadata["aepTurnId"] = JsonSerializer.SerializeToElement(source.Session.TurnId);
        return metadata;
    }
}
