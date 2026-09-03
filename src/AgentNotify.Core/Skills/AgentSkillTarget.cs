namespace AgentNotify.Core.Skills;

/// <summary>
/// A coding agent that reads Markdown skills, and where it looks for them.
/// </summary>
/// <remarks>
/// The catalogue lives here rather than in the CLI because the tray app installs
/// the same file to the same places, and two lists of "where does Claude Code
/// keep its skills" would drift the first time one of them moved.
/// <para>
/// Every entry is a folder convention published by the agent, not something
/// AgentNotify decides. When an agent changes its convention this list is wrong
/// until it is edited, which is why <see cref="AgentSkillCatalog.Custom"/>
/// exists: a person who knows better than this list can always point the
/// installer at a folder.
/// </para>
/// </remarks>
public sealed record AgentSkillTarget(
    /// <summary>Stable identifier, used by the CLI's <c>--agent</c> option.</summary>
    string Id,
    /// <summary>What the agent calls itself.</summary>
    string DisplayName,
    /// <summary>
    /// Path of the skills root relative to the user's home directory, or null
    /// for an agent whose folder can only be chosen by hand.
    /// </summary>
    string[]? PersonalSegments,
    /// <summary>The same, relative to a repository root.</summary>
    string[]? ProjectSegments,
    /// <summary>One line describing where the skill will land and why.</summary>
    string Note)
{
    /// <summary>Whether this entry knows a folder without being told one.</summary>
    public bool HasDefaultLocation => PersonalSegments is not null;
}
