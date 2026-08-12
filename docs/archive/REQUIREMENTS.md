# AgentNotify — Requirements

## 1. Project Purpose

AgentNotify is a small Windows-native desktop application that acts as a **human-attention
broker for autonomous coding agents**.

Multiple coding agents frequently run at the same time — in different terminals, Windows
Terminal tabs, windows, virtual desktops, and repositories. They routinely block waiting for
user input, approval, decisions, or simply finish tasks while the user is focused elsewhere.

AgentNotify runs in the background on Windows and exposes a tiny localhost HTTP API that
agents can call to raise a **custom toast** on the user's desktop and to persist notification
state, so the user can always answer the question:

> Which agents are waiting for me right now?

## 2. Primary User Scenario

1. A coding agent (e.g. OpenCode running inside WSL) needs a decision.
2. The agent calls `agentnotify.exe send --type input_required --title "Need your decision" ...`.
3. AgentNotify shows a custom, non-focus-stealing toast on the Windows desktop.
4. The toast for `input_required` stays visible until dismissed or resolved.
5. The user can dismiss it, open the Notification Center, or click **Open Agent**.
6. The agent later resolves the notification by ID; the toast disappears automatically.
7. The notification remains in persisted history.

## 3. Functional Requirements

### 3.1 Notification types
At minimum: `info`, `success`, `warning`, `error`, `input_required`, `permission_required`,
`completed`, `blocked`. Represented internally as an enum; JSON API stays ergonomic
(snake-case strings in JSON; the CLI accepts snake-case or kebab-case input).

### 3.2 Priority / urgency
`low`, `normal`, `high`, `critical`. Default: `normal`.

### 3.3 Status lifecycle
`active`, `dismissed`, `resolved`. An `active` notification is shown as a toast (unless paused)
and is a candidate for "Needs attention".

### 3.4 Local HTTP API (see docs/API.md)
- `GET /health` — minimal, unauthenticated liveness probe.
- `GET /v1/health` — authenticated, detailed health.
- `POST /v1/notifications` — create a notification, return its ID.
- `GET /v1/notifications` — list with simple filters.
- `GET /v1/notifications/{id}` — fetch one.
- `PATCH /v1/notifications/{id}` — update status.
- `POST /v1/notifications/{id}/dismiss` — convenience dismiss.

Base URL `http://127.0.0.1:47821` (port configurable). Loopback only. Bearer-token auth.

### 3.5 Custom toast UI
- Borderless, compact, native-feeling WPF windows.
- Positioned bottom-right of the working area by default (top-right configurable).
- Centralized stacking manager (no toast picks its own coordinates).
- Never steals keyboard focus (WS_EX_NOACTIVATE + ShowActivated=false + topmost).
- Remains above normal windows; multiple monitors handled via work-area.
- Never partially off-screen.
- Dismiss button; optional **Open Agent** action.
- Different accent treatment per type/priority.
- Auto-dismiss durations per type (configurable); `input_required`, `permission_required`,
  and `blocked` persist until dismissed/resolved.

### 3.6 Tray icon and agent onboarding
- Runs while no window is open.
- Double-click opens the Notification Center.
- Context menu: Notification Center, Getting Started, Copy/Save agent `SKILL.md`, Pause
  Notifications, Launch at Startup, logs, and Exit.

### 3.7 Notification Center
- "Needs attention": active `input_required`, `permission_required`, `blocked`, `error`.
- "Recent": recently completed/dismissed/resolved.
- Per-item Open / Dismiss actions.

### 3.8 CLI (agentnotify)
- `send`, `list`, `get`, `resolve`, `dismiss`, `health`, `token`.
- Shorthand: `agentnotify "Task done"`.
- Talks to the local REST API; business logic lives in the broker only.

### 3.9 WSL integration
- `agentnotify.exe` invocable from WSL (documented); optional `agentnotify` bash wrapper.

### 3.10 Authentication
- On first launch a cryptographically random token is generated and stored in
  `%LOCALAPPDATA%\AgentNotify\config.json`.
- `Authorization: Bearer <token>` required for all `/v1/*` routes.
- CLI auto-reads the token. Env override `AGENTNOTIFY_TOKEN`.

### 3.11 Persistence
- SQLite database at `%LOCALAPPDATA%\AgentNotify\agentnotify.db`.
- Preserves notification history across restarts.
- Repository behind `INotificationRepository`; no SQL from UI code.

### 3.12 Deduplication
- Optional logical `key` in a request. If an active notification with the same key exists,
  it is updated in place instead of creating duplicates.

### 3.13 Startup at login
- Per-user `HKCU\...\Run` registration (no Windows Service).

### 3.14 Single instance
- Named mutex per user; second instance signals the first to show the center, then exits.

### 3.15 Logging
- Plain-text rolling daily log under `%LOCALAPPDATA%\AgentNotify\logs\`.
- No secrets logged.

### 3.16 Distribution and installation
- Produce one x64 self-contained `AgentNotifySetup.exe` for distribution.
- Install per user without a Windows Service or mandatory administrator elevation.
- Install a tray broker and an `agentnotify.exe` CLI with distinct Windows-safe filenames.
- Add the CLI directory to user `PATH`, create Start menu shortcuts, register startup and
  uninstall metadata, and preserve user history during uninstall.
- Display publisher, author, MIT License, and explicit no-warranty language during setup.
- Launch the tray process and open a polished offline getting-started page at setup finish.
- Allow that page and the tray menu to copy/download the agent `SKILL.md`.

## 4. Non-Functional Requirements

- Loopback binding only (`127.0.0.1`, never `0.0.0.0`).
- Bearer-token auth with constant-time comparison.
- Kestrel max request body size (~64 KB); title/message length limits; basic rate limiting;
  graceful malformed-JSON handling.
- Malformed requests and CLI connection failures must return useful errors without crashing.
- UI callback/render failures must not fail an already persisted API request.
- Small, understandable codebase; no enterprise layering.

## 5. Lifecycle Behavior

- App continues running when all windows are closed.
- Exit only via tray menu (or task manager).
- On exit, active toasts close; history persists.

## 6. Security Requirements

- Loopback only.
- Local bearer token; not internet-facing.
- Token never committed to git, never logged.
- No secrets in source.

## 7. WSL Development Constraints

- Repository lives at `/path/to/agent-notify` in this workspace.
- Code is edited from WSL and built with the **Windows** .NET SDK (`dotnet.exe`).
- The Windows .NET 10.0.302 SDK is available at `/mnt/d/dev/dotnet/dotnet.exe`.
- WPF must never be built with the Linux `dotnet`; only `dotnet.exe`.
- Build/test commands run from WSL (see README.md).

## 8. MVP Scope (included in V1)

API + auth, custom toasts, stacking/queueing, focus safety, notification center, tray,
SQLite history, CLI, WSL wrapper/docs, validated agent skill, dedup by key, per-user startup,
single instance, installer/onboarding, logging, and tests.

## 9. Explicit Non-Goals (V1)

- Windows Service / Session 0 service
- MSIX / Windows App SDK / native Notification Center integration
- Named pipes, MCP server, cloud backend, external auth, Docker, SQL Server, Redis,
  message broker, telemetry service, account/login
- Browser application/frontend (the installed guide is static offline documentation)
- Cross-virtual-desktop window activation (basic PID-based activation only)

## 10. Future Ideas (roadmap only)

Windows App SDK toasts, MCP server, named-pipe transport, richer agent SDKs, terminal/window
activation, virtual-desktop and terminal-tab awareness, per-agent sounds/icons, grouping,
snooze, quiet hours, agent heartbeat/status, webhooks, acknowledgement callbacks, response
buttons, coalescing, per-project rules, email, WhatsApp Business, Teams, Slack, Discord, SMS,
mobile push, LAN with a separate stronger-auth design, signed updates, ARM64, and MSIX.

## 11. Post-V1 product requirements

The current working V1 is the compatibility baseline. New development must add:

- a native tray-opened Settings UI;
- built-in and user-defined notification types with configurable presentation, lifetime, priority, and sound;
- global and per-type WAV/MP3 sound profiles with preview and quiet-hour behavior;
- SQLite-backed provider profiles, routes, durable outbox entries, and delivery attempts;
- Windows DPAPI current-user encryption for every provider credential stored in SQLite;
- opt-in outbound channels with test-send, redaction, bounded retries, timeouts, rate/cost controls, and diagnostics;
- generic webhook, SMTP, Telegram, chat, push, SMS, and official WhatsApp delivery, followed by additional providers listed in `docs/FEATURE_BACKLOG.md`;
- a repository-hosted documentation/wiki site and reproducible GitHub release automation; and
- portable boundaries that allow future native macOS and Linux implementations.

Local desktop delivery and history remain the source of truth. An outbound failure must never make local notification creation fail.
