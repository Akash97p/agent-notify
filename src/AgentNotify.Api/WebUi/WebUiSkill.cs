using System.Reflection;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Harness;
using AgentNotify.Core.Skills;

namespace AgentNotify.Api.WebUi;

/// <summary>The agent skill as this build carries it, for installs started from the web UI.</summary>
internal static class WebUiSkill
{
    private static readonly Lazy<string> Skill = new(() => Read("AgentNotify.Api.Resources.SKILL.md"));
    private static readonly Lazy<string> OpenAiMetadata = new(() => Read("AgentNotify.Api.Resources.openai.yaml"));

    public static IReadOnlyList<SkillInstaller.SkillFile> Files(AgentSkillTarget target)
    {
        var files = new List<SkillInstaller.SkillFile> { new("SKILL.md", Skill.Value) };
        if (target.Id == AgentSkillCatalog.Codex.Id)
            files.Add(new(Path.Combine("agents", "openai.yaml"), OpenAiMetadata.Value));
        return files;
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
