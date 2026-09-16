using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using AgentNotify.Core.Skills;
using AgentNotify.Core.Wsl;

namespace AgentNotify.App;

/// <summary>
/// One agent's line on the Install tab: where its skill would go, whether it is
/// already there, and what pressing the button would do.
/// </summary>
/// <remarks>
/// The state is recomputed from disk rather than remembered, because the folder
/// belongs to another program. An agent updated in the background, or a skill
/// edited by hand, must not leave this list quietly claiming otherwise — so
/// <see cref="Refresh"/> runs whenever the tab is shown and after every install.
/// </remarks>
public sealed class SkillInstallRow : INotifyPropertyChanged
{
    private readonly Func<AgentSkillTarget, IReadOnlyList<SkillInstaller.SkillFile>> _files;
    private string? _skillsRoot;
    private SkillInstallState _state = SkillInstallState.NotInstalled;
    private string _status = "";

    /// <param name="wsl">The WSL home this row installs into, or null for this Windows user's own agents.</param>
    public SkillInstallRow(
        AgentSkillTarget target,
        Func<AgentSkillTarget, IReadOnlyList<SkillInstaller.SkillFile>> files,
        WslHome? wsl = null)
    {
        Target = target;
        Wsl = wsl;
        _files = files;
        if (target.HasDefaultLocation)
        {
            try
            {
                _skillsRoot = AgentSkillCatalog.DefaultSkillsRoot(target, homeDirectory: wsl?.WindowsHome);
            }
            catch (InvalidOperationException)
            {
                // No home directory. The row stays, without a destination, and
                // the Choose button is the way forward.
            }
        }
        Refresh();
    }

    public AgentSkillTarget Target { get; }
    public WslHome? Wsl { get; }
    public string DisplayName => Wsl is null ? Target.DisplayName : $"{Target.DisplayName} · WSL {Wsl.Distribution}";
    public string Note => Target.Note;

    /// <summary>The skills root, once one is known. Null until a folder is chosen.</summary>
    public string? SkillsRoot
    {
        get => _skillsRoot;
        private set
        {
            _skillsRoot = value;
            Notify();
            Notify(nameof(Destination));
            Notify(nameof(CanInstall));
        }
    }

    /// <summary>The folder the files actually land in, shown to the operator.</summary>
    public string Destination =>
        _skillsRoot is null ? "No folder chosen yet" : SkillInstaller.SkillDirectory(_skillsRoot);

    public SkillInstallState State
    {
        get => _state;
        private set
        {
            _state = value;
            Notify();
            Notify(nameof(ActionLabel));
        }
    }

    /// <summary>Result of the last install, or what is already on disk.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Notify();
        }
    }

    public bool CanInstall => _skillsRoot is not null;

    /// <summary>Names what the button does, so it never says "Install" over a reinstall.</summary>
    public string ActionLabel => State switch
    {
        SkillInstallState.UpToDate => "Reinstall",
        SkillInstallState.Outdated => "Update",
        _ => "Install"
    };

    /// <summary>Points this row at a folder the person picked themselves.</summary>
    public void UseFolder(string skillsRoot)
    {
        SkillsRoot = skillsRoot;
        Refresh();
    }

    public void Refresh()
    {
        if (_skillsRoot is null)
        {
            State = SkillInstallState.NotInstalled;
            Status = "";
            return;
        }

        try
        {
            State = SkillInstaller.Inspect(_skillsRoot, _files(Target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            State = SkillInstallState.NotInstalled;
            Status = $"Could not read that folder: {ex.Message}";
            return;
        }

        Status = State switch
        {
            SkillInstallState.UpToDate => "Installed and up to date.",
            SkillInstallState.Outdated => "A different version is installed.",
            _ => ""
        };
    }

    /// <summary>
    /// Writes the skill. Only replaces an existing, differing file when
    /// <paramref name="force"/> is set — the caller asks first.
    /// </summary>
    public SkillInstallResult Install(bool force)
    {
        if (_skillsRoot is null)
            return new SkillInstallResult(false, false, "", "Choose a folder first.");

        try
        {
            var result = SkillInstaller.Install(DisplayName, _skillsRoot, _files(Target), force, dryRun: false);
            Status = result.Message;
            Refresh();
            // Refresh overwrites Status with what is on disk, which for a
            // successful install says less than the install's own message.
            if (result.Changed) Status = result.Message;
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status = $"Could not install: {ex.Message}";
            return new SkillInstallResult(false, false, _skillsRoot, Status);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
