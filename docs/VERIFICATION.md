# Verification record

Date: 2026-08-12
Environment: Windows 11 host, WSL workspace, Windows .NET SDK 10.0.302 at `/mnt/d/dev/dotnet/dotnet.exe`, x64 publish target.

This record distinguishes automated/process verification from visual checks. No result from the inherited `/mnt/d/dev/AgentNotify` documentation was accepted without rerunning it.

## Verified

### Release build

Command:

```bash
./scripts/build.sh --no-restore
```

Result:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

All seven projects built: Contracts, Core, API, WPF App, CLI, Setup, and Tests.

### Automated tests

Command:

```bash
./scripts/test.sh --no-restore
```

Result:

```text
Passed: 87
Failed: 0
Skipped: 0
```

Coverage includes validation, lifecycle transitions, config recovery/round-trip, SQLite CRUD/query/prune, authentication, health, create/list/get/patch/dismiss, malformed/null JSON, callback isolation, keyed deduplication including concurrent calls, CLI connection failure, bare `--unresolved`, hyphenated notification type parsing, invalid type rejection, and version output.

### Skill

`distribution/agentnotify` was initialized using the official skill initializer. `quick_validate.py` reports:

```text
Skill is valid!
```

The installed `SKILL.md` was also checked for its expected `name: agentnotify` metadata.

### Packaging

Command:

```bash
./scripts/package.sh
```

Result:

- `artifacts/AgentNotifySetup.exe`: approximately 186 MB, one self-contained file.
- Embedded tray payload: approximately 85 MB.
- Embedded CLI payload: approximately 38 MB.
- Installer payload/resource validation passed.

Latest locally packaged artifact SHA-256:

```text
f91873f47b2ad7ccde92698aeab8e59219e2a6dff30dd6429a1d79f760724a83  AgentNotifySetup.exe
```

Regenerate the checksum after any rebuild because it necessarily changes with the binary.

The portable `scripts/package.ps1` implementation and WSL wrapper generated a matching `artifacts/SHA256SUMS.txt`. The static Pages source built locally into `_site`; GitHub-hosted workflows cannot be execution-tested until the owner chooses to push the local repository.

Windows version-resource inspection confirmed for setup, tray, and CLI:

```text
Product: AgentNotify
Company: Kabani Tech Private Limited
File version: 1.0.0.0
```

Authenticode status is `NotSigned`, as documented.

### Published tray/API/CLI smoke

The actual packaged `AgentNotify.Tray.exe` and `agentnotify.exe` were copied to a temporary directory on the Windows `D:` filesystem and executed. Verified:

- tray/API process started;
- authenticated `health` returned version `1.0.0`, API `v1`, and the running PID;
- `input-required` CLI spelling was accepted and serialized as `input_required`;
- create returned an active notification ID;
- a second create with the same key returned the same ID and updated content;
- `list --unresolved --agent smoke-test` returned the row;
- unauthenticated `/v1/health` returned HTTP `401`;
- malformed JSON returned HTTP `400`;
- resolving the ID removed it from the unresolved query; and
- launching the tray binary again left exactly one `AgentNotify.Tray.exe` process.

The smoke notification was resolved, the process was stopped, and the temporary binaries were removed.

Direct Linux `curl` in this WSL configuration could not reach the Windows loopback listener; Windows `curl.exe` and the Windows CLI succeeded. That is a WSL networking environment detail, not an API bind failure.

### Installer window smoke

The packaged installer was launched from the Windows `D:` filesystem. Process inspection confirmed:

```text
Main window title: Install AgentNotify
Main window handle: nonzero
Responding: true
```

The process was then stopped without installing, and the temporary copy was removed.

### Silent install/uninstall smoke

The packaged installer was run with explicit license acceptance into a temporary `D:` directory:

```text
--silent --accept-license --install-dir <temporary>\AgentNotify --no-startup
```

Verified installed outputs:

- `AgentNotify.Tray.exe`
- `agentnotify.exe`
- generated `GettingStarted.html` with no unreplaced skill placeholder
- valid `SKILL.md`
- `LICENSE.txt`
- `uninstall.ps1`

The installed CLI returned `agentnotify 1.0.0`. The Windows uninstall registry entry reported AgentNotify, version 1.0.0, and publisher Kabani Tech Private Limited. Running the registered uninstall script removed the known files and uninstall registration. The temporary test directory was then removed.

## Verified by implementation and compilation, not visually inspected

- Tray menu includes Notification Center, Getting Started, Copy/Download skill, Pause, Start with Windows, logs, and Exit.
- The supplied multi-resolution `an.ico` is compiled into app/setup resources and executable icon metadata.
- Toast HWND receives `WS_EX_NOACTIVATE`; XAML also sets `ShowActivated=false`, `Focusable=false`, and `Topmost=true`.
- Toast manager queues overflow and uses foreground-monitor work area with DPI conversion.
- Dashboard status changes feed back into the toast manager.
- Active attention rows are queried and restored at startup.
- Normal setup finish launches the tray app and offline guide when the default checkboxes remain enabled.
- Native Settings window opens from the tray and validates port, retention, stack size, placement, lifetimes, pause/DND, and the initial sound toggle.

These were code-reviewed and built, but the automation did not capture the user’s desktop because doing so could expose unrelated private screen content.

## Remaining human checks

- Inspect toast/dashboard/installer visual layout at 100%, 150%, and 200% scaling.
- Verify toast position with taskbars on each edge and mixed-DPI multi-monitor arrangements.
- Type continuously in an editor while toasts arrive and confirm focus/caret never moves.
- Click every tray/menu/toast/dashboard action in a clean installed profile.
- Complete a normal interactive install, confirm the browser guide opens on Finish, sign out/in to test startup, then uninstall through Windows Settings.
- Verify screen-reader naming, keyboard navigation, contrast, and reduced-motion behavior.
- Repeat install/uninstall under a second clean Windows user.

## Release blocker for public distribution

The app is functional but unsigned. Obtain an Authenticode certificate for Kabani Tech Private Limited, sign the two payload binaries before embedding them, sign the final installer last, timestamp all signatures, and publish a fresh SHA-256 checksum.
