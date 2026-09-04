using AgentNotify.Core.Skills;

namespace AgentNotify.Tests;

/// <summary>
/// Installing the agent skill.
/// </summary>
/// <remarks>
/// The installer writes into folders another program owns, so the behaviour
/// worth pinning down is what it refuses to do: overwrite an edited skill, or
/// report "up to date" about something that is not.
/// </remarks>
public sealed class SkillInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"agentnotify-skill-{Guid.NewGuid():N}");

    private static readonly IReadOnlyList<SkillInstaller.SkillFile> Files =
    [
        new("SKILL.md", "# AgentNotify\nsend a notification\n"),
        new(Path.Combine("agents", "openai.yaml"), "name: agentnotify\n")
    ];

    private string SkillPath(string relative) =>
        Path.Combine(SkillInstaller.SkillDirectory(_root), relative);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Install_WritesEveryFileUnderTheSkillFolder()
    {
        var result = SkillInstaller.Install("Claude Code", _root, Files, force: false, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal("# AgentNotify\nsend a notification\n", File.ReadAllText(SkillPath("SKILL.md")));
        Assert.Equal("name: agentnotify\n", File.ReadAllText(SkillPath(Path.Combine("agents", "openai.yaml"))));
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        SkillInstaller.Install("Claude Code", _root, Files, force: false, dryRun: false);
        var again = SkillInstaller.Install("Claude Code", _root, Files, force: false, dryRun: false);

        Assert.True(again.Success);
        Assert.False(again.Changed);
    }

    [Fact]
    public void Install_RefusesToOverwriteAnEditedSkill()
    {
        SkillInstaller.Install("Claude Code", _root, Files, force: false, dryRun: false);
        File.WriteAllText(SkillPath("SKILL.md"), "# AgentNotify\nour team's own wording\n");

        var result = SkillInstaller.Install("Claude Code", _root, Files, force: false, dryRun: false);

        Assert.False(result.Success);
        // The edit survives a refusal. Losing it is the failure this guards.
        Assert.Contains("our team's own wording", File.ReadAllText(SkillPath("SKILL.md")));
    }

    [Fact]
    public void Install_ReplacesAnEditedSkillWhenForced()
    {
        SkillInstaller.Install("Claude Code", _root, Files, force: false, dryRun: false);
        File.WriteAllText(SkillPath("SKILL.md"), "# edited\n");

        var result = SkillInstaller.Install("Claude Code", _root, Files, force: true, dryRun: false);

        Assert.True(result.Success);
        Assert.True(result.Changed);
        Assert.Equal("# AgentNotify\nsend a notification\n", File.ReadAllText(SkillPath("SKILL.md")));
    }

    [Fact]
    public void DryRun_WritesNothing()
    {
        var result = SkillInstaller.Install("Claude Code", _root, Files, force: false, dryRun: true);

        Assert.True(result.Success);
        Assert.False(result.Changed);
        Assert.False(Directory.Exists(SkillInstaller.SkillDirectory(_root)));
    }

    [Fact]
    public void Inspect_TellsTheThreeStatesApart()
    {
        Assert.Equal(SkillInstallState.NotInstalled, SkillInstaller.Inspect(_root, Files));

        SkillInstaller.Install("Claude Code", _root, Files, force: false, dryRun: false);
        Assert.Equal(SkillInstallState.UpToDate, SkillInstaller.Inspect(_root, Files));

        File.WriteAllText(SkillPath("SKILL.md"), "# older\n");
        Assert.Equal(SkillInstallState.Outdated, SkillInstaller.Inspect(_root, Files));
    }

    [Fact]
    public void Inspect_CallsAPartialInstallOutdated()
    {
        // An install from a build that shipped fewer files. Every file present
        // matches, but there is still something for Install to do.
        SkillInstaller.Install("Claude Code", _root, Files, force: false, dryRun: false);
        File.Delete(SkillPath(Path.Combine("agents", "openai.yaml")));

        Assert.Equal(SkillInstallState.Outdated, SkillInstaller.Inspect(_root, Files));
    }
}

public sealed class AgentSkillCatalogTests
{
    [Theory]
    [InlineData("claude", ".claude")]
    [InlineData("codex", ".agents")]
    [InlineData("opencode", ".config")]
    public void PersonalRoot_SitsUnderTheHomeDirectory(string id, string firstSegment)
    {
        var target = AgentSkillCatalog.Find(id);
        Assert.NotNull(target);

        var root = AgentSkillCatalog.DefaultSkillsRoot(target!);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.StartsWith(Path.Combine(home, firstSegment), root, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectRoot_SitsUnderTheRepository()
    {
        var root = AgentSkillCatalog.DefaultSkillsRoot(AgentSkillCatalog.ClaudeCode, "/repo");
        Assert.Equal(Path.Combine("/repo", ".claude", "skills"), root);
    }

    [Fact]
    public void CustomTarget_HasNoLocationToGuess()
    {
        Assert.False(AgentSkillCatalog.Custom.HasDefaultLocation);
        Assert.Throws<InvalidOperationException>(
            () => AgentSkillCatalog.DefaultSkillsRoot(AgentSkillCatalog.Custom));
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndUnknownIdsAreNull()
    {
        Assert.Same(AgentSkillCatalog.ClaudeCode, AgentSkillCatalog.Find("CLAUDE"));
        Assert.Null(AgentSkillCatalog.Find("nonesuch"));
    }
}
