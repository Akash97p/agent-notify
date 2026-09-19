using AgentNotify.Core.Router.Translation;

namespace AgentNotify.Core.Router;

public static class RouterEffortCatalog
{
    public const string Omit = "omit";
    public static readonly IReadOnlyList<string> SourceLevels = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>The model families a family-level override can name; mirrors <see cref="Family"/>.</summary>
    public static readonly IReadOnlyList<string> Families =
        ["claude", "openai", "deepseek", "glm", "kimi", "qwen", "minimax", "grok", "muse", "unknown"];

    public static RouterEffortCapability Resolve(
        StoredRouterUpstream upstream,
        string model,
        IReadOnlyList<RouterEffortMapping> overrides,
        IReadOnlyList<RouterEffortFamilyOverride>? familyOverrides = null)
    {
        var family = Family(model);
        var saved = overrides.FirstOrDefault(item =>
            item.UpstreamId == upstream.Id && string.Equals(item.Model, model, StringComparison.Ordinal));
        if (saved is not null)
            return Capability(upstream, model, family, "override", saved.SupportedValues, saved.LevelMap, saved.DefaultValue);

        var familySaved = familyOverrides?.FirstOrDefault(item => item.Family == family);
        if (familySaved is not null)
            return Capability(upstream, model, family, "family", familySaved.SupportedValues, familySaved.LevelMap, familySaved.DefaultValue);

        var (supported, map, _) = FamilyAutomatic(family);
        var known = family == "openai"
            ? upstream.Slug is "openai" or "chatgpt" || upstream.Auth == RouterAuth.CodexChatGpt
            : family == "claude" && upstream.Slug == "anthropic";
        return Capability(upstream, model, family, known ? "known" : "inferred", supported, map, null);
    }

    /// <summary>
    /// The automatic vocabulary for one family: every level the target can spell is sent under its own
    /// name, and only levels above the target's top collapse onto it. A mapping that renames a level
    /// the target supports would silently change what a client asked for, which is a defect, not policy.
    /// </summary>
    public static (IReadOnlyList<string> Supported, IReadOnlyList<string> Map, string? Default) FamilyAutomatic(string family) =>
        family switch
        {
            "openai" => (["minimal", "low", "medium", "high", "xhigh"],
                          ["minimal", "low", "medium", "high", "xhigh"], null),
            "claude" => (["low", "medium", "high", "xhigh", "max"],
                          ["low", "medium", "high", "xhigh", "max"], null),
            "deepseek" or "glm" or "kimi" or "qwen" or "minimax" or "grok" or "muse" => (
                          ["low", "medium", "high", "xhigh"],
                          ["low", "medium", "high", "xhigh", "xhigh"], null),
            _ => ([], [Omit, Omit, Omit, Omit, Omit], null),
        };

    /// <summary>
    /// The effort to send this target, given what the client asked for. Both inbound wires are mapped:
    /// low, medium, high, xhigh, and max mean the same thing on Claude Code's five-step scale and on
    /// Codex's own, so one table serves either client. A value outside that scale — OpenAI's
    /// <c>minimal</c>, or a provider-specific word — is sent verbatim when the target supports it, and
    /// otherwise falls back to the target's default (with <c>minimal</c> treated as low).
    /// </summary>
    public static string? ForRequest(RouterEffortCapability capability, string? sourceEffort)
    {
        if (string.IsNullOrWhiteSpace(sourceEffort)) return Default(capability);
        var value = sourceEffort.Trim();
        var index = LevelIndex(value);
        if (index >= 0)
        {
            var mapped = capability.LevelMap[index];
            return mapped == Omit ? null : mapped;
        }
        if (capability.SupportedValues.Contains(value, StringComparer.Ordinal)) return value;
        if (string.Equals(value, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            var mapped = capability.LevelMap[0];
            return mapped == Omit ? null : mapped;
        }
        return Default(capability);
    }

    /// <summary>Maps one of <see cref="SourceLevels"/> onto this target, or the default when absent.</summary>
    public static string? Map(RouterEffortCapability capability, string? sourceEffort)
    {
        if (string.IsNullOrWhiteSpace(sourceEffort)) return Default(capability);
        var index = LevelIndex(sourceEffort.Trim());
        if (index < 0) return Default(capability);
        var mapped = capability.LevelMap[index];
        return mapped == Omit ? null : mapped;
    }

    /// <summary>What this target gets when the request names no effort; null when it should carry none.</summary>
    public static string? Default(RouterEffortCapability capability) =>
        capability.DefaultValue == Omit ? null : capability.DefaultValue;

    public static RouterRequest Apply(RouterRequest request, RouterEffortCapability capability) =>
        request with { ReasoningEffort = ForRequest(capability, request.ReasoningEffort) };

    public static void Validate(IReadOnlyList<string>? supportedValues, IReadOnlyList<string>? levelMap, string? defaultValue = null)
    {
        if (supportedValues is null || supportedValues.Count > 8 ||
            supportedValues.Any(value => !ValidValue(value) || value == Omit) ||
            supportedValues.Distinct(StringComparer.Ordinal).Count() != supportedValues.Count())
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

    public static void ValidateFamily(string family)
    {
        if (!Families.Contains(family, StringComparer.Ordinal))
            throw new ArgumentException($"Family must be one of: {string.Join(", ", Families)}.");
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

    private static int LevelIndex(string value)
    {
        for (var level = 0; level < SourceLevels.Count; level++)
            if (string.Equals(SourceLevels[level], value, StringComparison.OrdinalIgnoreCase)) return level;
        return -1;
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
