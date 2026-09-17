using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentNotify.Core.Wsl;

/// <summary>A Linux user's home in a running WSL distribution, as the Windows broker reaches it.</summary>
/// <param name="Distribution">The distribution name, e.g. <c>Ubuntu-20.04</c>.</param>
/// <param name="LinuxHome">The home directory inside the distribution, e.g. <c>/home/akash</c>.</param>
/// <param name="WindowsHome">The same directory through the WSL share, e.g. <c>\\wsl.localhost\Ubuntu-20.04\home\akash</c>.</param>
public sealed record WslHome(string Distribution, string LinuxHome, string WindowsHome);

/// <summary>Where coding agents running inside WSL keep their files.</summary>
public interface IWslEnvironment
{
    /// <summary>The default user's home in every WSL distribution that is running now.</summary>
    IReadOnlyList<WslHome> RunningHomes();
}

/// <summary>
/// Finds WSL distributions on Windows so the broker can read agent logs, credentials, and skill
/// folders that live inside them. Returns nothing on other platforms.
/// </summary>
/// <remarks>
/// Only running distributions are read. Opening <c>\\wsl.localhost\&lt;name&gt;</c> starts a stopped
/// distribution, and a dashboard visit must not boot a Linux VM. A distribution's history therefore
/// appears while it is running, which is whenever an agent inside it is.
/// </remarks>
public sealed class WslDiscovery : IWslEnvironment
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public static WslDiscovery Default { get; } = new();

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private IReadOnlyList<WslHome> _cached = [];
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public WslDiscovery(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<WslHome> RunningHomes()
    {
        if (!OperatingSystem.IsWindows()) return [];
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (now - _cachedAt < CacheLifetime) return _cached;
            _cached = Discover();
            _cachedAt = _clock.GetUtcNow();
            return _cached;
        }
    }

    private static IReadOnlyList<WslHome> Discover()
    {
        var registered = Registered();
        if (registered.Count == 0) return [];
        var running = Running();
        var homes = new List<WslHome>();
        foreach (var (name, uid) in registered)
        {
            if (!running.Contains(name)) continue;
            try
            {
                var home = HomeOf(name, uid);
                if (home is not null) homes.Add(home);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // One unreadable distribution must not hide the others.
            }
        }
        return homes;
    }

    private static List<(string Name, int Uid)> Registered()
    {
        var result = new List<(string, int)>();
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(LxssKey);
            if (lxss is null) return result;
            foreach (var id in lxss.GetSubKeyNames())
            {
                using var distribution = lxss.OpenSubKey(id);
                if (distribution?.GetValue("DistributionName") is not string name ||
                    !WslPath.IsValidDistributionName(name) ||
                    name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)) continue;
                var uid = distribution.GetValue("DefaultUid") is int value ? value : 0;
                result.Add((name, uid));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return result;
    }

    private static HashSet<string> Running()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        foreach (var argument in new[] { "--list", "--running", "--quiet" })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["WSL_UTF8"] = "1";
        try
        {
            process.Start();
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            if (!process.WaitForExit(5000) || !copy.Wait(1000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return names;
            }
            foreach (var name in ParseRunningList(output.ToArray())) names.Add(name);
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException or AggregateException)
        {
        }
        return names;
    }

    private static WslHome? HomeOf(string name, int uid)
    {
        var root = @"\\wsl.localhost\" + name;
        if (!Directory.Exists(root))
        {
            root = @"\\wsl$\" + name;
            if (!Directory.Exists(root)) return null;
        }
        var passwd = new FileInfo(Path.Combine(root, "etc", "passwd"));
        if (!passwd.Exists || passwd.Length > 1024 * 1024) return null;
        // Distributions set up with "[user] default=" in /etc/wsl.conf keep DefaultUid at 0 in the
        // registry, and wsl.conf wins, so it names the user agents actually run as.
        var wslConf = new FileInfo(Path.Combine(root, "etc", "wsl.conf"));
        var user = wslConf.Exists && wslConf.Length <= 64 * 1024 ? DefaultUser(File.ReadAllText(wslConf.FullName)) : null;
        var passwdText = File.ReadAllText(passwd.FullName);
        var linuxHome = (user is null ? null : FindHome(passwdText, user)) ?? FindHome(passwdText, uid);
        if (linuxHome is null) return null;
        var windowsHome = WslPath.ToWindows(root, linuxHome);
        return Directory.Exists(windowsHome) ? new WslHome(name, linuxHome, windowsHome) : null;
    }

    /// <summary>
    /// Decodes <c>wsl.exe --list --running --quiet</c>. With <c>WSL_UTF8=1</c> the output is UTF-8;
    /// older wsl.exe builds ignore that and write UTF-16LE, with or without a byte-order mark.
    /// </summary>
    internal static IReadOnlyList<string> ParseRunningList(byte[] output)
    {
        string text;
        if (output.Length >= 2 && output[0] == 0xFF && output[1] == 0xFE)
            text = Encoding.Unicode.GetString(output, 2, output.Length - 2);
        else if (output.Length >= 2 && Array.IndexOf(output, (byte)0) >= 0)
            text = Encoding.Unicode.GetString(output);
        else
            text = Encoding.UTF8.GetString(output);
        return text.Replace("\0", "", StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimStart('\uFEFF'))
            .Where(WslPath.IsValidDistributionName)
            .ToArray();
    }

    /// <summary>The user named by <c>default=</c> in the <c>[user]</c> section of <c>/etc/wsl.conf</c>.</summary>
    internal static string? DefaultUser(string wslConf)
    {
        var inUser = false;
        string? user = null;
        foreach (var raw in wslConf.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                inUser = line.TrimEnd(']', ' ', '\t')[1..].Trim().Equals("user", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var equals = line.IndexOf('=');
            if (!inUser || equals < 0 || !line[..equals].Trim().Equals("default", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(equals + 1)..];
            var comment = value.IndexOf('#');
            if (comment >= 0) value = value[..comment];
            user = value.Trim().Trim('"', '\'').Trim();
        }
        return IsValidUserName(user) ? user : null;
    }

    private static bool IsValidUserName(string? name) =>
        name is { Length: > 0 and <= 32 } && name[0] != '-' &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '$');

    /// <summary>The home directory of <paramref name="uid"/> in an <c>/etc/passwd</c> file.</summary>
    internal static string? FindHome(string passwd, int uid) =>
        FindHome(passwd, 2, uid.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The home directory of the user called <paramref name="user"/> in an <c>/etc/passwd</c> file.</summary>
    internal static string? FindHome(string passwd, string user) => FindHome(passwd, 0, user);

    private static string? FindHome(string passwd, int field, string wanted)
    {
        foreach (var line in passwd.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split(':');
            if (fields.Length < 7 || fields[field] != wanted) continue;
            var home = fields[5].TrimEnd('/');
            if (home.Length == 0) home = "/";
            return WslPath.IsSafeLinuxPath(home) ? home : null;
        }
        return null;
    }
}

/// <summary>Conversions between WSL share paths and the Linux paths they stand for.</summary>
/// <remarks>Pure string operations, so the rules are the same whichever OS runs the tests.</remarks>
public static class WslPath
{
    private static readonly string[] Hosts = ["wsl.localhost", "wsl$"];

    public static bool IsValidDistributionName(string? name) =>
        name is { Length: > 0 and <= 64 } &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>An absolute Linux path with no <c>..</c> segment or control character.</summary>
    public static bool IsSafeLinuxPath(string? path) =>
        path is { Length: > 0 and <= 1024 } && path[0] == '/' && !path.Any(char.IsControl) &&
        !path.Split('/').Contains("..");

    /// <summary>
    /// Reads <c>\\wsl.localhost\&lt;distro&gt;\rest</c> or <c>\\wsl$\&lt;distro&gt;\rest</c> into the
    /// distribution name and the Linux path <c>/rest</c>.
    /// </summary>
    public static bool TryParse(string? windowsPath, out string distribution, out string linuxPath)
    {
        distribution = "";
        linuxPath = "";
        if (string.IsNullOrWhiteSpace(windowsPath) || windowsPath.Length > 2048 || windowsPath.Any(char.IsControl))
            return false;
        var path = windowsPath.Trim().Replace('/', '\\');
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Hosts.Contains(parts[0], StringComparer.OrdinalIgnoreCase) ||
            !IsValidDistributionName(parts[1]) || parts.Skip(2).Any(part => part is ".." or "."))
            return false;
        distribution = parts[1];
        linuxPath = "/" + string.Join('/', parts.Skip(2));
        return true;
    }

    /// <summary>A Linux path under a distribution's share root, with Windows separators.</summary>
    public static string ToWindows(string root, string linuxPath) =>
        root.TrimEnd('\\') + linuxPath.TrimEnd('/').Replace('/', '\\');
}
