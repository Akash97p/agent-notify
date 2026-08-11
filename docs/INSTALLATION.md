# Installation and packaging

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

`--accept-license` is mandatory with `--silent`. Optional switches are `--no-startup` and `--desktop-shortcut`. Silent mode does not launch the app or browser guide.

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
cd /home/akash/projects/agent-notify
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
