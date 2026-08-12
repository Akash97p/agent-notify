using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AgentNotify.Setup;

internal sealed record InstallOptions(string InstallDirectory, bool StartWithWindows, bool DesktopShortcut);
internal sealed record InstallProgress(int Percent, string Message);

internal static class InstallerService
{
    public static string ProductVersion =>
        typeof(InstallerService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(InstallerService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.1";

    private const string ProductKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentNotify";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EnvironmentKey = @"Environment";
    private static readonly string[] InstalledFiles =
        ["AgentNotify.Tray.exe", "agentnotify.exe", "SKILL.md", "GettingStarted.html", "LICENSE.txt", "THIRD_PARTY_NOTICES.txt", "uninstall.ps1"];

    public static string ValidateInstallDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Choose an install location.");
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        var root = Path.GetPathRoot(full);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("AgentNotify cannot be installed directly into a drive root.");
        if (!string.Equals(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)), "AgentNotify", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The install directory must end with an AgentNotify folder.");
        return full.TrimEnd(Path.DirectorySeparatorChar);
    }

    public static async Task InstallAsync(InstallOptions options, IProgress<InstallProgress> progress)
    {
        await Task.Run(() => Install(options, progress));
    }

    private static void Install(InstallOptions options, IProgress<InstallProgress> progress)
    {
        var directory = ValidateInstallDirectory(options.InstallDirectory);
        Directory.CreateDirectory(directory);

        progress.Report(new(12, "Installing AgentNotify…"));
        WriteResourceAtomically("Payload.AgentNotify.Tray.exe", Path.Combine(directory, "AgentNotify.Tray.exe"));
        progress.Report(new(33, "Installing command-line client…"));
        WriteResourceAtomically("Payload.agentnotify.exe", Path.Combine(directory, "agentnotify.exe"));

        var skill = ReadTextResource("Payload.SKILL.md");
        var template = ReadTextResource("Payload.GettingStarted.html");
        var html = template.Replace("__AGENTNOTIFY_SKILL_JSON__", JsonSerializer.Serialize(skill), StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), skill, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "GettingStarted.html"), html, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "LICENSE.txt"), ReadTextResource("Payload.LICENSE.txt"), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "THIRD_PARTY_NOTICES.txt"), ReadTextResource("Payload.THIRD_PARTY_NOTICES.txt"), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "uninstall.ps1"), UninstallScript, new UTF8Encoding(false));

        progress.Report(new(58, "Creating shortcuts and command path…"));
        CreateShortcuts(directory, options.DesktopShortcut);
        AddToUserPath(directory);
        SetStartup(options.StartWithWindows, Path.Combine(directory, "AgentNotify.Tray.exe"));

        progress.Report(new(78, "Registering AgentNotify with Windows…"));
        RegisterUninstaller(directory);
        BroadcastEnvironmentChange();
        progress.Report(new(96, "Finalizing installation…"));
    }

    public static string ReadTextResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Installer resource '{name}' is missing. Rebuild the setup package.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] ReadBinaryResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Installer payload '{name}' is missing. Rebuild the setup package.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void WriteResourceAtomically(string resourceName, string destination)
    {
        var temporary = destination + ".installing";
        File.WriteAllBytes(temporary, ReadBinaryResource(resourceName));
        File.Move(temporary, destination, overwrite: true);
    }

    private static void SetStartup(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue("AgentNotify", $"\"{executable}\"");
        else
            key.DeleteValue("AgentNotify", throwOnMissingValue: false);
    }

    private static void RegisterUninstaller(string directory)
    {
        var uninstallScript = Path.Combine(directory, "uninstall.ps1");
        var uninstall = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{uninstallScript}\" -InstallDir \"{directory}\"";
        using var key = Registry.CurrentUser.CreateSubKey(ProductKey);
        key.SetValue("DisplayName", "AgentNotify");
        key.SetValue("DisplayVersion", ProductVersion);
        key.SetValue("Publisher", "Kabani Tech Private Limited");
        key.SetValue("DisplayIcon", Path.Combine(directory, "AgentNotify.Tray.exe"));
        key.SetValue("InstallLocation", directory);
        key.SetValue("URLInfoAbout", "https://github.com/Akash97p/AgentNotify");
        key.SetValue("UninstallString", uninstall);
        key.SetValue("QuietUninstallString", uninstall);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var size = InstalledFiles.Select(f => new FileInfo(Path.Combine(directory, f))).Where(f => f.Exists).Sum(f => f.Length) / 1024;
        key.SetValue("EstimatedSize", (int)Math.Min(size, int.MaxValue), RegistryValueKind.DWord);
    }

    private static void AddToUserPath(string directory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey);
        var current = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!parts.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add(directory);
            key.SetValue("Path", string.Join(';', parts), RegistryValueKind.ExpandString);
        }
    }

    private static void CreateShortcuts(string directory, bool desktopShortcut)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var startFolder = Path.Combine(programs, "AgentNotify");
        Directory.CreateDirectory(startFolder);
        var executable = Path.Combine(directory, "AgentNotify.Tray.exe");
        CreateShortcut(Path.Combine(startFolder, "AgentNotify.lnk"), executable, "--show-center", directory);
        CreateShortcut(Path.Combine(startFolder, "Getting Started.lnk"), Path.Combine(directory, "GettingStarted.html"), "", directory);

        var desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AgentNotify.lnk");
        if (desktopShortcut)
            CreateShortcut(desktopPath, executable, "--show-center", directory);
        else if (File.Exists(desktopPath))
            File.Delete(desktopPath);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable; could not create shortcuts.");
        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            shellObject = Activator.CreateInstance(shellType);
            dynamic shell = shellObject!;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = targetPath;
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = workingDirectory;
            shortcut.IconLocation = Path.Combine(workingDirectory, "AgentNotify.Tray.exe") + ",0";
            shortcut.Description = "AgentNotify — human attention broker for coding agents";
            shortcut.Save();
        }
        finally
        {
            if (shortcutObject is not null && Marshal.IsComObject(shortcutObject)) Marshal.FinalReleaseComObject(shortcutObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject)) Marshal.FinalReleaseComObject(shellObject);
        }
    }

    private static void BroadcastEnvironmentChange()
    {
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    private const string UninstallScript = """
        param([Parameter(Mandatory=$true)][string]$InstallDir)
        $ErrorActionPreference = 'SilentlyContinue'
        Get-Process -Name 'AgentNotify.Tray','AgentNotify' | Stop-Process -Force
        Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AgentNotify'
        Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentNotify' -Recurse -Force
        $envKey = 'HKCU:\Environment'
        $userPath = (Get-ItemProperty -Path $envKey -Name Path).Path
        if ($null -ne $userPath) {
          $newPath = (($userPath -split ';') | Where-Object { $_ -and $_.TrimEnd('\') -ine $InstallDir.TrimEnd('\') }) -join ';'
          Set-ItemProperty -Path $envKey -Name Path -Value $newPath -Type ExpandString
        }
        $startFolder = Join-Path ([Environment]::GetFolderPath('Programs')) 'AgentNotify'
        Remove-Item -Path $startFolder -Recurse -Force
        Remove-Item -Path (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'AgentNotify.lnk') -Force
        @('AgentNotify.Tray.exe','agentnotify.exe','SKILL.md','GettingStarted.html','LICENSE.txt','THIRD_PARTY_NOTICES.txt') | ForEach-Object {
          Remove-Item -LiteralPath (Join-Path $InstallDir $_) -Force
        }
        $self = $MyInvocation.MyCommand.Path
        Start-Process -WindowStyle Hidden -FilePath 'cmd.exe' -ArgumentList '/c', "ping 127.0.0.1 -n 2 > nul & del /f /q `"$self`" & rmdir `"$InstallDir`" 2>nul"
        """;
}
