using System.Reflection;
using System.Text;
using AgentNotify.Core.Skills;

namespace AgentNotify.Cli;

/// <summary>
/// The skill files this build carries, per agent.
/// </summary>
/// <remarks>
/// The install itself lives in <see cref="SkillInstaller"/>, which the tray app
/// uses too. What stays here is the payload: the CLI embeds its own copy so
/// <c>install-skill</c> works with no network and no installed app beside it.
/// </remarks>
internal static class SkillPayload
{
    private const string SkillResource = "AgentNotify.Cli.Resources.agentnotify.SKILL.md";
    private const string OpenAiMetadataResource = "AgentNotify.Cli.Resources.agentnotify.agents.openai.yaml";

    public static IReadOnlyList<SkillInstaller.SkillFile> For(AgentSkillTarget target)
    {
        var files = new List<SkillInstaller.SkillFile>
        {
            new("SKILL.md", ReadResource(SkillResource))
        };
        // Codex wants a descriptor beside the Markdown; every other agent reads
        // the Markdown alone and would treat a stray yaml as a second skill.
        if (target.Id == AgentSkillCatalog.Codex.Id)
            files.Add(new(Path.Combine("agents", "openai.yaml"), ReadResource(OpenAiMetadataResource)));
        return files;
    }

    private static string ReadResource(string name)
    {
        var assembly = typeof(SkillPayload).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded skill resource '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
