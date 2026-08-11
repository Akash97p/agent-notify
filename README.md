# AgentNotify

AgentNotify is a local Windows human-attention broker for autonomous coding agents. Agents send a small authenticated request; AgentNotify displays a dedicated non-activating WPF toast, preserves the event in local history, and makes unresolved requests visible from the system tray.

It is designed for people running several agents across terminals, repositories, windows, and virtual desktops who need a reliable answer to: “Which agents are waiting for me?”

## What you get

- Custom Windows 11 toast windows owned by AgentNotify, not browser notifications.
- Sticky `input_required`, `permission_required`, `blocked`, and `error` attention requests.
- Auto-expiring informational, warning, success, and completion notifications.
- Centralized multi-toast stacking on the monitor where the user is working.
- A compact notification center with “Needs attention” and recent history.
- A system tray icon that keeps the broker alive when windows close.
- A loopback-only, bearer-authenticated REST API at `127.0.0.1:47821`.
- A self-contained `agentnotify.exe` CLI for Windows and WSL-based agents.
- SQLite history, deduplication keys, configurable retention, and local logs.
- Single-instance behavior and per-user startup registration.
- Tray actions to copy or save the distributable `SKILL.md`.
- One self-contained installer executable with an offline getting-started page.

## Install

The distributable is [AgentNotifySetup.exe](artifacts/AgentNotifySetup.exe). Copy that one file to a Windows 11 machine and run it; no separate .NET runtime is required.

Setup installs per user by default under `%LOCALAPPDATA%\Programs\AgentNotify`, then:

1. installs `AgentNotify.Tray.exe` and `agentnotify.exe`;
2. adds the CLI directory to the user `PATH`;
3. creates Start menu shortcuts;
4. registers AgentNotify to start at sign-in, unless unchecked;
5. registers a Windows “Installed apps” uninstall entry;
6. starts the tray application; and
7. opens the offline getting-started HTML page in the default browser when setup closes.

The installer displays the MIT License and the explicit “as is, no warranty” notice before installation. This build contains publisher metadata for **Kabani Tech Private Limited**, but it is not Authenticode-signed. Windows may therefore show an unknown-publisher/SmartScreen prompt until a code-signing certificate is used.

After installation, open a new PowerShell, Command Prompt, or WSL shell so the updated Windows user `PATH` is visible.

## Send your first notification

```powershell
agentnotify.exe send `
  --agent codex `
  --project AgentNotify `
  --type input_required `
  --priority high `
  --key AgentNotify-decision `
  --title "Need your decision" `
  --message "Should I use option A or option B?"
```

From Bash or WSL:

```bash
agentnotify.exe send \
  --agent codex \
  --project AgentNotify \
  --type input_required \
  --priority high \
  --key AgentNotify-decision \
  --title "Need your decision" \
  --message "Should I use option A or option B?"
```

The CLI prints the created notification, including its `id`. Clear a resolved request with:

```bash
agentnotify.exe resolve NOTIFICATION_ID
```

Useful commands:

```bash
agentnotify.exe health
agentnotify.exe list --unresolved
agentnotify.exe get NOTIFICATION_ID
agentnotify.exe dismiss NOTIFICATION_ID
agentnotify.exe "Build complete" "All tests passed" --type completed
```

Both underscore and hyphen spellings are accepted by the CLI, such as `input_required` and `input-required`.

## Give the skill to a coding agent

Right-click the AgentNotify tray icon and choose either **Copy agent SKILL.md** or **Download agent SKILL.md…**. The post-install getting-started page has the same Copy and Download buttons.

For Codex, place the file at:

```text
~/.codex/skills/agentnotify/SKILL.md
```

The source skill is at [distribution/agentnotify/SKILL.md](distribution/agentnotify/SKILL.md). It tells an agent when to notify, how to avoid notification spam, how to use stable deduplication keys, and how to resolve an attention request.

## Notification model

Types:

| Type | Default toast behavior | Intended use |
|---|---:|---|
| `input_required` | Sticky | A user decision or answer blocks progress |
| `permission_required` | Sticky | Explicit authorization is required |
| `blocked` | Sticky | A prerequisite or external dependency prevents progress |
| `error` | Sticky attention item | A significant operation failed |
| `warning` | 12 seconds | Attention is advisable but work can continue |
| `info` | 7 seconds | A meaningful non-actionable milestone |
| `success` | 5 seconds | An important operation succeeded |
| `completed` | 5 seconds | The requested work finished |

Priorities are `low`, `normal`, `high`, and `critical`. Status values are `active`, `dismissed`, and `resolved`.

An optional `key` identifies one logical unresolved condition. A new active notification with the same key updates the existing row and toast instead of producing duplicates. Keyed creation is serialized inside the broker so concurrent agents cannot create duplicate active rows.

## Tray behavior

The tray menu provides:

- Notification Center
- Getting started
- Copy agent SKILL.md
- Download agent SKILL.md…
- Pause notifications
- Start with Windows
- Open log folder
- Exit

Closing the notification center only hides it. AgentNotify exits only from the tray menu. A second launch signals the existing process to show the center instead of opening another API listener.

## Architecture

```text
Coding agent / CLI
        |
        | HTTP + local bearer token
        v
ASP.NET Core Minimal API (127.0.0.1 only)
        |
        v
NotificationService ---- SQLite repository
        |
        +---- ToastStackManager ---- WPF toast windows
        |
        +---- Notification Center
        |
        +---- WinForms NotifyIcon tray
```

Project layout:

```text
src/AgentNotify.Contracts   JSON contracts and enums
src/AgentNotify.Core        domain rules, config, persistence, logging
src/AgentNotify.Api         authenticated loopback Minimal API
src/AgentNotify.App         WPF tray app, toasts, center, startup/single instance
src/AgentNotify.Cli         self-contained command-line client
src/AgentNotify.Setup       self-contained per-user WPF installer
tests/AgentNotify.Tests     xUnit domain, persistence, API, auth, and CLI tests
distribution/agentnotify   validated agent skill
assets                     offline getting-started HTML template
docs                       detailed reference and verification notes
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for lifecycle and trust-boundary details.

## Local data and configuration

Runtime state stays under `%LOCALAPPDATA%\AgentNotify`:

```text
config.json                 port, random bearer token, UI/runtime options
agentnotify.db              SQLite notification history
logs/agentnotify-YYYYMMDD.log
resources/SKILL.md
resources/GettingStarted.html
```

Selected `config.json` defaults:

| JSON key | Default |
|---|---:|
| `port` | `47821` |
| `toastLocation` | `BottomRight` |
| `maxVisibleToasts` | `5` |
| `historyRetentionDays` | `30` |
| `pauseNotifications` | `false` |
| `maxRequestBodyBytes` | `65536` |
| `rateLimitPerSecond` | `30` |

`authToken` is generated with 256 bits of randomness on first launch. The CLI reads it automatically. `AGENTNOTIFY_PORT` and `AGENTNOTIFY_TOKEN` can override discovery for debugging, but agents should not print or transmit the token.

Uninstall removes application binaries, shortcuts, startup registration, and the CLI `PATH` entry. It intentionally preserves `%LOCALAPPDATA%\AgentNotify` history/config so an upgrade or reinstall does not destroy user data.

## Security and privacy

- Kestrel binds only to `127.0.0.1`, never `0.0.0.0`.
- Every `/v1/*` route requires the per-user bearer token.
- Token comparison uses SHA-256 and fixed-time byte comparison.
- Request bodies, fields, metadata size, and create rate are bounded.
- Notification content, database rows, and logs remain local; there is no telemetry or cloud service.
- API-to-UI callbacks are isolated so a rendering failure cannot make a persisted API request fail.
- The unauthenticated `/health` endpoint exposes only `{"status":"ok"}`.

Read [SECURITY.md](SECURITY.md) before proposing network transports or external delivery channels.

## Build and test from WSL

WPF must be built with a Windows .NET SDK, not Linux `dotnet`. This workspace uses .NET SDK 10.0.302 at `/mnt/d/dev/dotnet/dotnet.exe`.

```bash
cd /home/akash/projects/agent-notify
./scripts/build.sh
./scripts/test.sh
```

Override the SDK path when necessary:

```bash
AGENTNOTIFY_DOTNET_EXE=/mnt/d/tools/dotnet/dotnet.exe ./scripts/build.sh
```

The current release build completes with zero warnings. The test suite has 87 passing tests.

## Build the single-file installer

```bash
./scripts/package.sh
```

This publishes self-contained single-file Windows binaries for the tray app and CLI, embeds both in a self-contained WPF setup executable, validates the skill, and creates:

```text
artifacts/AgentNotifySetup.exe
```

The artifact is intentionally ignored by Git. See [docs/INSTALLATION.md](docs/INSTALLATION.md) for packaging internals, signing guidance, and release checks.

## API

The versioned API is documented in [docs/API.md](docs/API.md). The primary create route is:

```http
POST http://127.0.0.1:47821/v1/notifications
Authorization: Bearer LOCAL_TOKEN
Content-Type: application/json
```

```json
{
  "agent": "codex",
  "agentInstance": "agent-3",
  "project": "payments",
  "type": "input_required",
  "priority": "high",
  "key": "payments-schema-decision",
  "title": "Need schema decision",
  "message": "Choose normalized or denormalized storage.",
  "cwd": "D:\\dev\\payments"
}
```

## Current limitations

- The binaries and installer are x64 Windows builds.
- The installer is not yet Authenticode-signed.
- “Open Agent” cannot reliably focus a specific Windows Terminal tab or cross virtual desktops yet.
- There is no settings UI; advanced settings are edited in `config.json` while the app is stopped.
- External delivery providers are roadmap items and are not present in this local-only release.

## Roadmap

The transport design will keep the local broker as the source of truth and add opt-in delivery adapters. Planned directions include:

- Email through user-configured SMTP or provider APIs.
- WhatsApp through the official WhatsApp Business Cloud API.
- Microsoft Teams, Slack, Discord, and generic webhooks.
- SMS and mobile push through explicitly configured providers.
- Quiet hours, schedules, snooze, escalation, grouping, and per-project rules.
- Response buttons and acknowledgements back to the waiting agent.
- Agent heartbeat/status, richer SDKs, and an optional MCP server.
- Safer terminal/tab activation and virtual-desktop awareness.
- ARM64 packages, signed releases, automatic updates, and migration tooling.

External channels will be disabled by default and must add provider-specific secret storage, consent, redaction, retry, cost-control, and rate-limit policies. See [docs/ROADMAP.md](docs/ROADMAP.md).

## Contributing and license

Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting changes. AgentNotify is available under the permissive [MIT License](LICENSE).

Publisher: **Kabani Tech Private Limited**
Author: **Akash P** — [github.com/Akash97p](https://github.com/Akash97p)

The software is provided “as is”, without warranty of any kind, as stated in the license.
