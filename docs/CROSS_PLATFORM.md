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
- A full native macOS notification center/settings application or a native Linux tray UI. The
  shipped macOS quota status item is deliberately smaller; the portable broker and OS notifications
  remain the primary desktop path on macOS and Linux.
- Apple notarization and the Mac App Store. Unsigned binaries are acceptable for a developer tool
  at this stage; an Apple Developer account can wait for adoption.

---

## Historical starting point

The three blockers below describe the state before the portable broker work. Phases 1–3 are now
done; the WebUI gives macOS and Linux users browser-based configuration, questions, history,
usage, and quota. macOS additionally has a native quota-only status item; Linux has no tray.
See [WEB_UI.md](WEB_UI.md).

Already portable — these target `net10.0` with no Windows-only API use:

| Project | Role |
| --- | --- |
| `AgentNotify.Protocol` | Native DTOs, type IDs, JSON rules, ARC model/schema |
| `AgentNotify.Core` | Domain, validation, config, SQLite, logging, delivery adapters |
| `AgentNotify.Api` | Loopback Minimal API host |
| `AgentNotify.Cli` | `agentnotify` command-line client |
| `AgentNotify.Tests` | Automated coverage |

Windows-only by design — these target `net10.0-windows` and use WPF/WinForms:

| Project | Role |
| --- | --- |
| `AgentNotify.App` | Tray process, toasts, notification center, Settings |
| `AgentNotify.Setup` | Per-user installer |

The three things that originally blocked non-Windows use:

1. The broker only exists inside the WPF tray process. There is no headless host, so on
   macOS or Linux nothing starts the API, the repository, or the delivery dispatcher.
2. Provider secrets are protected with Windows DPAPI. `AesGcmSecretProtector` exists but takes an
   injected key and is used only by tests; there is no production key source on other platforms.
3. There is no desktop notification path other than WPF toast windows.

Everything else — SQLite, the outbox, all nineteen delivery adapters, the API, the CLI — is
expected to work unchanged.

---

## Phase 1 — Portable broker (**done**)

### 1.1 Shared channel adapter list

`App.xaml.cs` constructs all nineteen adapters inline. Move that construction into
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

The .NET CLI and broker still cross-compile, but an archive containing `agentnotify-menubar` must
be packaged on macOS because Swift/AppKit is not built on Linux. The portable projects also build and
test on Linux and macOS CI runners; the macOS job compiles and strictly verifies the native executable
before release packaging.

*Done when:* one macOS run of the script produces every archive plus
`SHA256SUMS-portable.txt`, CI builds and tests the portable projects on Linux and macOS, and the
release workflow attaches them.

## Phase 4 — Distribution (**partly done**)

- GitHub Releases as the primary channel for every platform. **Done** — the release workflow attaches every archive plus `SHA256SUMS.txt`.
- A POSIX `install.sh` that downloads, verifies the checksum, and installs into `~/.local/bin`. **Done** — it refuses to install anything it cannot verify.
- A Homebrew tap pointing at the release archives. **Planned**; no Apple Developer account is required for it.
- A Winget manifest for Windows. **Planned**.

## Phase 5 — Native desktop clients (**partly done**)

**Done:** a native Swift/AppKit macOS client shows one status item per selected Codex/Claude account,
with its provider mark and five-hour balance, and lists every monitored account/window. The broker
owns its lifecycle; Live quota in the WebUI configures enabled state, refresh interval, and menu-bar
accounts. The child receives only the loopback port and normalized quota projection.

**Planned:** a full macOS notification center/settings client and a Linux tray client, both built on
the same portable broker. Contributors welcome; the earlier phases keep this possible without a
rewrite.

---

## Verification honesty

The first portable-broker verification used Windows with WSL and had no Linux desktop session or
macOS host. On 2026-09-04 the released `osx-x64` archive ran on owner Intel hardware. Since then,
the development environment has also used that Intel Mac for live broker and WebUI checks; the
dated evidence is in [VERIFICATION.md](VERIFICATION.md). What can and cannot be claimed:

WSL turned out to be more useful than expected: it is a real Linux x64 userland, so a
`linux-x64` self-contained publish of `agentnotifyd` and `agentnotify` runs natively there. That
made it possible to verify the Linux broker end to end rather than only compile it.

| Claim | Status |
| --- | --- |
| Portable projects compile for win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64 | **Verified**; macOS archives are now assembled on macOS |
| Portable logic is correct | **Verified** by the automated test suite |
| The headless broker runs on Linux and macOS, serves `/v1`, and the CLI drives it | **Verified** in WSL and on both CI runners |
| Owner-only `0600`/`0700` local state on Unix | **Verified** in WSL on real files |
| The key-file fallback and its warning | **Verified** in WSL |
| Single-instance locking and clean `SIGTERM` shutdown | **Verified** in WSL |
| `notify-send` actually displays a notification | **Unverified** — needs a graphical Linux session |
| macOS `osascript` notifier actually displays a notification | **Verified** 2026-09-04 on owner Intel Mac hardware (banner seen on screen; see [VERIFICATION.md](VERIFICATION.md)) |
| macOS `terminal-notifier` backend | **Unverified** — not installed on the verification machine |
| AgentNotify Relay channel on macOS | **Verified end to end** 2026-09-10 — live discovery, browser approval, protected credential save, catch-all routing, encryption, first-attempt envelope acceptance (`201`), and mobile display all succeeded |
| macOS Keychain key store | **Verified** on the macOS CI runner |
| Linux `secret-tool` key store | **Unverified** — not installed on the CI runners; Linux exercises the key-file fallback |
| Native macOS quota menu bar builds/signs | **Verified** for x86_64 and arm64 targets; the UI has not been visually observed |
| ARM64 binaries execute | **Unverified** — no ARM64 machine |

Running the Linux binary is what found three defects that no amount of cross-compilation would
have surfaced; see `docs/VERIFICATION.md`. The remaining unverified rows are the reason Linux and
macOS CI jobs are part of Phase 3 rather than an optional extra.

---

## Positioning

AgentNotify is the local attention layer for coding agents across Windows, macOS, and Linux. The
broker, CLI, API, and browser interface run on all three. macOS also has native quota status;
full notification-center/toast settings remain Windows-only and Linux has no tray. Some optional
agent harnesses invoke Python or a host's own runtime, but the broker, WebUI, and macOS status item
need neither Node nor Python at runtime.
