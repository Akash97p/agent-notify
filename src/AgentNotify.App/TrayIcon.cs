using System.Diagnostics;
using System.Drawing;
using System.IO;
using WinForms = System.Windows.Forms;

namespace AgentNotify.App;

/// <summary>WinForms <see cref="WinForms.NotifyIcon"/> driven from the WPF dispatcher thread.
/// Keep a strong reference to the icon so it is not collected.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _centerItem;
    private readonly WinForms.ToolStripMenuItem _pauseItem;
    private readonly WinForms.ToolStripMenuItem _startupItem;
    private Action? _onShowCenter;
    private Action? _onExit;
    private Func<bool>? _isPaused;
    private Func<bool>? _isStartupEnabled;

    public TrayIcon(Icon icon, string logsDir,
        Func<bool> isPaused, Func<bool> isStartupEnabled,
        Action onShowCenter, Action onOpenSettings, Action onOpenGettingStarted, Action onCopySkill, Action onSaveSkill,
        Action onTogglePause, Action onToggleStartup, Action onOpenAbout, Action onExit)
    {
        _isPaused = isPaused;
        _isStartupEnabled = isStartupEnabled;
        _onShowCenter = onShowCenter;
        _onExit = onExit;

        _centerItem = new WinForms.ToolStripMenuItem("Notification Center", null, (_, _) => onShowCenter());
        _centerItem.Font = new Font(_centerItem.Font, FontStyle.Bold);

        _pauseItem = new WinForms.ToolStripMenuItem("Pause notifications", null, (_, _) =>
        {
            onTogglePause();
            RefreshChecks();
        });
        _startupItem = new WinForms.ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            onToggleStartup();
            RefreshChecks();
        });

        var openLogs = new WinForms.ToolStripMenuItem("Open log folder", null, (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(logsDir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logsDir}\"") { UseShellExecute = true });
            }
            catch { }
        });
        var gettingStarted = new WinForms.ToolStripMenuItem("Getting started", null, (_, _) => onOpenGettingStarted());
        var settings = new WinForms.ToolStripMenuItem("Settings…", null, (_, _) => onOpenSettings());
        var copySkill = new WinForms.ToolStripMenuItem("Copy agent SKILL.md", null, (_, _) => onCopySkill());
        var saveSkill = new WinForms.ToolStripMenuItem("Download agent SKILL.md…", null, (_, _) => onSaveSkill());
        var about = new WinForms.ToolStripMenuItem("About AgentNotify", null, (_, _) => onOpenAbout());
        var exit = new WinForms.ToolStripMenuItem("Exit", null, (_, _) => onExit());

        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.AddRange([_centerItem, settings, new WinForms.ToolStripSeparator(),
            gettingStarted, copySkill, saveSkill, new WinForms.ToolStripSeparator(),
            _pauseItem, _startupItem, new WinForms.ToolStripSeparator(), openLogs, new WinForms.ToolStripSeparator(), about, exit]);
        _menu.Opening += (_, _) => RefreshChecks();

        _icon = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "AgentNotify",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                onShowCenter();
        };
        RefreshChecks();
    }

    private void RefreshChecks()
    {
        _pauseItem.Checked = _isPaused?.Invoke() ?? false;
        _startupItem.Checked = _isStartupEnabled?.Invoke() ?? false;
    }

    public void UpdateIcon(Icon icon)
    {
        _icon.Icon = icon;
    }

    public void ShowMessage(string title, string message, WinForms.ToolTipIcon type = WinForms.ToolTipIcon.Info)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = type;
        _icon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
