using System.Diagnostics;
using System.Text;
using AgentNotify.Contracts;
using AgentNotify.Core.Domain;
using AgentNotify.Core.Logging;

namespace AgentNotify.Desktop;

/// <summary>Shared text handling for the process-launching notifiers.</summary>
internal static class NotifierText
{
    private const int MaxTitle = 200;
    private const int MaxBody = 1000;

    /// <summary>
    /// Removes control characters and bounds the length. Notification text comes from whatever an
    /// agent posts, so it is treated as hostile before it reaches any external program.
    /// </summary>
    internal static string Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var builder = new StringBuilder(Math.Min(value.Length, max));
        foreach (var character in value)
        {
            if (builder.Length >= max) break;
            // Keep printable text and ordinary spaces; drop newlines, escapes, and terminal control.
            if (char.IsControl(character))
            {
                if (character is '\n' or '\t') builder.Append(' ');
                continue;
            }
            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    internal static string Title(Notification notification)
    {
        var title = Clean(notification.Title, MaxTitle);
        return title.Length == 0 ? "AgentNotify" : title;
    }

    /// <summary>Body text prefixed with the agent and project so several agents stay distinguishable.</summary>
    internal static string Body(Notification notification)
    {
        var message = Clean(notification.Message, MaxBody);
        var agent = Clean(notification.Agent, 60);
        var project = Clean(notification.Project, 60);

        var origin = (agent.Length, project.Length) switch
        {
            (0, 0) => "",
            (_, 0) => agent,
            (0, _) => project,
            _ => $"{agent} · {project}"
        };

        if (origin.Length == 0) return message;
        return message.Length == 0 ? origin : $"{origin}\n{message}";
    }

    /// <summary>Runs a helper process without a shell and reports whether it succeeded.</summary>
    internal static async Task<bool> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info);
        if (process is null) return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return false;
        }
    }

    internal static bool ExistsOnPath(string tool)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory.Trim(), tool))) return true;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return false;
    }
}

/// <summary>Shows notifications through <c>notify-send</c> on Linux and other freedesktop sessions.</summary>
public sealed class NotifySendDesktopNotifier : IDesktopNotifier
{
    private const string Tool = "notify-send";

    public string Name => Tool;

    public bool IsAvailable =>
        !OperatingSystem.IsWindows()
        && !OperatingSystem.IsMacOS()
        && HasDesktopSession
        && NotifierText.ExistsOnPath(Tool);

    /// <summary>
    /// True when a graphical session looks reachable. Without this the broker would try to notify
    /// a display that does not exist, for example over SSH or in a container.
    /// </summary>
    private static bool HasDesktopSession =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public async Task<bool> ShowAsync(
        Notification notification,
        NotificationLifetime lifetime,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var arguments = new List<string>
            {
                "--app-name", "AgentNotify",
                "--urgency", Urgency(notification.Priority, notification.Type),
                // 0 keeps a sticky notification on screen until the user acts on it.
                "--expire-time", (lifetime.IsSticky ? 0 : lifetime.Seconds * 1000).ToString(),
                // Everything after this is positional, so a title starting with "-" is safe.
                "--",
                NotifierText.Title(notification),
                Escape(NotifierText.Body(notification))
            };

            return await NotifierText.RunAsync(Tool, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Urgency(NotificationPriority priority, string type) =>
        priority == NotificationPriority.Critical || NotificationTypes.IsAttention(type)
            ? "critical"
            : priority == NotificationPriority.Low ? "low" : "normal";

    /// <summary>
    /// Escapes the XML entities that a notification server may interpret as Pango markup in the
    /// body. Without this an agent could inject markup into the displayed notification.
    /// </summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>Shows notifications on macOS through <c>terminal-notifier</c> or Notification Center.</summary>
public sealed class MacOsDesktopNotifier : IDesktopNotifier
{
    private const string TerminalNotifier = "terminal-notifier";
    private const string OsaScript = "/usr/bin/osascript";

    public string Name => UseTerminalNotifier ? TerminalNotifier : "osascript";

    public bool IsAvailable => OperatingSystem.IsMacOS() && (UseTerminalNotifier || File.Exists(OsaScript));

    private static bool UseTerminalNotifier =>
        OperatingSystem.IsMacOS() && NotifierText.ExistsOnPath(TerminalNotifier);

    public async Task<bool> ShowAsync(
        Notification notification,
        NotificationLifetime lifetime,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var title = NotifierText.Title(notification);
            var body = NotifierText.Body(notification);

            if (UseTerminalNotifier)
            {
                return await NotifierText.RunAsync(
                    TerminalNotifier,
                    ["-title", title, "-message", body, "-group", "agentnotify"],
                    cancellationToken).ConfigureAwait(false);
            }

            // The AppleScript is a fixed program and the text arrives through argv, so hostile
            // titles or messages cannot terminate a string literal and inject script of their own.
            // Notification Center controls the lifetime; osascript cannot render a sticky banner.
            const string script =
                """
                on run argv
                    display notification (item 1 of argv) with title (item 2 of argv)
                end run
                """;

            return await NotifierText.RunAsync(
                OsaScript,
                ["-e", script, "--", body, title],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Writes notifications to standard output. Used on headless machines, over SSH, and whenever no
/// graphical backend is available, so the broker still surfaces attention requests somewhere.
/// </summary>
public sealed class ConsoleDesktopNotifier : IDesktopNotifier
{
    private readonly TextWriter _writer;

    public ConsoleDesktopNotifier(TextWriter? writer = null) => _writer = writer ?? Console.Out;

    public string Name => "console";

    public bool IsAvailable => true;

    public Task<bool> ShowAsync(
        Notification notification,
        NotificationLifetime lifetime,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var marker = NotificationTypes.IsAttention(notification.Type) ? "!" : "*";
            var origin = NotifierText.Clean(notification.Agent, 60);
            var project = NotifierText.Clean(notification.Project, 60);
            if (project.Length > 0) origin = $"{origin}/{project}";

            _writer.WriteLine(
                $"{marker} [{notification.CreatedAt.ToLocalTime():HH:mm:ss}] {origin} {notification.Type}: " +
                $"{NotifierText.Title(notification)} — {NotifierText.Clean(notification.Message, 1000)}");
            _writer.Flush();
            return Task.FromResult(true);
        }
        catch (Exception)
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>Chooses the best available desktop notification backend for the current session.</summary>
public static class DesktopNotifierFactory
{
    /// <summary>
    /// Returns the first available backend, always falling back to the console notifier so a
    /// notification is never silently dropped.
    /// </summary>
    public static IDesktopNotifier Create(IAppLogger? logger = null)
    {
        foreach (var candidate in Candidates())
        {
            if (!candidate.IsAvailable) continue;
            logger?.Info($"Desktop notifications use the '{candidate.Name}' backend.");
            return candidate;
        }

        logger?.Warn("No desktop notification backend is available; notifications are written to standard output.");
        return new ConsoleDesktopNotifier();
    }

    private static IEnumerable<IDesktopNotifier> Candidates()
    {
        if (OperatingSystem.IsMacOS()) yield return new MacOsDesktopNotifier();
        else if (!OperatingSystem.IsWindows()) yield return new NotifySendDesktopNotifier();
    }
}
