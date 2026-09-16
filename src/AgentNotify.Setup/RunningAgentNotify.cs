using System.Diagnostics;

namespace AgentNotify.Setup;

/// <summary>
/// The AgentNotify tray processes in this Windows session, and how setup stops them.
/// </summary>
/// <remarks>
/// The tray is asked to exit through <see cref="ExitEventName"/>, which runs the same shutdown as
/// its Exit menu item: Kestrel, the delivery dispatcher, and SQLite close cleanly. A tray that does
/// not answer — including one from a build that predates the event — is killed afterwards.
/// <para>
/// <c>agentnotify.exe</c> is deliberately left alone. Agents can be blocked in
/// <c>interactions wait</c>; setup renames that file out of the way instead of killing them.
/// </para>
/// </remarks>
internal sealed class RunningAgentNotify
{
    /// <summary>Created by the tray's single-instance owner. Keep in sync with AgentNotify.App.</summary>
    public const string ExitEventName = "Local\\AgentNotify.Exit.v1";

    private static readonly string[] ProcessNames = ["AgentNotify.Tray", "AgentNotify"];

    private RunningAgentNotify(bool wasRunning) => WasRunning = wasRunning;

    /// <summary>Whether a tray was running when this snapshot was taken.</summary>
    public bool WasRunning { get; }

    public static RunningAgentNotify Detect()
    {
        var processes = Find();
        try { return new RunningAgentNotify(processes.Length > 0); }
        finally { DisposeAll(processes); }
    }

    public Task StopAsync() => Task.Run(Stop);

    private static void Stop()
    {
        var processes = Find();
        try
        {
            if (processes.Length == 0) return;

            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exit))
            {
                using (exit)
                {
                    try { exit.Set(); }
                    catch (ObjectDisposedException) { }
                }
                WaitForExit(processes, TimeSpan.FromSeconds(10));
            }

            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(5000);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Already gone, or not ours to stop. File replacement reports anything that matters.
                }
            }
        }
        finally { DisposeAll(processes); }
    }

    private static void WaitForExit(Process[] processes, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        foreach (var process in processes)
        {
            var remaining = timeout - deadline.Elapsed;
            if (remaining <= TimeSpan.Zero) return;
            try { process.WaitForExit(remaining); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    private static Process[] Find()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var result = new List<Process>();
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                bool ours;
                try { ours = process.SessionId == session; }
                catch (InvalidOperationException) { ours = false; }
                if (ours) result.Add(process);
                else process.Dispose();
            }
        }
        return result.ToArray();
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes) process.Dispose();
    }
}
