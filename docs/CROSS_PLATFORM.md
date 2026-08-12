# Cross-platform plan

AgentNotify should become the common human-attention layer for coding agents on every desktop a
developer uses, not only Windows. This document is the implementation plan: what has to change,
in what order, and what "done" means for each step.

Status keys used below: **done**, **in progress**, **planned**.

---

## Goal

One command, any agent, any channel, any desktop:

```bash
agentnotify send --type input_required --title "Need a decision" --message "A or B?"
```

The agent-facing contract — the CLI, the loopback `/v1` API, the notification model, `SKILL.md` —
must be identical on Windows, macOS, and Linux. Only the desktop presentation layer and the
platform secret store differ.

## Non-goals for this plan

- Rewriting in Rust or Go. .NET 10 is already cross-platform; most of the codebase is portable today.
- A native macOS menu-bar UI or a native Linux tray UI. Those are separate later projects.
  This plan delivers a headless broker plus native OS notifications on macOS and Linux.
- Apple notarization and the Mac App Store. Unsigned binaries are acceptable for a developer tool
  at this stage; an Apple Developer account can wait for adoption.

---

## Starting point

Already portable — these target `net10.0` with no Windows-only API use:

| Project | Role |
| --- | --- |
| `AgentNotify.Contracts` | DTOs, type IDs, JSON rules |
| `AgentNotify.Core` | Domain, validation, config, SQLite, logging, delivery adapters |
| `AgentNotify.Api` | Loopback Minimal API host |
| `AgentNotify.Cli` | `agentnotify` command-line client |
| `AgentNotify.Tests` | Automated coverage |

Windows-only by design — these target `net10.0-windows` and use WPF/WinForms:

| Project | Role |
| --- | --- |
| `AgentNotify.App` | Tray process, toasts, notification center, Settings |
| `AgentNotify.Setup` | Per-user installer |

The three things that actually block non-Windows use:

1. The broker only exists inside the WPF tray process. There is no headless host, so on
   macOS or Linux nothing starts the API, the repository, or the delivery dispatcher.
2. Provider secrets are protected with Windows DPAPI. `AesGcmSecretProtector` exists but takes an
   injected key and is used only by tests; there is no production key source on other platforms.
3. There is no desktop notification path other than WPF toast windows.

Everything else — SQLite, the outbox, all eighteen delivery adapters, the API, the CLI — is
expected to work unchanged.

---

## Phase 1 — Portable broker (**done**)

### 1.1 Shared channel adapter list

`App.xaml.cs` constructs all eighteen adapters inline. Move that construction into
`AgentNotify.Core` so the WPF app and the new headless host cannot drift apart.

*Done when:* a single factory in Core returns the adapter list, `App.xaml.cs` uses it, and the
existing tests still pass unchanged.

### 1.2 Platform secret protection

Select the protector at runtime instead of hard-coding DPAPI:

| Platform | Protection |
| --- | --- |
| Windows | DPAPI, current-user scope (unchanged) |
| macOS | AES-GCM under a 256-bit master key stored in the login Keychain via `/usr/bin/security` |
| Linux | AES-GCM under a 256-bit master key stored in the Secret Service via `secret-tool` when available, otherwise a `0600` key file in the config directory |

The file-backed fallback is weaker than DPAPI or a keyring: any process running as the same user
can read it. It must be reported honestly in `SECURITY.md`, in the app's own diagnostics, and never
be silently selected on Windows.

*Done when:* a factory picks the protector per platform, each key store round-trips a secret, the
Windows path is untouched, and the active protection level is visible to the user.

### 1.3 Unix file permissions

`config.json` holds the bearer token and the SQLite database holds notification history. On Unix
both are created world-readable by default. Set `0600` on the config file, the database, and the
master-key file, and `0700` on the config directory.

*Done when:* files created on Unix are owner-only, and Windows behaviour is unchanged.

### 1.4 Headless host

New `AgentNotify.Host` console project (`net10.0`, binary `agentnotifyd`) that composes config,
logging, SQLite, the delivery dispatcher, the API, and a desktop notifier; handles `SIGINT`/`SIGTERM`;
enforces single-instance with a lock file; and shuts down cleanly.

*Done when:* the host starts the broker, serves the same `/v1` API the CLI already speaks, and stops
without leaving a claimed outbox item.

---

## Phase 2 — Desktop notifiers (**done**, unverified on a real desktop session)

An `IDesktopNotifier` abstraction with one implementation per platform, chosen at runtime:

| Platform | Mechanism | Notes |
| --- | --- | --- |
| Linux | `notify-send` | Priority maps to urgency; sticky types use expiry `0` |
| macOS | `terminal-notifier` when present, otherwise `osascript display notification` | `osascript` cannot render sticky notifications |
| Windows | existing WPF toast stack | Unchanged |
| Any | console fallback | Used on headless machines and over SSH |

All process invocations must pass arguments as a list, never through a shell, and must bound and
sanitize notification text before it reaches an interpreter such as AppleScript.

*Done when:* each notifier escapes hostile titles/messages safely, an unavailable backend degrades to
the console fallback instead of failing a notification, and notifier failure never fails persistence.

---

## Phase 3 — Build and release (**done**)

Publish self-contained single-file binaries for `agentnotify` and `agentnotifyd`:

- `win-x64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

These cross-compile from any host, including the Windows SDK used for the WPF build. The portable
projects must also build and test on a Linux and a macOS CI runner — that, not cross-compilation,
is what proves the code actually runs off Windows.

*Done when:* one script produces every archive plus `SHA256SUMS.txt`, CI builds and tests the
portable projects on Linux and macOS, and the release workflow attaches the archives.

## Phase 4 — Distribution (**partly done**)

- GitHub Releases as the primary channel for every platform. **Done** — the release workflow attaches every archive plus `SHA256SUMS.txt`.
- A POSIX `install.sh` that downloads, verifies the checksum, and installs into `~/.local/bin`. **Done** — it refuses to install anything it cannot verify.
- A Homebrew tap pointing at the release archives. **Planned**; no Apple Developer account is required for it.
- A Winget manifest for Windows. **Planned**.

## Phase 5 — Native desktop clients (**planned**, out of scope here)

A macOS menu-bar client and a Linux tray client with the notification center and Settings UI,
built on the same portable broker. Contributors welcome; the phases above exist to make this
possible without a rewrite.

---

## Verification honesty

The maintainer's development machine is Windows with WSL and has no macOS host, no Linux desktop
session, and no Linux .NET SDK. What can and cannot be claimed:

WSL turned out to be more useful than expected: it is a real Linux x64 userland, so a
`linux-x64` self-contained publish of `agentnotifyd` and `agentnotify` runs natively there. That
made it possible to verify the Linux broker end to end rather than only compile it.

| Claim | Status |
| --- | --- |
| Portable projects compile for win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64 | **Verified** by cross-compilation |
| Portable logic is correct | **Verified** by the automated test suite |
| The headless broker runs on Linux and macOS, serves `/v1`, and the CLI drives it | **Verified** in WSL and on both CI runners |
| Owner-only `0600`/`0700` local state on Unix | **Verified** in WSL on real files |
| The key-file fallback and its warning | **Verified** in WSL |
| Single-instance locking and clean `SIGTERM` shutdown | **Verified** in WSL |
| `notify-send` and macOS notifiers actually display a notification | **Unverified** — needs a graphical Linux session and a Mac |
| macOS Keychain key store | **Verified** on the macOS CI runner |
| Linux `secret-tool` key store | **Unverified** — not installed on the CI runners; Linux exercises the key-file fallback |
| ARM64 binaries execute | **Unverified** — no ARM64 machine |

Running the Linux binary is what found three defects that no amount of cross-compilation would
have surfaced; see `docs/VERIFICATION.md`. The remaining unverified rows are the reason Linux and
macOS CI jobs are part of Phase 3 rather than an optional extra.

---

## Positioning

AgentNotify is not "desktop notifications for one agent on Windows". It is the notification layer
for AI coding agents: agent-agnostic, channel-agnostic, repo-local, no Node or Python runtime, and
cross-platform. The phases above are what turn that description into something true on three
operating systems.
