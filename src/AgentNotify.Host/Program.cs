using System.Reflection;
using AgentNotify.Core;
using AgentNotify.Core.Config;

namespace AgentNotify.Host;

/// <summary>
/// Entry point for <c>agentnotifyd</c>, the headless AgentNotify broker used on macOS and Linux.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = HostOptions.Parse(args, out var error);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Run 'agentnotifyd --help' for usage.");
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"agentnotifyd {Version}");
            return 0;
        }

        var configDir = options.ConfigDir ?? ConfigStore.DefaultConfigDir();
        UnixFilePermissions.CreateOwnerOnlyDirectory(configDir);

        using var singleInstance = SingleInstanceLock.TryAcquire(configDir);
        if (singleInstance is null)
        {
            Console.Error.WriteLine(
                $"AgentNotify is already running for this user (lock: {SingleInstanceLock.PathFor(configDir)}).");
            return 1;
        }

        using var shutdown = new CancellationTokenSource();
        // The registrations must stay referenced for the lifetime of the process. A discarded
        // PosixSignalRegistration is finalized, which silently unhooks the handler and leaves the
        // daemon unable to react to SIGTERM at all.
        using var signals = ShutdownSignals.Register(shutdown);

        BrokerRuntime runtime;
        try
        {
            runtime = await BrokerRuntime.StartAsync(
                configDir,
                options.Port,
                desktopNotifications: !options.NoDesktop,
                shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Startup failures must not leave a half-initialized broker behind.
            Console.Error.WriteLine($"AgentNotify failed to start: {exception.Message}");
            return 1;
        }

        var exitCode = 0;
        {
            Console.WriteLine($"AgentNotify broker listening on {runtime.Url}");
            Console.WriteLine($"  secrets      : {runtime.Protection.Description}");
            Console.WriteLine($"  notifications: {runtime.NotifierName}");
            if (!runtime.Protection.IsUserBound)
            {
                Console.WriteLine(
                    "  warning      : no platform keyring was found, so provider credentials are only " +
                    "as protected as the owner-only key file.");
            }
            Console.WriteLine("Press Ctrl+C to stop.");

            try
            {
                await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Stopping…");
            }

            // A service manager expects the process to be gone shortly after SIGTERM. If a shutdown
            // step wedges, report it and exit rather than becoming a daemon that cannot be stopped;
            // an interrupted delivery is recovered from the outbox on the next start.
            try
            {
                await runtime.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("AgentNotify did not shut down cleanly within 15 seconds; exiting.");
                exitCode = 1;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"AgentNotify shutdown error: {exception.Message}");
                exitCode = 1;
            }
        }

        return exitCode;
    }

    private static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private static void PrintUsage() => Console.WriteLine(
        """
        agentnotifyd — the AgentNotify broker for macOS and Linux.

        Runs the loopback notification API, the SQLite history, and the outbound delivery
        dispatcher, and shows notifications on the current desktop session. Agents talk to it
        with the same 'agentnotify' CLI and the same /v1 API used on Windows.

        Usage:
          agentnotifyd [options]

        Options:
          --port <n>          Listen on this loopback port instead of the configured one.
          --config-dir <path> Use a different per-user data directory.
          --no-desktop        Print notifications to standard output instead of the desktop.
          --version           Print the version and exit.
          --help, -h          Print this help and exit.
        """);
}

/// <summary>Parsed command-line options for the host.</summary>
internal sealed record HostOptions
{
    internal int? Port { get; init; }
    internal string? ConfigDir { get; init; }
    internal bool NoDesktop { get; init; }
    internal bool ShowHelp { get; init; }
    internal bool ShowVersion { get; init; }

    internal static HostOptions Parse(string[] args, out string? error)
    {
        error = null;
        var options = new HostOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    return options with { ShowHelp = true };

                case "--version":
                    return options with { ShowVersion = true };

                case "--no-desktop":
                    options = options with { NoDesktop = true };
                    break;

                case "--port":
                    if (i + 1 >= args.Length)
                    {
                        error = "--port requires a value.";
                        return options;
                    }

                    if (!int.TryParse(args[++i], out var port) || port is < 1 or > 65535)
                    {
                        error = "--port must be a number between 1 and 65535.";
                        return options;
                    }

                    options = options with { Port = port };
                    break;

                case "--config-dir":
                    if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
                    {
                        error = "--config-dir requires a path.";
                        return options;
                    }

                    options = options with { ConfigDir = args[++i] };
                    break;

                default:
                    error = $"Unknown option '{args[i]}'.";
                    return options;
            }
        }

        return options;
    }
}

/// <summary>
/// Prevents two brokers from serving one per-user data directory.
/// </summary>
/// <remarks>
/// The Windows tray app uses a named mutex, which has no portable cross-process equivalent. An
/// exclusively held lock file works on every platform and is released by the operating system even
/// if the process is killed, so a crash cannot leave the broker permanently unstartable.
/// </remarks>
internal sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private SingleInstanceLock(FileStream stream) => _stream = stream;

    internal static string PathFor(string configDir) => Path.Combine(configDir, "agentnotifyd.lock");

    internal static SingleInstanceLock? TryAcquire(string configDir)
    {
        try
        {
            var stream = new FileStream(
                PathFor(configDir),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            try
            {
                stream.SetLength(0);
                using var writer = new StreamWriter(stream, leaveOpen: true);
                writer.Write(Environment.ProcessId);
                writer.Flush();
                stream.Flush();
            }
            catch (IOException)
            {
                // The PID is a diagnostic convenience; holding the lock is what matters.
            }

            return new SingleInstanceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
    }
}
