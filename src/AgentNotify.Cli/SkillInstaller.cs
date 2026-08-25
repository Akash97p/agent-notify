using System.Reflection;
using System.Text;

namespace AgentNotify.Cli;

internal enum SkillAgent
{
    Codex,
    Claude
}

internal static class SkillInstaller
{
    private const string SkillResource = "AgentNotify.Cli.Resources.agentnotify.SKILL.md";
    private const string OpenAiMetadataResource = "AgentNotify.Cli.Resources.agentnotify.agents.openai.yaml";

    public static string DefaultSkillsRoot(SkillAgent agent, bool projectScope)
    {
        if (projectScope)
        {
            var directory = Directory.GetCurrentDirectory();
            return Path.Combine(directory, agent == SkillAgent.Codex ? ".agents" : ".claude", "skills");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("Could not determine the current user's home directory. Pass --path explicitly.");

        return Path.Combine(profile, agent == SkillAgent.Codex ? ".agents" : ".claude", "skills");
    }

    public static SkillInstallResult Install(
        SkillAgent agent,
        string skillsRoot,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsRoot);

        var fullRoot = Path.GetFullPath(skillsRoot);
        var skillDirectory = Path.Combine(fullRoot, "agentnotify");
        var files = new List<(string RelativePath, string Content)>
        {
            ("SKILL.md", ReadResource(SkillResource))
        };
        if (agent == SkillAgent.Codex)
            files.Add((Path.Combine("agents", "openai.yaml"), ReadResource(OpenAiMetadataResource)));

        var changes = new List<(string Path, string Content)>();
        foreach (var file in files)
        {
            var destination = Path.Combine(skillDirectory, file.RelativePath);
            if (!File.Exists(destination))
            {
                changes.Add((destination, file.Content));
                continue;
            }

            var existing = File.ReadAllText(destination);
            if (string.Equals(existing, file.Content, StringComparison.Ordinal))
                continue;
            if (!force)
                return new SkillInstallResult(false, false, skillDirectory,
                    $"Refusing to overwrite the existing file '{destination}'. Re-run with --force after reviewing it.");

            changes.Add((destination, file.Content));
        }

        if (changes.Count == 0)
            return new SkillInstallResult(true, false, skillDirectory, "AgentNotify skill is already up to date.");
        if (dryRun)
            return new SkillInstallResult(true, false, skillDirectory,
                $"Would install {changes.Count} file(s) into '{skillDirectory}'.");

        foreach (var change in changes)
            WriteAtomically(change.Path, change.Content);

        return new SkillInstallResult(true, true, skillDirectory,
            $"Installed the AgentNotify skill for {DisplayName(agent)} at '{skillDirectory}'.");
    }

    private static string DisplayName(SkillAgent agent) => agent == SkillAgent.Codex ? "Codex" : "Claude Code";

    private static string ReadResource(string name)
    {
        var assembly = typeof(SkillInstaller).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded skill resource '{name}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteAtomically(string destination, string content)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException($"Could not determine the destination directory for '{destination}'.");
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

internal sealed record SkillInstallResult(
    bool Success,
    bool Changed,
    string SkillDirectory,
    string Message);
