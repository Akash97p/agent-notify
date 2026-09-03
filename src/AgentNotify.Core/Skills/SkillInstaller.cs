using System.Text;

namespace AgentNotify.Core.Skills;

/// <summary>What an install did, or would have done.</summary>
public sealed record SkillInstallResult(
    bool Success,
    bool Changed,
    string SkillDirectory,
    string Message);

/// <summary>The state of an agent's copy of the skill, before anything is written.</summary>
public enum SkillInstallState
{
    /// <summary>Nothing is there.</summary>
    NotInstalled,
    /// <summary>Byte for byte what this build carries.</summary>
    UpToDate,
    /// <summary>Present, and different — a newer build, or an edited copy.</summary>
    Outdated
}

/// <summary>
/// Writes the AgentNotify skill into an agent's skills folder.
/// </summary>
/// <remarks>
/// Content is passed in rather than read here: the CLI and the tray app each
/// carry their own embedded copy, and this layer has no business knowing which
/// assembly's resources to reach into.
/// <para>
/// It will not overwrite a file that differs unless told to. A skill is a file
/// people edit — adding a project convention, trimming an example — and silently
/// replacing that during an unrelated update is the kind of thing that teaches
/// people not to press buttons.
/// </para>
/// </remarks>
public static class SkillInstaller
{
    /// <summary>One file of a skill, relative to the skill's own folder.</summary>
    public sealed record SkillFile(string RelativePath, string Content);

    /// <summary>Whether the skill is there, and whether it matches this build.</summary>
    public static SkillInstallState Inspect(string skillsRoot, IReadOnlyList<SkillFile> files)
    {
        var directory = SkillDirectory(skillsRoot);
        var seen = false;

        foreach (var file in files)
        {
            var destination = Path.Combine(directory, file.RelativePath);
            if (!File.Exists(destination)) continue;
            seen = true;
            if (!SameContent(destination, file.Content)) return SkillInstallState.Outdated;
        }

        if (!seen) return SkillInstallState.NotInstalled;

        // Every file that exists matches, but one may be missing entirely — an
        // install from a build that shipped fewer files. That is "outdated", not
        // "up to date": pressing Install has something to do.
        var complete = files.All(file => File.Exists(Path.Combine(directory, file.RelativePath)));
        return complete ? SkillInstallState.UpToDate : SkillInstallState.Outdated;
    }

    public static string SkillDirectory(string skillsRoot) =>
        Path.Combine(Path.GetFullPath(skillsRoot), AgentSkillCatalog.SkillFolderName);

    public static SkillInstallResult Install(
        string displayName,
        string skillsRoot,
        IReadOnlyList<SkillFile> files,
        bool force,
        bool dryRun)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsRoot);

        var skillDirectory = SkillDirectory(skillsRoot);
        var changes = new List<(string Path, string Content)>();

        foreach (var file in files)
        {
            var destination = Path.Combine(skillDirectory, file.RelativePath);
            if (!File.Exists(destination))
            {
                changes.Add((destination, file.Content));
                continue;
            }

            if (SameContent(destination, file.Content)) continue;
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
            $"Installed the AgentNotify skill for {displayName} at '{skillDirectory}'.");
    }

    private static bool SameContent(string path, string content)
    {
        try
        {
            return string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            // Unreadable counts as different: the install path will fail loudly
            // rather than this reporting "up to date" about a file nobody can see.
            return false;
        }
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
