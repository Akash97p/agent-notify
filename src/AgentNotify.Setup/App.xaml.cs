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
            if (!e.Args.Contains("--accept-license", StringComparer.OrdinalIgnoreCase))
            {
                Shutdown(2);
                return;
            }

            try
            {
                var directory = ValueAfter(e.Args, "--install-dir") ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "AgentNotify");
                var options = new InstallOptions(
                    InstallerService.ValidateInstallDirectory(directory),
                    StartWithWindows: !e.Args.Contains("--no-startup", StringComparer.OrdinalIgnoreCase),
                    DesktopShortcut: e.Args.Contains("--desktop-shortcut", StringComparer.OrdinalIgnoreCase));
                await InstallerService.InstallAsync(options, new Progress<InstallProgress>());
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
