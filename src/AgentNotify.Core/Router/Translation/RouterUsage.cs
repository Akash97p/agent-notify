namespace AgentNotify.Core.Router.Translation;

/// <summary>
/// Inclusive usage: InputTokens includes cached tokens (OpenAI convention).
/// Anthropic reports input exclusive of cache_read/create; translation adds them.
/// </summary>
public sealed record RouterUsage(
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? ReasoningTokens);
