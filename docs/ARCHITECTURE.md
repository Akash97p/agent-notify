# Architecture

## Design goals

AgentNotify is one interactive per-user Windows process. It owns the tray icon, localhost API, notification lifecycle, SQLite repository, dashboard, and custom WPF toast windows. This keeps installation and diagnostics simple while preserving clean boundaries inside the process.

## Components

### Contracts

`AgentNotify.Contracts` defines JSON DTOs, stable notification type identifiers, priority/status enums, and shared `System.Text.Json` rules. The original eight snake-case type IDs remain compatible while custom IDs are validated and persisted without an enum migration.

### Core

`AgentNotify.Core` contains no WPF or ASP.NET dependency. It owns:

- validation and lifecycle transition rules;
- creation and deduplication behavior;
- typed configuration and random-token generation;
- `INotificationRepository`;
- SQLite persistence; and
- local file logging.

Keyed creation is guarded by a process-wide asynchronous gate in `NotificationService`. AgentNotify is single-instance, so this prevents concurrent callers from creating two active rows with the same logical key.

### API

`AgentNotify.Api` builds an embedded ASP.NET Core Minimal API host. Kestrel binds to `127.0.0.1` and all `/v1` routes pass through bearer authentication. The host uses an explicit local content root so Windows test processes launched from WSL UNC paths do not hang while probing the working directory.

API callbacks are instance-scoped. Callback exceptions are logged and isolated from the API response, so a toast-rendering failure cannot roll back a notification already persisted to SQLite.

### Desktop app

`AgentNotify.App` owns the application lifetime. Startup order is:

1. acquire the per-session named mutex;
2. load and normalize config, generating the token when absent;
3. initialize SQLite;
4. start Kestrel;
5. construct the toast manager, dashboard, and tray icon;
6. restore persisted attention-required toasts; and
7. begin retention pruning.

A second process opens the named event created by the first process, signals it, and exits. The first process marshals that signal to the WPF dispatcher and shows the dashboard, including when the signal arrives during initialization.

### Toast lifecycle

`ToastStackManager` is the sole owner of visible toast positions and overflow. It chooses the monitor containing the current foreground window, converts the monitor work area from physical pixels to WPF units, and anchors the stack at the configured corner.

When the visible limit is reached, new notifications are queued instead of silently replacing an existing toast. Auto-expiry changes the notification status to `dismissed`, placing it in Recent history. Sticky attention types remain active until dismissed or resolved. Clicking a toast body opens the dashboard without implicitly resolving it.

### Sound delivery

The portable Core layer validates and imports WAV/MP3 files into a managed per-user directory using content-addressed safe names. Typed configuration selects a global file or per-type override. The WPF sound service plays on the UI dispatcher, respects pause/DND policy and volume, and isolates missing/invalid media failures from notification persistence and API responses.

### Installer

`AgentNotify.Setup` is a WPF per-user installer. `scripts/package.sh` first publishes the tray app and CLI as self-contained single files, then embeds them, the MIT License, the skill, and the offline guide into the self-contained setup executable.

Installed filenames deliberately differ on case-insensitive Windows filesystems:

- `AgentNotify.Tray.exe` — background UI/API process;
- `agentnotify.exe` — command-line client.

## Persistence

SQLite contains one `notifications` table and indexes for status, key, and creation time. Each repository operation opens a short-lived pooled connection. UI code never issues SQL directly.

Active attention rows survive restart. Resolved/dismissed rows older than `historyRetentionDays` are pruned. Malformed config falls back to safe defaults and is rewritten at app startup.

Custom type definitions live in typed configuration and control label, accent, default priority, enabled state, and lifetime. SQLite rows keep the stable type ID, so removing or disabling presentation policy never makes historical data unreadable. Legacy PascalCase type/duration values are normalized during load.

Delivery schema changes are tracked in `schema_migrations` and applied transactionally. The current schema contains provider profiles, routes, outbox items, and per-attempt diagnostics with foreign keys and due-work indexes. Provider secret dictionaries are encrypted before repository calls with a versioned DPAPI current-user envelope; public profile models contain only secret key names. A portable injected-key AES-GCM implementation exists for tests and future platform keychain adapters, never as an automatic production fallback.

After a notification is committed locally, matching enabled routes are idempotently materialized into the SQLite outbox before the API response. This hook performs no network I/O and is failure-isolated from local success. A single background dispatcher atomically claims due work, decrypts credentials only at the adapter boundary, enforces a timeout, records sanitized attempts, applies bounded jittered retry, dead-letters permanent/exhausted failures, and recovers interrupted claims on the next start. Adapter exceptions and response bodies are never written to diagnostics or logs.

## Failure behavior

- Malformed/oversized requests receive an HTTP error rather than crashing the broker.
- A UI callback failure does not fail a persisted API request.
- Logging failures are non-fatal.
- A malformed config uses defaults.
- The CLI catches connection failures and timeouts and exits nonzero with a useful message.
- Initialization failures are recorded in the local log and terminate the incomplete broker rather than leaving a partial tray process.

## Adding a new outbound adapter

The rules below apply to every adapter, including the eighteen already implemented. External delivery subscribes to lifecycle events after local persistence. Each adapter is isolated behind a delivery interface and the durable outbox, and must never block API persistence or the desktop UI thread. Provider credentials must not be stored in notification metadata or the config token field. See [CHANNELS.md](CHANNELS.md) for the per-provider security policies that a new adapter is expected to match.
