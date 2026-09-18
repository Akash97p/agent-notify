namespace AgentNotify.Core.Router.Translation;

public abstract record RouterStreamEvent;

/// <summary>A text delta.</summary>
public sealed record TextDeltaEvent(string Text) : RouterStreamEvent;

/// <summary>A reasoning delta.</summary>
public sealed record ReasoningDeltaEvent(string Text) : RouterStreamEvent;

/// <summary>Start of a tool call.</summary>
public sealed record ToolCallStartEvent(int Index, string Id, string Name) : RouterStreamEvent;

/// <summary>Incremental tool call arguments.</summary>
public sealed record ToolCallArgumentsDeltaEvent(int Index, string JsonDelta) : RouterStreamEvent;

/// <summary>End of a tool call.</summary>
public sealed record ToolCallEndEvent(int Index) : RouterStreamEvent;

/// <summary>Usage reported mid-stream.</summary>
public sealed record UsageEvent(RouterUsage Usage) : RouterStreamEvent;

/// <summary>Stream finished.</summary>
public sealed record FinishEvent(string Reason) : RouterStreamEvent
{
    public const string Stop = "stop";
    public const string Length = "length";
    public const string ToolCalls = "tool_calls";
    public const string ContentFilter = "content_filter";
    public const string Error = "error";
}

/// <summary>Stream started.</summary>
public sealed record StartEvent : RouterStreamEvent;
