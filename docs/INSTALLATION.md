# Installation and packaging

This document covers the **Windows 11 x64** build: the installer, the tray application, and the
packaging pipeline that produces them. There is no equivalent graphical build for macOS or Linux;
those platforms install the CLI and the headless `agentnotifyd` broker instead, described in
[INSTALLATION_UNIX.md](INSTALLATION_UNIX.md).

## End-user installation

Run `AgentNotifySetup.exe`. Setup is per-user and does not require administrator rights for the default location.

Default install directory:

```text
%LOCALAPPDATA%\Programs\AgentNotify
```

Installed files:

```text
AgentNotify.Tray.exe
agentnotify.exe
GettingStarted.html
SKILL.md
LICENSE.txt
THIRD_PARTY_NOTICES.txt
uninstall.ps1
```

Setup adds the install directory to the current user’s `PATH`, creates Start menu shortcuts, optionally creates a desktop shortcut, and registers:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AgentNotify
HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentNotify
```

The default finish actions launch the tray app and open `GettingStarted.html` in the default browser. The page works offline and embeds the full skill text for its Copy and Download buttons.

For managed deployment and verification, setup supports a license-gated silent mode:

```powershell
AgentNotifySetup.exe --silent --accept-license --install-dir "D:\Apps\AgentNotify" --no-startup
```

`--accept-license` is mandatory with `--silent` for a first install. Optional switches are `--no-startup` and `--desktop-shortcut`. A silent first install does not launch the app or browser guide.

## Updating

Run the newer `AgentNotifySetup.exe`; there is no need to quit AgentNotify first. Setup finds the
existing installation through its uninstall registration (`InstallLocation` and `DisplayVersion`,
provided `AgentNotify.Tray.exe` is still in that folder) and switches to an update:

- it shows the installed and new versions, keeps the install folder, and keeps the current
  **Start when I sign in** and desktop-shortcut choices;
- it does not ask for the MIT License again — that was accepted at install — though the licence
  can still be opened;
- **Update AgentNotify** asks the running tray to exit through the named event
  `Local\AgentNotify.Exit.v1`, which runs the same clean shutdown as the tray's **Exit**; a tray
  that has not exited after ten seconds (including one from a build without that event) is ended;
- a running `agentnotify.exe` is not stopped, because an agent may be waiting on
  `interactions wait`. Setup renames the in-use file to `agentnotify.exe.<id>.old`, puts the new
  file in its place, and deletes leftovers on the next install or uninstall;
- the tray is started again when setup finishes, and the getting-started guide is not reopened.

Silent updates need no licence switch:

```powershell
AgentNotifySetup.exe --silent
```

With an existing installation, `--silent` keeps its folder and choices (an explicit `--install-dir`,
`--no-startup`, or `--desktop-shortcut` still wins), stops the tray, and starts it again afterwards if
it was running. Add `--no-launch` to leave it stopped.

## Uninstall

Use Windows Settings → Apps → Installed apps → AgentNotify → Uninstall. The uninstall script removes known application files, shortcuts, startup registration, uninstall registration, and the user `PATH` entry. Unknown files in the install directory are not intentionally deleted.

Runtime data under `%LOCALAPPDATA%\AgentNotify` is preserved. Delete that directory manually only when the user intentionally wants to remove the token, configuration, history, generated guide, and logs.

## Build from WSL

Use a Windows .NET 10 SDK. The current workspace uses:

```text
/mnt/d/dev/dotnet/dotnet.exe
```

Build and test:

```bash
cd /path/to/agent-notify
./scripts/build.sh
./scripts/test.sh
```

Package:

```bash
./scripts/package.sh
```

The packaging pipeline publishes three x64 self-contained single-file executables. The tray and CLI binaries become embedded resources in setup; only this file needs distribution:

```text
artifacts/AgentNotifySetup.exe
```

## Release checklist

1. Update version metadata in `Directory.Build.props` and setup registration.
2. Run `./scripts/build.sh` and require zero errors/warnings.
3. Run `./scripts/test.sh` and record the exact total.
4. Validate `distribution/agentnotify` with the skill validator.
5. Run `./scripts/package.sh` from a clean artifact directory.
6. Inspect version/company/product metadata on all three binaries.
7. Launch the packaged tray app and exercise health, create, list, dedup, resolve, and second-instance behavior.
8. Launch setup and inspect layout/license text on standard and high-DPI Windows displays.
9. Install into a clean Windows user profile; verify PATH, startup, Start menu, browser guide, skill actions, and uninstall.
10. Authenticode-sign the installer and embedded binaries for a public release, then verify signatures and timestamping.
11. Publish checksums alongside the signed installer.

## Signing

Assembly publisher metadata does not establish a trusted Windows publisher. Public distribution should sign `AgentNotify.Tray.exe`, `agentnotify.exe`, and the final `AgentNotifySetup.exe` using a certificate issued to Kabani Tech Private Limited. Signing must occur in the packaging sequence so the setup resource contains the signed payloads, and the final installer must then be signed last.

The artifact created in this development workspace is unsigned.
