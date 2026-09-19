# Architecture

## Design goals

AgentNotify runs one broker per user. On Windows the WPF tray process owns the broker, native
notification center, Settings window, and custom toasts. On macOS and Linux `agentnotifyd` owns the
same loopback API, lifecycle, SQLite repository, outbound delivery, and local WebUI. macOS also runs
a thin broker-owned AppKit status item for quota; Linux has no native tray yet. Portable domain
boundaries keep the hosts aligned.

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

### Interaction and host-adapter boundary

Portable Core services persist bounded permissions and questions in SQLite, enforce expiry,
supersession, digest/nonce checks, idempotency, and first-valid-response-wins, and expose
authenticated `/v1/interactions` routes. The WebUI and CLI answer locally. Codex/Claude ask hooks,
Hermes, and OpenClaw translate answers back through their host-owned control surfaces. The
adapter with a live native request owns its in-memory waiter; the SQLite record remains authoritative.
Relay/mobile can return an answer through background polling, and the broker validates it before
settling the waiter. Native-host acceptance is not yet persisted separately, and v1 mobile answers
are visible to Relay rather than sealed end to end. A managed Agent Client Protocol bridge remains
planned. See [BIDIRECTIONAL_AGENT_COMMUNICATION.md](BIDIRECTIONAL_AGENT_COMMUNICATION.md).

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
or assert subscription quota. A dated, exact-model price catalog keyed by provider and model
estimates what those token records would cost at published standard API rates or OpenCode Go's
published quota-equivalent token rates, with separate Claude 5-minute and 1-hour cache-write prices.
The rate is chosen per record (`ApiPriceCatalog.RateFor`), because each record is one request: its
prompt size selects a long-context tier, its service tier (Codex `thread_settings_applied`, OpenCode
`-fast` model IDs) selects OpenAI's fast rates, and its timestamp selects OpenCode Go's peak rate.
A record is unpriced when no published rate covers that combination. Unknown models and OpenCode Go
records with cache writes remain unpriced unless the provider publishes that exact rate;
context-tiered Go models remain unknown. Project grouping uses
each row's working directory (Claude), active turn/session directory (Codex), OpenCode or Kilo session
directory, Muse `route_facts` working directory (a subagent inherits its parent session's), or the
Gemini CLI project folder resolved through `projects.json` (names or SHA-256 hashes of the path); the page receives
only a basename and stable opaque hash, never the full path. The source logs remain authoritative;
the Usage view also groups deduplicated rows by source, session, and project, exposing only a
hashed session ID, time span, token/model aggregates, and estimated cost for the 50 most recent
sessions. Its response uses `contract_version: "4"`; raw provider session IDs stay on the broker.
the current cache is in memory and is rebuilt after restart.
On Windows the same sources are also read inside WSL. `WslDiscovery` in Core lists distributions
from `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss`, asks `wsl.exe --list --running` which
of them are running, resolves each default user's home from that distribution's `/etc/passwd`, and
reaches it through `\\wsl.localhost\<distribution>`. The default user is the one named by `[user]
default=` in the distribution's `/etc/wsl.conf` when present, because WSL applies it over the
registry's `DefaultUid`, which stays `0` for distributions configured that way; otherwise it is
`DefaultUid`. Only running distributions are touched:
opening the share of a stopped distribution boots its VM, which a dashboard visit must not do. The
agents' default locations inside the distribution are used, because their environment variables are
not visible to the broker. Discovery is cached for 30 seconds and re-resolved on every scan, so
history from a distribution appears while it runs; the report lists the included distributions and
uses `contract_version: "4"`. Reading through the share is slow for page-level I/O: SQLite querying a
260 MB OpenCode database over `\\wsl.localhost` took about 30 seconds per request and could fail,
while copying the file sequentially took under two. OpenCode rows are therefore cached per database
keyed by the length and modification time of the file and its `-wal`, and a database on a UNC path
is copied (with its WAL, retaken if either changes during the copy) into a random directory under
the user's temp folder, queried there, and deleted; leftovers older than an hour are removed on the
next read. JSONL ledgers were already cached per file, but only in memory, so the first scan after
the broker starts still parses every file. Walking directories through the share also costs a round
trip per directory, which took over ten seconds for Muse Code's per-subagent session folders, so a
ledger root inside WSL is listed by one `wsl.exe --distribution <name> --exec find <root> -type f
-name <pattern> -printf …` (no shell; paths with control characters or `..` are dropped), falling back
to walking the share if that fails; only files whose size or modification time changed are then read,
with 1 MB sequential buffers. To keep the cold scan affordable, a line is JSON-parsed only
when it contains the marker of a record that carries usage (`"assistant"` for Claude Code;
`token_count`, `turn_context`, `session_meta`, or `thread_settings_applied` for Codex;
`model_completed` or `route_facts` for Muse Code). Muse Code and Gemini CLI usage follow OpenAI's
convention where input includes cached input: Muse output includes reasoning, while Gemini reports
`thoughts` and `tool` tokens separately and they are added to output and input. The Usage report uses
`contract_version: "4"`, whose `source` values add `kilo`, `muse`, and `gemini_cli`. Linux `cwd` values are grouped by their POSIX path on a Windows broker. More complete fork/replay attribution,
durable indexing, historical rate schedules, and provider-specific billing modifiers
remain separate work.

The Live quota page is a separate provider/account snapshot, never calculated from the Usage
ledger. The owner can add up to 16 named Codex/Claude profile directories under their home folder;
the owner-only config file stores profile IDs, editable labels, and paths, never copied credentials.
Labels for the two built-in current profiles are stored separately so renaming never changes agent
profile discovery. The
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
uses `contract_version: "3"`. A separate OpenCode Go estimate sums only local SQLite token rows for
exactly priced models against OpenCode's published per-model dollar caps over rolling 5-hour,
7-day periods and a monthly period: the last 30 days, or the current billing cycle when the owner sets
`openCodeGoRenewalDay` (`OpenCodeGoBillingCycle`, local midnight on the renewal day, clamped to short
months). Each window reports `starts_at`, and a fixed window its `resets_at`; the quota report uses
`contract_version: "3"`. It is explicitly estimated and has no provider reset or remaining
balance; unpriced rows suppress a window percentage. The WebUI triggers checks on demand. On macOS, the enabled native status item polls the normalized
projection at its configured 5–60 minute interval (five minutes by default), which can trigger a
provider check after the cache expires. Disabling it restores fully on-demand quota checks; provider
failure or no internet never affects notifications, history, Usage, or the rest of the app.
API accounts are the opt-in exception: pasted provider keys are sealed with the same injected
protector (DPAPI current user on Windows) before SQLite storage, decrypted only transiently to
call one fixed `https` URL per provider, and never logged, returned, or embedded in errors or
snapshots. Outbound calls use a redirect-free, cookie-free client with a 15-second timeout, a
1 MiB body cap, generic error messages, `Retry-After` honoring (capped at one hour), a
five-minute per-account snapshot cache with manual refresh limited to once per 60 seconds, a
per-service gate against stampedes, and no background polling. The billing report uses
`contract_version: "1"`.
Each running WSL distribution with a `~/.codex` or `~/.claude` directory adds a discovered account
(`codex:wsl:<distribution>`, renamable through the same label map as the built-in accounts); an
account the owner added by hand for the same directory takes its place. Secondary profiles —
`.codex-<name>` or `.claude-<name>` directories (also with `_`) directly under the native home or
a running distribution's home that hold that agent's sign-in markers (`auth.json` or `sessions`
for Codex, `.credentials.json` or `projects` for Claude Code) — are discovered the same way
(`codex:home:<name>`, `claude_code:wsl:<distribution>:<name>`), listed after the built-ins and
before hand-added accounts, and counted by Usage. A discovered profile whose directory matches a
hand-added or built-in profile is listed once, and removing any discovered account records its ID
until it is restored. Built-in and discovered
accounts cannot be deleted from config because they are derived, so removing one records its ID in
`removedQuotaAccounts`, which both Live quota and the account list filter out until it is restored.
Pointing one at a different directory creates an added account and records the detected ID as
removed, so there is one representation of an account's directory rather than per-ID overrides.
Usage reads the ledgers of added accounts as well as the default locations, deduplicating roots by
path and events by identity, so a profile that is both added and discovered is counted once. A profile on the WSL share
can also be added by hand, as the one exception to the under-home rule. Codex for such a profile is
run inside that distribution — `wsl.exe --distribution <name> --exec /bin/sh`, then the user's login,
interactive shell so version-manager PATH setup applies — with `CODEX_HOME` passed through
`WSLENV`; the probe ignores non-JSON stdout lines a shell rc file may print. Claude's credential file
is read through the share like any other.

### macOS quota menu bar

`agentnotify-menubar` is a native Swift/AppKit accessory executable shipped only in macOS archives.
`agentnotifyd` starts it from beside the broker after the loopback API is listening, passes only the
configured port, restarts it when its WebUI settings change, and stops it during broker shutdown or
when the owner disables it. It has no Dock icon and never opens `config.json`, reads the bearer token,
inspects an agent profile, or calls a provider directly.

The child reads a secret-free `contract_version: "1"` projection under `/ui/api/menu-bar`. Core filters
the Live quota report to Codex and Claude Code, keeps every monitored account and quota window for the
dropdown, and chooses the lowest selected five-hour (`duration_minutes == 300`) remaining percentage
for the status title. An empty account selection means every account. Stale snapshots remain visibly
stale rather than becoming an invented zero. WebUI configuration controls enabled state, a 5–60
minute polling interval, and headline account selection; only the headline is filtered.

The status item uses the same local-user trust boundary as the WebUI. Its periodic GET can cause a
provider probe after `LiveQuotaService`'s cache expires; the service still coalesces requests, keeps
per-account caches/failure state, applies provider rate limits, and sanitizes the result before the
child receives it. Explicit **Refresh now** and **Disable Menu Bar** actions use the same protected
state-change header as the browser. Failure leaves the rest of the broker usable and shows `--%`.

The Insights Dashboard is a browser-side composition of the existing Overview, Usage, and Live
quota projections. It keeps their provenance separate: account quota cannot be attributed to
project token history when the source logs do not expose a reliable account identity. Compact
quota gauges visualize remaining balance, and Usage keeps dense session/project/model details in
disclosures. Motion is implemented with embedded CSS, includes no external runtime, and obeys
`prefers-reduced-motion`.

### Provider router

`AgentNotify.Core/Router` is an opt-in local proxy: an agent points its API base URL at the broker,
and the router chooses an upstream provider and model per request, translates between the OpenAI
Responses, OpenAI Chat Completions, and Anthropic Messages wire formats, fails over across an ordered
list of targets, and records what it did. It is off until the owner turns it on, and a broker with it
off contacts no provider. See [ROUTER.md](ROUTER.md).

It deliberately breaks one habit of the rest of the broker: the router's request path *is* network
work, because proxying is what it does. It stays outside the notification path — it never writes
notification history, never enqueues delivery, and never touches the WPF dispatcher — so a hung
upstream cannot delay a toast or an API response.

`RouterProxy` is mounted on the existing loopback listener under `/router/v1`, before the `/v1`
branch, and authenticates with its own key rather than the broker's bearer token: an agent's
configuration file then holds a credential that can spend money but cannot read notifications. The
same loopback `Host` check as the web interface applies, and a request carrying an `Origin` header is
refused, so no web page can reach it. Router routes raise the request-body limit from the API's
64 KiB to `routerMaxRequestBodyBytes`, because a single agent turn carries a whole conversation.

Upstream definitions, routes, and the ledger live in SQLite beside everything else. An upstream key is
sealed with the same `ISecretProtector` as channel credentials, decrypted only while a request is
being built, and never returned by an endpoint, written to a log, or stored in the ledger; an envelope
that cannot be opened fails that attempt (`provider_key_unreadable`) rather than sending the request
without a credential. A key may only travel to its own validated base URL: `https` anywhere, plain
`http` only for a loopback literal, no redirects, no cookies, no system proxy.

Two upstream kinds carry no key at all: a subscription upstream (`codex_chatgpt`, `muse_code`)
authenticates with the sign-in another tool keeps on this computer, read from that tool's file at
request time by `RouterCredentialSource`. Those tokens are held only in memory, never logged, stored,
or returned, and go only to the preset's fixed host. Renewing Codex's sign-in writes the new tokens
back into Codex's own `auth.json` — the one other place, beside `Router/Connect`, where AgentNotify
writes another program's file — because rotating its refresh token without doing so would sign Codex
out. Both kinds are unofficial and opt-in. Reading a key OpenCode already holds happens only on an
explicit request from the owner, and that key is then sealed like one typed in.

One credential the router never holds: an agent's **own** Anthropic sign-in. Claude Code has a single
base URL, so a connected Claude Code sends its built-in models to the router too. It authenticates to
the router with `x-agentnotify-router-key`, which leaves its `Authorization`/`x-api-key` carrying its
own credential; the router forwards that, with the request body unchanged, to
`https://api.anthropic.com/v1` for a bare `claude-…` model no route claims, and to nothing else. It is
per request, never stored, logged, or put in the ledger, and never sent to a configured upstream.

**One account list.** Codex and Claude Code accounts — the built-in profiles, discovered ones such as
`~/.codex-second`, and those added by hand — are defined once, by `QuotaAccountDefinition.Monitored`.
Live quota, the router's agent connections (`RouterAgentProfile.FromAccounts`), and the notification
setup page all read it, so there is no second place to register an account. The same holds for keys:
a router upstream references a key under API accounts (`api_account:<id>`) instead of copying it.

`RouteResolver` is a pure function of one configuration snapshot and the requested model, so a ledger
row can be explained after the fact. Translation runs through one intermediate request and one stream
event model, which is what makes every wire pair work from three decoders and three encoders; when the
inbound and upstream wires match, the body is passed through untouched except for the model name, so
fields the intermediate model does not carry — Responses reasoning items with `encrypted_content`,
Anthropic thinking signatures — survive that hop. Failover is ordered and stops being possible the
moment a byte reaches the client: a partially delivered stream cannot be retried as though nothing was
sent, so a later failure ends the stream with the client wire's own error event.

`Router/Connect` puts routed models into an agent's own model picker by writing that agent's
configuration: a generated model catalogue plus a provider block for Codex, a `modelPicker` list plus
an `env` block for Claude Code. It is the only part of AgentNotify that edits another program's
files, so it is held to a narrow contract: a copy is taken before every write and any copy can be
restored, only marker-delimited lines or named keys are touched, a key the owner set is commented out
rather than duplicated (TOML refuses duplicates), disconnecting restores the owner's previous values,
and a connected agent's catalogue is rewritten whenever the router's configuration changes so it never
lists a model the router would refuse. A reconnect keeps the values recorded at the first connect,
including a recorded "none", because what it finds in their place are AgentNotify's own. The router
key is written into those files deliberately, so a connected agent needs no environment variable; that
key can spend through the router and nothing more.

Smart switching is persisted as `off`, `ordered`, `sticky`, or `round_robin`; sticky and round-robin
cursors are process-local because they are scheduling state, not configuration. A native Claude Code
request may fail outward from the agent's own Anthropic credential to configured equivalent-model and
cross-model targets, but the native credential target is never introduced into another request.
Reasoning effort is normalized from Claude Code's five-step `output_config.effort` and from Codex's
`reasoning.effort`/`reasoning_effort` — the five level names mean the same thing on both scales —
then mapped only after a concrete provider/model target is selected. Automatic capability tables
are identity wherever the target can spell a level, so no client's effort is silently renamed; only
levels above the target's top collapse onto it. Family-level and bounded per-model overrides
determine the emitted provider vocabulary, a target default covers requests that carry no effort,
and `omit` avoids sending unsupported fields. A value outside the five names (OpenAI's `minimal`, a
provider-specific word) is sent verbatim when the target supports it, and otherwise falls back to
the default.

The ledger is proxy-observed usage and is kept separate from the log-derived Usage view and from Live
quota. The same physical call appears in both the router ledger and the agent's own log, so the two
are never added together.

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

Setup treats a run as an update when this user's uninstall registration names a folder that still
contains `AgentNotify.Tray.exe`. An update keeps that folder and the startup/shortcut choices and
does not ask for the licence again. It stops the tray by setting `Local\AgentNotify.Exit.v1`, an
auto-reset event the single-instance owner creates next to its show-center event; the tray answers
with the same shutdown as its Exit menu item, and setup kills a tray that has not exited after ten
seconds. `agentnotify.exe` is never stopped, since an agent may be blocked in it: when a payload
file is in use, setup renames it to `<name>.<id>.old` (Windows permits renaming a running image),
moves the new file into place, and deletes those leftovers on the next install or uninstall.

Installed filenames deliberately differ on case-insensitive Windows filesystems:

- `AgentNotify.Tray.exe` — background UI/API process;
- `agentnotify.exe` — command-line client.

## Persistence

SQLite contains one `notifications` table and indexes for status, key, and creation time. Each repository operation opens a short-lived pooled connection. UI code never issues SQL directly.

Active attention rows survive restart. Resolved/dismissed rows older than `historyRetentionDays` are pruned. Malformed config falls back to safe defaults and is rewritten at app startup.

Custom type definitions live in typed configuration and control label, accent, default priority, enabled state, and lifetime. SQLite rows keep the stable type ID, so removing or disabling presentation policy never makes historical data unreadable. Legacy PascalCase type/duration values are normalized during load.

Delivery schema changes are tracked in `schema_migrations` and applied transactionally. The current schema contains provider profiles, routes, outbox items, and per-attempt diagnostics with foreign keys and due-work indexes. Provider secret dictionaries are encrypted before repository calls with a versioned DPAPI current-user envelope; public profile models contain only secret key names. A portable injected-key AES-GCM implementation exists for tests and future platform keychain adapters, never as an automatic production fallback. Stored API-account keys live in a separate `billing_accounts` table in the same database file (`id`, `provider`, `label`, sealed `encrypted_key`, timestamps), created idempotently; list and snapshot responses carry only the id, provider, label, timestamps, and `has_key`, never the key or any part of it.

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
3. Provider credentials are encrypted before persistence: current-user DPAPI on Windows and
   platform-backed AES-GCM keys on macOS/Linux, with a documented owner-only key-file fallback on
   Linux. Only sealed envelopes reach SQLite, and secret fields are redacted from every API and log.
4. Local desktop delivery is authoritative. Outbound channels are opt-in secondary
   deliveries and can never make notification creation fail.
5. Prefer official provider APIs. An unofficial bridge — Signal through `signal-cli`,
   for instance — must be labelled experimental and stay user-managed.
6. macOS and Linux clients are first-class roadmap goals, so portable routing and
   domain logic must not live in WPF classes.
7. ARC plus the local SQLite record is the canonical human-attention and interaction
   history. External agent protocols are adapters or projections over it, never
   replacements for the local lifecycle.
8. Bidirectional agent communication uses direct native adapters for existing terminal and editor
   sessions. An Agent Client Protocol client for managed subprocess sessions remains planned.
9. A skill or ordinary MCP tool cannot intercept a host-native approval. An adapter has
   to own a synchronous host hook, plugin/SDK/gateway/RPC request, or ACP
   server-initiated request, and return the human answer through that same control
   surface.
10. A remote answer is an authorization message. The current broker checks the request digest,
    single-use nonce, expiry, and first-wins state and rejects stale/replayed responses. Relay
    acceptance, broker answer acceptance, and native-host acceptance are distinct facts; the last
    is not yet persisted as a receipt. End-to-end sealing to the installation remains required work.

11. The provider router is off by default and authenticated with its own key. It is the one
    component whose request path performs network I/O, it never writes notification history or
    delivery work, and its proxy-observed ledger is a separate record from log-derived Usage and
    from account Live quota — the three are never summed. Its agent connectors are the only code that
    writes another program's configuration, and only when the owner presses a button or runs a
    command; every such write is preceded by a restorable copy.

## Adding a new outbound adapter

The rules below apply to every adapter, including the nineteen already implemented. External delivery subscribes to lifecycle events after local persistence. Each adapter is isolated behind a delivery interface and the durable outbox, and must never block API persistence or the desktop UI thread. Provider credentials must not be stored in notification metadata or the config token field. See [CHANNELS.md](CHANNELS.md) for the per-provider security policies that a new adapter is expected to match.
