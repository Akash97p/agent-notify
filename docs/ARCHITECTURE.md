# Architecture

## Design goals

AgentNotify is one interactive per-user Windows process. It owns the tray icon, localhost API, notification lifecycle, SQLite repository, dashboard, and custom WPF toast windows. This keeps installation and diagnostics simple while preserving clean boundaries inside the process.

## Components

### Protocol

`AgentNotify.Protocol` is the portable wire-contract assembly. It defines the local API DTOs, stable
notification type identifiers, priority/status enums, shared `System.Text.Json` rules, and the ARC
0.1 event model and JSON Schema. The original eight snake-case
type IDs remain compatible while custom IDs are validated and persisted without an enum migration.

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

`POST /v1/events` accepts ARC `request.created`, `request.updated`, and `request.resolved` envelopes.
Created and updated events project into the native notification model; resolution closes the keyed
condition through the same lifecycle service. A stable key derived from sender/event identity makes
unkeyed retries idempotent across active and resolved history. An explicit request key opts into the
active-condition lifecycle. Unknown core fields are rejected, and only bounded correlation metadata
is retained.

### Planned interaction and host-adapter boundary

Bidirectional permission and question handling is planned as a portable Core/Protocol capability,
not as vendor logic inside WPF. A host adapter will translate a coding agent's synchronous hook,
plugin, SDK, gateway, RPC, or Agent Client Protocol request into one durable AgentNotify
interaction. SQLite will remain authoritative for the pending request, response, expiry, and native
host acceptance; desktop and mobile are competing response surfaces under a deterministic
first-valid-response-wins rule.

The adapter that owns the live native request also owns the in-memory waiter. Relay transports a
sealed response back to the desktop, but neither Relay nor the language model completes the waiter
directly. A response is successful only after the desktop validates and persists it and the native
host accepts the mapped decision. See
[BIDIRECTIONAL_AGENT_COMMUNICATION.md](BIDIRECTIONAL_AGENT_COMMUNICATION.md) for the researched
protocol split, security boundary, and agent-by-agent feasibility.

### Web interface

`AgentNotify.Api/WebUi` mounts a browser interface on the API listener when the host supplies
`WebUiOptions`; both `agentnotifyd` and the tray app do. The front end is plain ES modules and CSS,
embedded as resources, with no build step, so the .NET build and the Windows CI need no Node
toolchain. It talks only to `/ui/api`, which calls the same Core services the Settings window uses.

There is no sign-in, matching the tray app. A middleware guard runs before routing: it refuses
foreign `Host` headers (DNS rebinding) and requires `X-AgentNotify-UI: 1` plus a same-origin
`Origin` on every state change, so a page on another site can neither read the interface nor change
anything through it. Loopback host names may carry a different browser-side port when an SSH local
forward targets the broker's listening port; the guard compares a state change's `Origin` to the
actual `Host` header, including that browser-side port.

Provider editors are driven by `ProviderFormCatalog` (field descriptors), `ProviderFormBuilder`
(validation and the stored configuration document), and `ProviderFormReader` (non-secret values
back into a form), all in Core. Relay pairing runs in the broker through `RelayPairingSessions`, so
the issued installation token never reaches the page. See [WEB_UI.md](WEB_UI.md).

The Usage page is a read-only local-history projection. `LocalUsageService` in Core discovers
Claude Code and Codex session JSONL and OpenCode's `opencode.db` under the broker user's profile.
It retains only usage-bearing assistant rows, normalizes non-overlapping token buckets, and caches
JSONL events by path, size, and mtime for the lifetime of the broker. OpenCode's current `message`
table is queried afresh in read-only mode on each report, so live database updates are visible;
the SQL selects only scalar usage fields, never message payloads. It deduplicates Claude
message/request identities and differences Codex cumulative counters per rollout. OpenCode's
reasoning counter is separate from output and is added once; Codex reasoning is already included
in output. The `/ui/api/usage` route returns aggregated counts only;
it never returns log paths, prompt text, response text, or credentials. It does not probe providers,
or assert subscription quota. A dated, exact-model price catalog estimates what those token
records would cost at published standard API rates or OpenCode Go's published quota-equivalent
token rates, with separate Claude 5-minute and 1-hour cache-write prices. Unknown models and
OpenCode Go records with unpriced cache writes remain explicitly unpriced. Project grouping uses
each row's working directory (Claude), active turn/session directory (Codex), or OpenCode session
directory; the page receives
only a basename and stable opaque hash, never the full path. The source logs remain authoritative;
the Usage view also groups deduplicated rows by source, session, and project, exposing only a
hashed session ID, time span, token/model aggregates, and estimated cost for the 50 most recent
sessions. Its response uses `contract_version: "2"`; raw provider session IDs stay on the broker.
the current cache is in memory and is rebuilt after restart. More complete fork/replay attribution,
durable indexing, historical rate schedules, and provider-specific billing modifiers
remain separate work.

The Live quota page is a separate provider/account snapshot, never calculated from the Usage
ledger. The owner can add up to 16 named Codex/Claude profile directories under their home folder;
the owner-only config file stores profile IDs, labels, and paths, never copied credentials. The
page's profile-management endpoint returns paths only for the local owner to edit. A Codex child
process receives its selected `CODEX_HOME`; a Claude probe reads only that profile's
`.credentials.json` if available. `LiveQuotaService` in Core coalesces simultaneous requests,
caches each account for five
minutes, limits manual rechecks to one per 30 seconds, and retains a clearly marked stale value
after transient failure only while the credential-file scope is unchanged. Codex is queried
through its documented `app-server` `account/rateLimits/read` RPC; Codex owns its credentials and
AgentNotify receives only quota fields. The child process gets the Codex launcher's bin directory
in its own `PATH` so npm's `/usr/bin/env node` launcher works under launchd. Claude Code's current
OAuth credential is read without
modification for a bounded, read-only request to Anthropic's account-usage endpoint; no refresh
token is redeemed and no response or token is logged. A 429 respects `Retry-After`. The endpoint
is a first-party implementation dependency without a stable public API guarantee. OpenCode is
explicitly unavailable because it routes to multiple independent provider accounts. The quota
endpoint returns only normalized percentages, reset times, optional plan/credit values, source,
account ID/label, and freshness; it never returns access tokens or account email. Its quota report
uses `contract_version: "2"`. A separate OpenCode Go estimate sums only local SQLite token rows for
exactly priced models against OpenCode's published per-model dollar caps over rolling 5-hour,
7-day, and 30-day periods. It is explicitly estimated and has no provider reset or remaining
balance; unpriced rows suppress a window percentage. The page triggers on-demand
checks; there is no background network polling or dependency on internet for the rest of the app.

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

## Standing decisions

These are the constraints the implementation is held to. They are recorded because
each one is a choice that looks arbitrary from the code alone, and reversing any of
them changes the product rather than the implementation.

1. Configuration has two surfaces over one broker: the native WPF Settings window on
   Windows, and the web interface the broker serves at `/ui/` on every platform. Neither
   ever returns a stored secret. Neither asks for a password: both trust the person at the
   computer, and the web interface defends only against other web sites. Provider
   validation lives in Core (`ProviderFormCatalog`) so the surfaces cannot disagree about
   what a valid channel is; the WPF panel still carries its own copy until it is moved
   onto the catalog.
2. SQLite is the source of truth for notification history. Provider profiles, routing
   rules, outbox entries, and delivery attempts are added through explicit migrations
   that preserve existing history.
3. Provider credentials are encrypted with Windows DPAPI at current-user scope before
   persistence. Only sealed envelopes reach SQLite, and secret fields are redacted from
   every API and log.
4. Local desktop delivery is authoritative. Outbound channels are opt-in secondary
   deliveries and can never make notification creation fail.
5. Prefer official provider APIs. An unofficial bridge — Signal through `signal-cli`,
   for instance — must be labelled experimental and stay user-managed.
6. macOS and Linux clients are first-class roadmap goals, so portable routing and
   domain logic must not live in WPF classes.
7. ARC plus the local SQLite record is the canonical human-attention and interaction
   history. External agent protocols are adapters or projections over it, never
   replacements for the local lifecycle.
8. Bidirectional agent communication has two modes: direct native adapters for existing
   terminal and editor sessions, and an Agent Client Protocol client for sessions
   AgentNotify manages as subprocesses.
9. A skill or ordinary MCP tool cannot intercept a host-native approval. An adapter has
   to own a synchronous host hook, plugin/SDK/gateway/RPC request, or ACP
   server-initiated request, and return the human answer through that same control
   surface.
10. A remote answer is an authorization message. It is bound to the exact installation,
    session/turn, native request, and request digest; it expires; replayed or stale
    responses are rejected; and Relay acceptance, human response, and native-host
    acceptance stay three distinct facts.

## Adding a new outbound adapter

The rules below apply to every adapter, including the nineteen already implemented. External delivery subscribes to lifecycle events after local persistence. Each adapter is isolated behind a delivery interface and the durable outbox, and must never block API persistence or the desktop UI thread. Provider credentials must not be stored in notification metadata or the config token field. See [CHANNELS.md](CHANNELS.md) for the per-provider security policies that a new adapter is expected to match.
