namespace AgentNotify.Core.Router.Translation;

/// <summary>Normalized non-streaming response.</summary>
public sealed record RouterResponse(
    string Text,
    string ReasoningText,
    IReadOnlyList<RouterToolCallPart> ToolCalls,
    string? FinishReason,
    RouterUsage? Usage);

