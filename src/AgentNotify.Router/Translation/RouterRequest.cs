namespace AgentNotify.Router.Translation;

/// <summary>Normalized router request across wires.</summary>
public sealed record RouterRequest(
    string? Model,
    string? SystemPrompt,
    IReadOnlyList<RouterMessage> Messages,
    IReadOnlyList<RouterTool> Tools,
    RouterToolChoice? ToolChoice,
    int? MaxOutputTokens,
    double? Temperature,
    double? TopP,
    IReadOnlyList<string>? StopSequences,
    bool Stream,
    string? ReasoningEffort,
    bool? ParallelToolCalls,
    IReadOnlySet<string> CustomToolNames)
{
    public static RouterRequest CreateEmpty(string? model = null) =>
        new(model, null, [], [], null, null, null, null, null, false, null, null, new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>A message in a normalized request.</summary>
public sealed record RouterMessage(
    string Role,
    IReadOnlyList<RouterMessagePart> Parts)
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
    public const string System = "system";
}

public abstract record RouterMessagePart;

/// <summary>A text part.</summary>
public sealed record RouterTextPart(string Text) : RouterMessagePart;

/// <summary>An image part.</summary>
public sealed record RouterImagePart(
    string? Url,
    string? Base64Data,
    string? MediaType) : RouterMessagePart
{
    public bool IsUrl => Url is not null;
    public bool IsBase64 => Base64Data is not null;
}

/// <summary>A tool call part.</summary>
public sealed record RouterToolCallPart(
    string Id,
    string Name,
    string ArgumentsJson) : RouterMessagePart;

/// <summary>A tool result part.</summary>
public sealed record RouterToolResultPart(
    string CallId,
    string Text,
    bool IsError) : RouterMessagePart;

/// <summary>A tool definition.</summary>
public sealed record RouterTool(
    string Name,
    string? Description,
    string? ParametersJson) // raw JSON string of the schema, null if none
{
}

/// <summary>Tool choice constraint.</summary>
public sealed record RouterToolChoice(
    string Kind, // auto | none | required | tool
    string? Name)
{
    public static RouterToolChoice Auto => new("auto", null);
    public static RouterToolChoice None => new("none", null);
    public static RouterToolChoice Required => new("required", null);
    public static RouterToolChoice ForTool(string name) => new("tool", name);

    public bool IsAuto => Kind == "auto";
    public bool IsNone => Kind == "none";
    public bool IsRequired => Kind == "required";
    public bool IsTool => Kind == "tool";
}

/// <summary>Result of decoding an inbound request.</summary>
public sealed record DecodeResult(
    RouterRequest Request,
    IReadOnlyList<string> DroppedNotes);
