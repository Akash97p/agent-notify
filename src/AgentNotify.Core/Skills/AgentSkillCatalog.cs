namespace AgentNotify.Core.Skills;

/// <summary>
/// The agents AgentNotify knows how to install its skill for.
/// </summary>
/// <remarks>
/// Deliberately short. An entry here is a claim about another product's on-disk
/// layout, and a wrong claim writes a file somewhere the agent will never read —
/// which looks exactly like a successful install. Anything not listed goes
/// through <see cref="Custom"/>, where the person choosing the folder is the one
/// making the claim.
/// </remarks>
public static class AgentSkillCatalog
{
    /// <summary>The folder name the skill is installed into, under a skills root.</summary>
    public const string SkillFolderName = "agentnotify";

    public static readonly AgentSkillTarget ClaudeCode = new(
        Id: "claude",
        DisplayName: "Claude Code",
        PersonalSegments: [".claude", "skills"],
        ProjectSegments: [".claude", "skills"],
        Note: "Loaded for every project. Claude reads it when a request mentions notifying you.");

    public static readonly AgentSkillTarget Codex = new(
        Id: "codex",
        DisplayName: "Codex",
        PersonalSegments: [".agents", "skills"],
        ProjectSegments: [".agents", "skills"],
        Note: "Installs the skill and the openai.yaml descriptor Codex needs alongside it.");

    public static readonly AgentSkillTarget OpenCode = new(
        Id: "opencode",
        DisplayName: "OpenCode",
        PersonalSegments: [".config", "opencode", "skill"],
        ProjectSegments: [".opencode", "skill"],
        Note: "OpenCode reads Markdown skills from its config directory.");

    /// <summary>
    /// Any other agent. Has no default location on purpose: the folder is the
    /// one piece of information this program does not have.
    /// </summary>
    public static readonly AgentSkillTarget Custom = new(
        Id: "custom",
        DisplayName: "Another agent",
        PersonalSegments: null,
        ProjectSegments: null,
        Note: "Choose the folder your agent loads skills from. The file is plain Markdown.");

    public static readonly IReadOnlyList<AgentSkillTarget> All =
        [ClaudeCode, Codex, OpenCode, Custom];

    /// <summary>Every agent with a folder this program can work out for itself.</summary>
    public static IEnumerable<AgentSkillTarget> WithKnownLocations =>
        All.Where(target => target.HasDefaultLocation);

    public static AgentSkillTarget? Find(string id) =>
        All.FirstOrDefault(target => string.Equals(target.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where this agent keeps its skills, either for the current user or for the
    /// repository in <paramref name="projectDirectory"/>. <paramref name="homeDirectory"/> replaces the
    /// current user's home, for an agent that runs under another home such as a WSL distribution's.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The target has no default location, or the home directory is unknown —
    /// both cases where the caller has to supply a path instead of guessing.
    /// </exception>
    public static string DefaultSkillsRoot(
        AgentSkillTarget target,
        string? projectDirectory = null,
        string? homeDirectory = null)
    {
        var segments = projectDirectory is null ? target.PersonalSegments : target.ProjectSegments;
        if (segments is null)
            throw new InvalidOperationException(
                $"{target.DisplayName} has no default skills folder. Pass one explicitly.");

        var root = projectDirectory ?? homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException(
                "Could not determine the current user's home directory. Pass a path explicitly.");

        return Path.Combine([root, .. segments]);
    }
}
