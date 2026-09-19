using System.Diagnostics;
using AgentNotify.Core.Config;
using AgentNotify.Core.Logging;

namespace AgentNotify.Host;

/// <summary>
/// Keeps the native AppKit status item aligned with the owner-controlled configuration. The child
/// receives only the loopback port; it never receives or reads the broker bearer token.
/// </summary>
internal sealed class MacMenuBarController : IDisposable
{
    private readonly AgentNotifyConfig _config;
    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private Process? _process;
    private AppliedSettings? _appliedSettings;
    private bool _missingLogged;
    private bool _disposed;

    internal MacMenuBarController(AgentNotifyConfig config, IAppLogger logger)
    {
        _config = config;
        _logger = logger;
    }

    internal void Apply()
    {
        if (!OperatingSystem.IsMacOS()) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_config.MacMenuBar.Enabled)
            {
                StopLocked();
                _appliedSettings = null;
                return;
            }

            var desired = AppliedSettings.From(_config);
            if (_process is { HasExited: false } && desired == _appliedSettings) return;
            StopLocked();
            StartLocked(desired);
        }
    }

    private void StartLocked(AppliedSettings desired)
    {
        var executable = ExecutablePath();
        if (executable is null)
        {
            if (!_missingLogged)
            {
                _missingLogged = true;
                _logger.Warn("macOS menu bar is enabled, but agentnotify-menubar is not installed beside agentnotifyd.");
            }
            return;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--port");
            start.ArgumentList.Add(_config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += ChildExited;
            process.Start();
            _process = process;
            _appliedSettings = desired;
            _logger.Info($"macOS quota menu bar started: pid={process.Id}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.Warn($"macOS quota menu bar could not start: {exception.Message}");
        }
    }

    private void ChildExited(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_process, sender))
            {
                _process?.Dispose();
                _process = null;
                _appliedSettings = null;
            }
        }
    }

    private static string? ExecutablePath()
    {
        var processDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        foreach (var directory in new[] { AppContext.BaseDirectory, processDirectory }
                     .Where(directory => !string.IsNullOrWhiteSpace(directory))
                     .Distinct(StringComparer.Ordinal))
        {
            var candidate = Path.Combine(directory!, "agentnotify-menubar");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private void StopLocked()
    {
        if (_process is null) return;
        try
        {
            _process.Exited -= ChildExited;
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.Warn($"macOS quota menu bar could not stop cleanly: {exception.Message}");
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _appliedSettings = null;
        }
    }

    private sealed record AppliedSettings(int Port, int RefreshMinutes, string AccountIds)
    {
        internal static AppliedSettings From(AgentNotifyConfig config) => new(
            config.Port,
            config.MacMenuBar.RefreshMinutes,
            string.Join("\n", config.MacMenuBar.AccountIds));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }
    }
}
