using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace AgentNotify.Setup;

public partial class SetupWindow : Window
{
    private bool _installing;
    private bool _installed;
    private bool _finishActionsRun;
    private string? _installedDirectory;

    public SetupWindow()
    {
        InitializeComponent();
        Icon = BitmapFrameFromResource();
        VersionText.Text = $"Version {InstallerService.ProductVersion} · Kabani Tech Private Limited";
        InstallPath.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "AgentNotify");
    }

    private static System.Windows.Media.ImageSource BitmapFrameFromResource()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/an.ico"))
            ?? throw new InvalidOperationException("Installer icon resource is missing.");
        using var stream = resource.Stream;
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (!_installing)
            Close();
    }

    private void OnAcceptanceChanged(object sender, RoutedEventArgs e) =>
        InstallButton.IsEnabled = AcceptTerms.IsChecked == true && !_installing;

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where AgentNotify will be installed",
            InitialDirectory = Directory.Exists(InstallPath.Text) ? InstallPath.Text : null
        };
        if (dialog.ShowDialog(this) == true)
        {
            InstallPath.Text = string.Equals(Path.GetFileName(dialog.FolderName), "AgentNotify", StringComparison.OrdinalIgnoreCase)
                ? dialog.FolderName
                : Path.Combine(dialog.FolderName, "AgentNotify");
        }
    }

    private void OnAuthor(object sender, RoutedEventArgs e) => Open("https://github.com/Akash97p");

    private void OnLicense(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "AgentNotify-MIT-License.txt");
            File.WriteAllText(path, InstallerService.ReadTextResource("Payload.LICENSE.txt"));
            Open(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open license", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_installed)
        {
            Close();
            return;
        }
        if (AcceptTerms.IsChecked != true)
            return;

        string installDirectory;
        try { installDirectory = InstallerService.ValidateInstallDirectory(InstallPath.Text); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid install location", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var running = Process.GetProcessesByName("AgentNotify.Tray")
            .Concat(Process.GetProcessesByName("AgentNotify"))
            .ToArray();
        if (running.Length > 0)
        {
            var answer = MessageBox.Show(this,
                "AgentNotify is currently running. Setup must close it before continuing. Continue?",
                "AgentNotify is running", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;
            foreach (var process in running)
            {
                try { process.Kill(); process.WaitForExit(5000); }
                catch { }
                finally { process.Dispose(); }
            }
        }

        _installing = true;
        InstallButton.IsEnabled = false;
        InstallPath.IsEnabled = false;
        Progress.Value = 3;
        StatusText.Text = "Preparing installation…";

        var progress = new Progress<InstallProgress>(p =>
        {
            Progress.Value = p.Percent;
            StatusText.Text = p.Message;
        });

        try
        {
            var options = new InstallOptions(
                installDirectory,
                StartWithWindows.IsChecked == true,
                DesktopShortcut.IsChecked == true);
            await InstallerService.InstallAsync(options, progress);
            _installedDirectory = installDirectory;
            _installed = true;
            Progress.Value = 100;
            StatusText.Text = "AgentNotify installed successfully.";
            InstallButton.Content = "Finish";
            InstallButton.IsEnabled = true;
            AcceptTerms.IsEnabled = false;
        }
        catch (Exception ex)
        {
            Progress.Value = 0;
            StatusText.Text = "Installation failed.";
            InstallButton.IsEnabled = true;
            InstallPath.IsEnabled = true;
            MessageBox.Show(this, ex.Message, "Installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _installing = false;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_installing)
        {
            e.Cancel = true;
            return;
        }
        RunFinishActions();
        base.OnClosing(e);
    }

    private void RunFinishActions()
    {
        if (!_installed || _finishActionsRun || _installedDirectory is null)
            return;
        _finishActionsRun = true;

        if (LaunchAfterInstall.IsChecked == true)
            Open(Path.Combine(_installedDirectory, "AgentNotify.Tray.exe"));
        if (OpenGuide.IsChecked == true)
            Open(Path.Combine(_installedDirectory, "GettingStarted.html"));
    }

    private static void Open(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
