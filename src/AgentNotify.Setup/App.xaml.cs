using System.Diagnostics;
using System.IO;
using System.Windows;

namespace AgentNotify.Setup;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase))
        {
            // The licence was accepted when AgentNotify was first installed; an update does not ask again.
            var existing = InstallerService.FindExisting();
            if (existing is null && !e.Args.Contains("--accept-license", StringComparer.OrdinalIgnoreCase))
            {
                Shutdown(2);
                return;
            }

            try
            {
                var directory = ValueAfter(e.Args, "--install-dir") ?? existing?.Directory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "AgentNotify");
                var options = new InstallOptions(
                    InstallerService.ValidateInstallDirectory(directory),
                    StartWithWindows: !e.Args.Contains("--no-startup", StringComparer.OrdinalIgnoreCase) &&
                        (existing?.StartWithWindows ?? true),
                    DesktopShortcut: e.Args.Contains("--desktop-shortcut", StringComparer.OrdinalIgnoreCase) ||
                        existing?.DesktopShortcut == true);
                var running = RunningAgentNotify.Detect();
                await running.StopAsync();
                await InstallerService.InstallAsync(options, new Progress<InstallProgress>());
                if (running.WasRunning && !e.Args.Contains("--no-launch", StringComparer.OrdinalIgnoreCase))
                {
                    // The update itself succeeded; a tray that fails to start is not a setup failure.
                    try
                    {
                        Process.Start(new ProcessStartInfo(Path.Combine(options.InstallDirectory, "AgentNotify.Tray.exe"))
                            { UseShellExecute = true });
                    }
                    catch (Exception) { }
                }
                Shutdown(0);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "AgentNotifySetup-silent.log"), ex.ToString());
                }
                catch { }
                Shutdown(1);
            }
            return;
        }

        MainWindow = new SetupWindow();
        MainWindow.Show();
    }

    private static string? ValueAfter(string[] args, string option)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (string.Equals(args[i], option, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
