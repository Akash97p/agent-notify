namespace AgentNotify.Core;

/// <summary>
/// Restricts local AgentNotify state to the owning user on Unix-like systems.
/// </summary>
/// <remarks>
/// On Windows the per-user profile directory already carries a restrictive ACL and DPAPI protects
/// provider secrets, so every method here is a no-op. On Linux and macOS the config file holds the
/// local bearer token, the database holds notification history, and the fallback key file holds the
/// secret-protection key; all three are created world-readable by default, which would let any
/// process running as another user on a shared machine read them.
///
/// Permission changes are best-effort. A filesystem that does not support Unix modes must never
/// prevent AgentNotify from starting, so failures are swallowed rather than propagated.
/// </remarks>
public static class UnixFilePermissions
{
    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerOnlyFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>True when Unix file modes are meaningful on the current platform.</summary>
    public static bool IsSupported => !OperatingSystem.IsWindows();

    /// <summary>Creates <paramref name="path"/> if needed and restricts it to the owner (0700).</summary>
    public static void CreateOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        try
        {
            Directory.CreateDirectory(path, OwnerOnlyDirectory);
        }
        catch (Exception)
        {
            // Fall back to a plain create; the mode is tightened separately below.
            try { Directory.CreateDirectory(path); } catch { return; }
        }

        TrySetMode(path, OwnerOnlyDirectory, directory: true);
    }

    /// <summary>Restricts an existing file to the owner (0600). Missing files are ignored.</summary>
    public static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        TrySetMode(path, OwnerOnlyFile, directory: false);
    }

    private static void TrySetMode(string path, UnixFileMode mode, bool directory)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            if (directory ? Directory.Exists(path) : File.Exists(path))
                File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // Unsupported filesystem, a mount without permission support, or a race with deletion.
            // Restricting local state is defence in depth, never a startup requirement.
        }
    }
}
