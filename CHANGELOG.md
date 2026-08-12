# Changelog

All notable changes to AgentNotify are recorded here. This project is a prerelease; the mature
`1.0.0` milestone is intentionally reserved and has not been reached. Versions follow the scheme in
[docs/RELEASING.md](docs/RELEASING.md), and any tag containing a hyphen is published as a GitHub
prerelease.

The release workflow reads the section for the tagged version out of this file and uses it as the
release description, so each entry should be written for someone deciding whether to install the
build.

## [0.0.3-alpha.1] - 2026-08-12

Fixes a crash that could close the application, and adds an About section.

### Fixed

- **Selecting a saved provider in Settings closed the application.** `JsonElement.TryGetInt32`
  throws rather than returning false for any value that is not a number, including JSON `null`, and
  optional provider settings are stored as `null` when left blank. A Telegram provider saved
  without a topic ID therefore threw when its row was selected, and because that handler runs
  outside the panel's error boundary the exception terminated the tray process — taking the broker
  and its local API with it. The same pattern affected eight settings across SMTP, Telegram,
  Pushover, Twilio SMS, Twilio WhatsApp, and MQTT.
- **A successful provider test reported an error.** Test send saves, sends, then reloads the
  provider. The reload hit the same defect, so the failure appeared after the message had already
  been delivered.

### Added

- **About section.** A new About tab in Settings shows the application icon, name, version, what
  AgentNotify is, a local-first summary, links to the repository, documentation, releases and
  issues, the per-user data directory, and publisher, licence and unsigned-binary notes. The tray
  menu gained an "About AgentNotify" entry that opens it.
- **`docs/BUG.md`**, a bug log recording the cause, fix and lesson for each defect found so far.

### Changed

- Documentation no longer contains machine-specific filesystem paths, and every version reference
  is aligned with the released version.

## [0.0.2-alpha.1] - 2026-08-12

AgentNotify now runs on macOS and Linux, not just Windows.

### Added

- **Headless broker for macOS and Linux (`agentnotifyd`).** Runs the same broker the Windows tray
  process hosts — configuration, SQLite history, the durable delivery outbox, and the loopback
  `/v1` API — without a desktop UI framework. Agents cannot tell the two apart: the CLI, the bearer
  token, and the API contract are identical on every platform.
- **Desktop notifications on macOS and Linux.** `notify-send` on Linux, `terminal-notifier` or
  Notification Center on macOS, and a console fallback so a notification is never silently dropped
  when no graphical session exists, such as over SSH.
- **Per-platform provider secret protection.** Windows continues to use DPAPI and never falls back.
  macOS stores the key in the login keychain, Linux in the Secret Service keyring via `secret-tool`,
  and either falls back to an owner-only key file with an explicit startup warning.
- **Self-contained binaries for five runtimes** — `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`,
  and `osx-arm64` — published as archives with SHA-256 checksums, alongside the Windows installer.
- **`install.sh` for macOS and Linux**, which verifies the published checksum before installing and
  refuses to install anything it cannot verify.
- **Continuous integration on Linux and macOS runners**, including a broker smoke test that
  exercises the API, keyed deduplication, owner-only file permissions, and `SIGTERM` shutdown.
- **New documentation**: a CLI reference, a configuration reference, a troubleshooting guide, a
  macOS/Linux installation guide, and an index at `docs/README.md`.

### Fixed

- **Local state could be written to the working directory on macOS and Linux.** On Unix,
  `Environment.GetFolderPath` returns an empty string when the base directory does not exist yet —
  the normal state of a fresh account — which produced a relative path. The first run could
  therefore write `config.json`, containing the local bearer token, plus the secret key and the
  history database into whatever directory the broker was started from. The data directory is now
  always resolved to an absolute path.
- **`agentnotifyd` ignored `SIGTERM`.** The signal registrations were discarded and finalized, which
  unhooked the handler; the broker neither shut down nor exited and could only be stopped with
  `SIGKILL`, making it unmanageable under systemd or launchd.
- **Shutdown could hang indefinitely** on an unbounded delivery-dispatcher stop. Every shutdown step
  is now bounded, and a second signal exits immediately.
- **Configured sound file names were not sanitized consistently across platforms.** `Path.GetFileName`
  treats a backslash as an ordinary character on Unix, so a Windows-style path in `config.json`
  survived unchanged there. Since configuration is portable between machines, normalization is now
  identical on every platform.

### Security

- Local state on Unix is created owner-only: the data directory `0700`, and `config.json`,
  `agentnotify.db`, and `secret.key` `0600`. `config.json` holds the local bearer token.
- Desktop notification backends launch helper processes with an argument list rather than through a
  shell, and the macOS AppleScript is a fixed program that receives notification text through `argv`,
  so agent-supplied titles and messages cannot be interpreted as commands or script.

### Known limitations

- macOS and Linux have no tray icon, notification center window, or Settings UI. Configure the
  broker through `config.json` and use the CLI.
- Neither graphical notification backend has been confirmed to display a notification on real
  hardware, and the Linux `secret-tool` path and the ARM64 binaries have not been executed.
  See [docs/VERIFICATION.md](docs/VERIFICATION.md) for exactly what has and has not been observed.
- Binaries remain unsigned. Windows may show a SmartScreen prompt, and macOS requires clearing the
  quarantine attribute on first run.

## [0.0.1-alpha.1] - 2026-08-12

First published prerelease.

### Added

- Windows tray application with custom non-activating toasts, a notification center, and a native
  Settings window.
- Loopback-only, bearer-authenticated `/v1` API and a self-contained `agentnotify` CLI for Windows
  and WSL.
- SQLite history with deduplication keys, configurable retention, and local logging.
- Eighteen opt-in outbound delivery adapters behind a durable outbox, with DPAPI-encrypted
  credentials.
- Four built-in notification tones and managed WAV/MP3 sound import.
- A single self-contained `AgentNotifySetup.exe` per-user installer with an offline getting-started
  page.

[0.0.3-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.0.3-alpha.1
[0.0.2-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.0.2-alpha.1
[0.0.1-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.0.1-alpha.1
