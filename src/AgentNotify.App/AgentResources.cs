using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using AgentNotify.Core.Config;

namespace AgentNotify.App;

internal static class AgentResources
{
    private const string SkillResource = "AgentNotify.Resources.SKILL.md";
    private const string GettingStartedResource = "AgentNotify.Resources.GettingStarted.html";
    private const string SkillPlaceholder = "__AGENTNOTIFY_SKILL_JSON__";

    private static readonly Lazy<string> Skill = new(() => ReadEmbedded(SkillResource));
    private static readonly Lazy<string> GettingStarted = new(() => ReadEmbedded(GettingStartedResource));

    public static string SkillText => Skill.Value;

    public static string WriteGettingStarted()
    {
        var directory = Path.Combine(ConfigStore.DefaultConfigDir(), "resources");
        Directory.CreateDirectory(directory);
        var skillPath = Path.Combine(directory, "SKILL.md");
        var htmlPath = Path.Combine(directory, "GettingStarted.html");
        File.WriteAllText(skillPath, SkillText);
        File.WriteAllText(htmlPath, GettingStarted.Value.Replace(
            SkillPlaceholder,
            JsonSerializer.Serialize(SkillText),
            StringComparison.Ordinal));
        return htmlPath;
    }

    public static void OpenGettingStarted()
    {
        var path = WriteGettingStarted();
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string ReadEmbedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
