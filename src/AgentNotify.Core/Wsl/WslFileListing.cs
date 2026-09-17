using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentNotify.Core.Wsl;

/// <summary>A file found inside a WSL distribution, addressed through the WSL share.</summary>
public sealed record WslFile(string WindowsPath, long Length, DateTime ModifiedUtc);

/// <summary>
/// Lists files inside a running WSL distribution with one native <c>find</c>. Walking thousands of
/// directories through <c>\\wsl.localhost</c> costs a round trip per directory and took over ten
/// seconds for one agent's session tree; the same listing inside the distribution takes milliseconds.
/// </summary>
public static class WslFileListing
{
    /// <summary>
    /// Files named <paramref name="namePattern"/> under a share path such as
    /// <c>\\wsl.localhost\Ubuntu\home\me\.codex\sessions</c>, or null when the listing is unavailable
    /// and the caller should walk the share itself. A root that does not exist lists no files.
    /// </summary>
    public static IReadOnlyList<WslFile>? TryList(string shareRoot, string namePattern, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows() || !WslPath.TryParse(shareRoot, out var distribution, out var linuxRoot) ||
            !WslPath.IsSafeLinuxPath(linuxRoot) || namePattern.Any(c => char.IsControl(c) || c is '/' or '\\'))
            return null;
        var host = shareRoot.Trim().Replace('/', '\\').TrimStart('\\').Split('\\')[0];
        var shareBase = @"\\" + host + @"\" + distribution;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wsl.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        // No shell is involved: find receives each argument as given. Symbolic links are not
        // followed, and a missing root only prints to stderr.
        foreach (var argument in new[]
                 {
                     "--distribution", distribution, "--exec", "find", linuxRoot, "-type", "f", "-name", namePattern,
                     "-printf", "%s\\t%T@\\t%p\\0"
                 })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds) || !copy.Wait(TimeSpan.FromSeconds(2)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            // find exits 1 when the root is missing, which is an empty listing rather than a failure.
            if (process.ExitCode is not (0 or 1)) return null;
            return Parse(output.ToArray(), shareBase);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or AggregateException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes <c>find -printf '%s\t%T@\t%p\0'</c> output into share paths under
    /// <paramref name="shareBase"/> (<c>\\wsl.localhost\&lt;distribution&gt;</c>). Entries with unsafe paths
    /// are dropped.
    /// </summary>
    internal static IReadOnlyList<WslFile> Parse(byte[] output, string shareBase)
    {
        var files = new List<WslFile>();
        foreach (var entry in Encoding.UTF8.GetString(output).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = entry.Split('\t', 3);
            if (fields.Length != 3 ||
                !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                !decimal.TryParse(fields[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
                !WslPath.IsSafeLinuxPath(fields[2]) || fields[2].Contains('\\'))
                continue;
            DateTime modified;
            try { modified = DateTime.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond)); }
            catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException) { continue; }
            files.Add(new WslFile(WslPath.ToWindows(shareBase, fields[2]), length, modified));
        }
        return files;
    }
}
