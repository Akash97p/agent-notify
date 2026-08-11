using System.Text.Json;

namespace AgentNotify.Contracts;

/// <summary>
/// Payload used to create a notification. Type and priority default to Info / Normal.
/// <see cref="Title"/> and <see cref="Message"/> are required.
/// </summary>
public sealed class CreateNotificationRequest
{
    /// <summary>Optional logical key. If an active notification with the same key exists,
    /// it is updated in place instead of creating a duplicate.</summary>
    public string? Key { get; set; }

    /// <summary>Name of the agent (e.g. "opencode"). Defaults to "unknown".</summary>
    public string? Agent { get; set; }

    /// <summary>Per-run agent instance identifier (e.g. "oc-71dc").</summary>
    public string? AgentInstance { get; set; }

    /// <summary>Project/repository name.</summary>
    public string? Project { get; set; }

    /// <summary>Built-in or user-defined stable type identifier.</summary>
    public string Type { get; set; } = NotificationTypes.Info;

    /// <summary>Optional priority. The selected type definition supplies the default when omitted.</summary>
    public NotificationPriority? Priority { get; set; }

    public string Title { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>Working directory of the agent (Windows or WSL path).</summary>
    public string? Cwd { get; set; }

    /// <summary>Process ID of the agent, when known. Used by the "Open Agent" action.</summary>
    public long? Pid { get; set; }

    /// <summary>Extensible custom metadata (values are arbitrary JSON).</summary>
    public Dictionary<string, JsonElement>? Metadata { get; set; }
}
