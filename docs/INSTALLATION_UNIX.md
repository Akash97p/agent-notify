# Installing on macOS and Linux

Windows gets the tray application and a single-file installer. macOS and Linux get the same broker
without a graphical tray: a background process called `agentnotifyd`, plus the same `agentnotify`
command-line client that agents already use. The API, the bearer token, the notification model, and
`SKILL.md` are identical on all three platforms, so an agent written against the Windows build needs
no changes.

> **Status.** These builds are part of the `0.0.1-alpha.1` prerelease. The broker itself has been
> run and exercised end to end on Linux, but the macOS build and the graphical notification backends
> have not been run on real hardware yet. See [VERIFICATION.md](VERIFICATION.md) for exactly what
> has and has not been observed.

## Install

```sh
curl -fsSL https://raw.githubusercontent.com/Akash97p/agent-notify/main/scripts/install.sh | sh
```

The script detects your platform, downloads the matching archive from GitHub Releases, **verifies
its SHA-256 against the published checksum file**, and installs both binaries into `~/.local/bin`.
It refuses to install anything it cannot verify.

To install elsewhere or pin a version:

```sh
AGENTNOTIFY_PREFIX=/usr/local/bin AGENTNOTIFY_VERSION=v0.0.1-alpha.1 sh install.sh
```

### Manual install

Download the archive for your platform from the
[releases page](https://github.com/Akash97p/agent-notify/releases), check it against
`SHA256SUMS.txt`, then:

```sh
tar -xzf agentnotify-linux-x64.tar.gz
install -m 0755 agentnotify-linux-x64/agentnotify  ~/.local/bin/
install -m 0755 agentnotify-linux-x64/agentnotifyd ~/.local/bin/
```

Supported archives: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, and `win-x64` for a portable
Windows copy without the installer.

macOS marks downloaded binaries with a quarantine attribute. These builds are not yet notarized, so
the first run needs:

```sh
xattr -d com.apple.quarantine ~/.local/bin/agentnotify ~/.local/bin/agentnotifyd
```

## Run the broker

```sh
agentnotifyd
```

It prints the address it listens on, how provider secrets are protected, and which notification
backend it selected:

```text
AgentNotify broker listening on http://127.0.0.1:47821
  secrets      : Secret Service keyring via secret-tool
  notifications: notify-send
Press Ctrl+C to stop.
```

Options:

| Option | Effect |
| --- | --- |
| `--port <n>` | Listen on a different loopback port |
| `--config-dir <path>` | Use a different per-user data directory |
| `--no-desktop` | Print notifications to standard output instead of the desktop |
| `--version`, `--help` | Print version or usage and exit |

Then, from any shell:

```sh
agentnotify health
agentnotify send --agent codex --project payments --type input_required \
  --key payments-decision --title "Need a decision" --message "Normalized or denormalized?"
```

## Run it in the background

### Linux (systemd user service)

Create `~/.config/systemd/user/agentnotify.service`:

```ini
[Unit]
Description=AgentNotify broker
After=graphical-session.target

[Service]
ExecStart=%h/.local/bin/agentnotifyd
Restart=on-failure

[Install]
WantedBy=default.target
```

```sh
systemctl --user daemon-reload
systemctl --user enable --now agentnotify
```

`agentnotifyd` stops cleanly on `SIGTERM`, so `systemctl --user stop agentnotify` shuts the broker
down rather than killing it.

### macOS (launchd agent)

Create `~/Library/LaunchAgents/dev.agentnotify.broker.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>dev.agentnotify.broker</string>
  <key>ProgramArguments</key>
  <array><string>/Users/YOUR_USER/.local/bin/agentnotifyd</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
</dict>
</plist>
```

```sh
launchctl load ~/Library/LaunchAgents/dev.agentnotify.broker.plist
```

## Desktop notifications

| Platform | Backend | Requirement |
| --- | --- | --- |
| Linux | `notify-send` | Install `libnotify-bin` (Debian/Ubuntu) or `libnotify` (Fedora/Arch), and run inside a graphical session |
| macOS | `terminal-notifier` if installed, otherwise Notification Center via `osascript` | None; `terminal-notifier` gives better grouping |
| Any | console | Automatic fallback when no graphical session is available, for example over SSH |

The console fallback means a notification is never silently dropped: if nothing can display it, the
broker writes it to standard output instead. `osascript` cannot render a notification that stays on
screen, so on macOS sticky attention types behave like ordinary banners; the entry still stays
active in history until it is resolved.

## Where your data lives

| Path | Contents |
| --- | --- |
| `$XDG_DATA_HOME/AgentNotify` or `~/.local/share/AgentNotify` | Everything below |
| `config.json` | Settings **and the local bearer token** |
| `agentnotify.db` | Notification history |
| `secret.key` | Present only when no keyring is available |
| `logs/` | Daily log files |
| `agentnotifyd.lock` | Single-instance lock |

The directory is created `0700` and those files `0600`. Do not copy or commit `config.json`: anyone
holding the token can post notifications to your broker.

## Provider credential protection

Outbound channels are opt-in and disabled until you configure them. When you do, credentials are
encrypted before they reach SQLite:

- **macOS** — AES-GCM under a key kept in your login keychain.
- **Linux** — AES-GCM under a key kept in the Secret Service keyring, via `secret-tool`
  (`libsecret-tools` on Debian/Ubuntu, `libsecret` elsewhere).
- **No keyring available** — AES-GCM under an owner-only `secret.key` file. The broker warns about
  this at startup. It is weaker: any process running as you can read that file.

Installing `secret-tool` before configuring providers gets you the stronger option on Linux. See
[SECURITY.md](../SECURITY.md).

## What is missing compared with Windows

There is no tray icon, no notification center window, and no Settings UI yet. Configure the broker by
editing `config.json` (see [CONFIGURATION.md](CONFIGURATION.md)) and restarting it, and use the CLI
to list, resolve, and dismiss notifications. Native macOS and Linux desktop clients are planned;
see [CROSS_PLATFORM.md](CROSS_PLATFORM.md).

## Troubleshooting

Most symptoms and fixes are shared with Windows and live in
[TROUBLESHOOTING.md](TROUBLESHOOTING.md). Platform-specific ones:

| Symptom | Cause | Fix |
| --- | --- | --- |
| `AgentNotify is already running for this user` | Another `agentnotifyd` holds the lock | `agentnotify health`; stop the other instance, or pass a different `--config-dir` |
| Broker starts but nothing appears on screen | No `notify-send`, or no graphical session | Install `libnotify-bin`; over SSH the console fallback is expected |
| `notifications: console` on a desktop machine | `DISPLAY`/`WAYLAND_DISPLAY` not visible to the service | Ensure the user service inherits the graphical session environment |
| Startup warns about the key file | No keyring found | Install and unlock `secret-tool`, then re-enter provider credentials |
| macOS refuses to run the binary | Quarantine on an unsigned download | `xattr -d com.apple.quarantine <path>` |
