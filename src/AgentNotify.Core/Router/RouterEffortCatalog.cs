using AgentNotify.Core.Router.Translation;

namespace AgentNotify.Core.Router;

public static class RouterEffortCatalog
{
    public const string Omit = "omit";
    public static readonly IReadOnlyList<string> SourceLevels = ["low", "medium", "high", "xhigh", "max"];

    public static RouterEffortCapability Resolve(
        StoredRouterUpstream upstream,
        string model,
        IReadOnlyList<RouterEffortMapping> overrides)
    {
        var saved = overrides.FirstOrDefault(item =>
            item.UpstreamId == upstream.Id && string.Equals(item.Model, model, StringComparison.Ordinal));
        if (saved is not null)
            return Capability(upstream, model, Family(model), "override", saved.SupportedValues, saved.LevelMap, saved.DefaultValue);

        var family = Family(model);
        return family switch
        {
            "openai" => Capability(upstream, model, family,
                upstream.Slug is "openai" or "chatgpt" || upstream.Auth == RouterAuth.CodexChatGpt ? "known" : "inferred",
                ["minimal", "low", "medium", "high", "xhigh"],
                ["minimal", "low", "medium", "high", "xhigh"]),
            "claude" => Capability(upstream, model, family,
                upstream.Slug == "anthropic" ? "known" : "inferred",
                ["low", "medium", "high", "xhigh", "max"],
                ["low", "medium", "high", "xhigh", "max"]),
            "deepseek" or "glm" or "kimi" or "qwen" or "minimax" or "grok" or "muse" =>
                Capability(upstream, model, family, "inferred",
                    ["low", "medium", "high", "xhigh"],
                    ["low", "low", "medium", "high", "xhigh"]),
            _ => Capability(upstream, model, family, "inferred", [], [Omit, Omit, Omit, Omit, Omit])
        };
    }

    /// <summary>
    /// The effort to send this target, given what the client asked for on <paramref name="inboundWire"/>.
    /// </summary>
    /// <remarks>
    /// The level map is written in Claude Code's five-step vocabulary, so it only applies to a request
    /// that arrived on the Anthropic wire. A client on an OpenAI wire already speaks its provider's own
    /// vocabulary — remapping Codex's <c>medium</c> through Claude's scale would silently change what it
    /// asked for — so its value is kept and only a missing one is filled from the target's default.
    /// </remarks>
    public static string? ForRequest(RouterEffortCapability capability, string inboundWire, string? sourceEffort)
    {
        if (inboundWire == RouterWire.AnthropicMessages) return Map(capability, sourceEffort);
        return string.IsNullOrWhiteSpace(sourceEffort) ? Default(capability) : sourceEffort;
    }

    /// <summary>Maps one of <see cref="SourceLevels"/> onto this target, or the default when absent.</summary>
    public static string? Map(RouterEffortCapability capability, string? sourceEffort)
    {
        if (string.IsNullOrWhiteSpace(sourceEffort)) return Default(capability);
        var index = -1;
        for (var level = 0; level < SourceLevels.Count; level++)
            if (string.Equals(SourceLevels[level], sourceEffort, StringComparison.OrdinalIgnoreCase)) index = level;
        if (index < 0) return Default(capability);
        var mapped = capability.LevelMap[index];
        return mapped == Omit ? null : mapped;
    }

    /// <summary>What this target gets when the request names no effort; null when it should carry none.</summary>
    public static string? Default(RouterEffortCapability capability) =>
        capability.DefaultValue == Omit ? null : capability.DefaultValue;

    public static RouterRequest Apply(RouterRequest request, RouterEffortCapability capability, string inboundWire) =>
        request with { ReasoningEffort = ForRequest(capability, inboundWire, request.ReasoningEffort) };

    public static void Validate(IReadOnlyList<string>? supportedValues, IReadOnlyList<string>? levelMap, string? defaultValue = null)
    {
        if (supportedValues is null || supportedValues.Count > 8 ||
            supportedValues.Any(value => !ValidValue(value) || value == Omit) ||
            supportedValues.Distinct(StringComparer.Ordinal).Count() != supportedValues.Count)
            throw new ArgumentException("Supported effort values must be up to eight unique printable identifiers.");
        if (levelMap is null || levelMap.Count != SourceLevels.Count)
            throw new ArgumentException("Effort mapping must contain Low, Medium, High, Extra, and Max values.");
        if (levelMap.Any(value => value != Omit && !supportedValues.Contains(value, StringComparer.Ordinal)))
            throw new ArgumentException("Every mapped effort must be supported by the target or set to omit.");
        if (defaultValue is not null && defaultValue != Omit && !supportedValues.Contains(defaultValue, StringComparer.Ordinal))
            throw new ArgumentException("Default effort must be supported by the target or set to omit.");
        var positions = levelMap.Select(value => value == Omit ? -1 : supportedValues.IndexOf(value)).ToList();
        var present = positions.Where(position => position >= 0).ToList();
        if (!present.SequenceEqual(present.Order()))
            throw new ArgumentException("Effort mapping must not decrease as source effort increases.");
    }

    public static string Family(string model)
    {
        var key = model.ToLowerInvariant();
        if (key.Contains("claude")) return "claude";
        if (key.Contains("deepseek")) return "deepseek";
        if (key.Contains("glm")) return "glm";
        if (key.Contains("kimi") || key.Contains("moonshot")) return "kimi";
        if (key.Contains("qwen")) return "qwen";
        if (key.Contains("minimax")) return "minimax";
        if (key.Contains("grok")) return "grok";
        if (key.Contains("muse")) return "muse";
        if (key.Contains("gpt") || key.StartsWith("o1") || key.StartsWith("o3") || key.StartsWith("o4")) return "openai";
        return "unknown";
    }

    private static RouterEffortCapability Capability(StoredRouterUpstream upstream, string model, string family,
        string source, IReadOnlyList<string> supported, IReadOnlyList<string> map, string? defaultValue = null) =>
        new(upstream.Id, upstream.Slug, model, upstream.WireFor(model), family, source, supported, map, defaultValue);

    private static bool ValidValue(string value) =>
        value.Length is > 0 and <= 32 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
            if (values[index] == value) return index;
        return -1;
    }
}
