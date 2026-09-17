# AgentNotify — TODO

`[x]` is implemented and verified at the level stated. Unchecked items are genuine remaining work.

## Product and broker

- [x] Windows WPF per-user tray process
- [x] ASP.NET Core Minimal API bound only to `127.0.0.1`
- [x] Random local bearer token and authenticated `/v1` routes
- [x] Notification types, priorities, statuses, context, metadata, and logical keys
- [x] User-defined type IDs with configurable label, accent, priority, lifetime, and safe fallback
- [x] Versioned SQLite provider/route/outbox/attempt schema
- [x] DPAPI current-user encrypted provider secrets with redacted summaries
- [x] Durable outbound dispatcher with idempotent queueing, recovery, retry, diagnostics, and test-send
- [x] SQLite history behind `INotificationRepository`
- [x] Concurrent-safe keyed deduplication inside the single broker
- [x] Request/body validation, rate limiting, and malformed JSON handling
- [x] ARC 0.2 request creation, update, response, and resolution lifecycle
- [x] Local daily logs without token logging
- [x] History retention pruning

## Desktop experience

- [x] Branded tray/window/setup icon from `assets/branding/an.ico`
- [x] Custom borderless WPF toasts with `WS_EX_NOACTIVATE`
- [x] Foreground-monitor, DPI-aware centralized stacking
- [x] Overflow queue
- [x] Type-based expiry and sticky attention requests
- [x] Persisted attention toast restoration
- [x] Notification Center with attention/recent views and status actions
- [x] Pause and Start with Windows tray toggles
- [x] Single-instance mutex plus reliable show-center signal
- [x] Tray Getting Started, Copy SKILL.md, and Save SKILL.md actions
- [x] Native Settings UI for general, toast lifetime, and initial sound controls
- [x] Managed global/per-type WAV/MP3 sounds with preview, volume, pause, and DND policy
- [ ] Reliable “Open Agent” terminal/tab activation across virtual desktops

## CLI and agent integration

- [x] `send`, shorthand, `list`, `get`, `resolve`, `dismiss`, `health`, `token`, `install-skill`, `install-harness`, `interactions`, and version commands
- [x] Snake-case and kebab-case CLI type parsing
- [x] Friendly nonzero exits for invalid arguments, broker connection failures, and timeouts
- [x] WSL wrapper and Windows user PATH installation
- [x] Validated `agentnotify` skill and OpenAI UI metadata
- [x] Offline browser guide with Copy/Download skill actions
- [x] Offline Codex and Claude Code skill installation from the CLI
- [x] Notify-only auto-notify harnesses for OpenCode (event plugin), Codex (hooks), and Claude Code (hooks) with `install-harness` (real-host display smoke pending)
- [x] Durable interaction/response model with expiry, cancellation, idempotency, and host acceptance
- [x] Returning the human answer into the waiting host call (Codex/Claude ask mode, Hermes transport, OpenClaw watcher)
- [x] Local permission, single-choice, and text response UI with first-valid-response-wins (web interface `Questions` page)
- [ ] Generic Agent Client Protocol bridge for managed coding-agent sessions
- [x] Native adapters for all eleven hosts (OpenCode, Codex, Claude, Gemini, Copilot, Cursor, Muse, Kilo, OpenClaw, Hermes, Pi)
- [x] Relay reverse response envelopes with reconnect/backfill and replay defense (broker, Relay, and mobile all implemented; continuous desktop polling runs with the broker)
- [x] Mobile allow/deny, single-choice and free-text answer UI (desktop/host outcome feedback back to the phone is still absent — the phone learns only that the Relay recorded the answer)
- [x] Teach the skill that questions exist (`interactions request`/`wait` for `single_choice` and `text`)
- [ ] Multi-select answers. `single_choice` means exactly one and the broker rejects an
      answer carrying more; there is no kind for "pick several".
- [ ] Free-form phone-to-agent messaging. Deliberately outside the interaction contract
      (an answer is authorized by a request's digest, nonce and first-wins state; a
      message has none of those). Needs its own installation inbox, a `can_message`
      grant, and a host adapter that can actually accept text — see
      `docs/RELAY_INTERACTIONS.md`.

## Distribution and open source

- [x] One self-contained `AgentNotifySetup.exe` distributable
- [x] Per-user install, Start menu, optional desktop shortcut, startup, PATH, and uninstall registration
- [x] Publisher/author/version metadata
- [x] MIT License and setup no-warranty acknowledgement
- [x] README, contributing, security, architecture, installation, API, CLI, configuration, troubleshooting, integration, roadmap, and verification docs, indexed by `docs/README.md`
- [x] Preserve local user data on uninstall
- [ ] In-place update: setup detects the installed version, skips the licence, stops the tray
      through a named exit event, renames in-use files, and restarts the tray. Implemented and
      cross-compiled only; not yet run on Windows.
- [ ] Authenticode signing and timestamping
- [ ] Published SHA-256 checksums
- [ ] ARM64 installer
- [x] Portable `agentnotifyd` broker for macOS and Linux
- [x] Self-contained archives for win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64 with checksums
- [x] Checksum-verifying POSIX `install.sh`
- [x] Green Linux/macOS CI with self-contained CLI/broker execution on both hosted runners
- [ ] Homebrew tap and Winget manifest
- [x] Cross-platform web interface served by the broker at `/ui/` (`agentnotify ui`), covering Settings and Notification Center — no sign-in; owner-verified on macOS
- [x] Local Usage page: read-only Claude Code, Codex, and OpenCode token summaries by source, provider, project, model, and day, with dated API/Go token-rate estimates; live browser check on macOS
- [x] Live quota page: Codex app-server and Claude Code account-usage probes with cached five-hour/weekly windows, reset times, stale state, and OpenCode unavailable state; live broker/browser check on macOS
- [x] Named additional Codex/Claude profile monitoring and local OpenCode Go per-model published-cap estimates; profile and estimate tests, macOS broker check
- [x] Compact remaining-balance quota UI, editable names for current/additional agent profiles, simplified Usage disclosures, and animated Insights dashboard
- [x] WSL on Windows: usage, live quota, and skill installs include running WSL distributions (default user from `/etc/wsl.conf`); WSL profile directories can be added by hand; `install-skill --wsl`; discovery, quota, and usage owner-checked on Windows 11 with Ubuntu-20.04
- [x] Editable, movable, and removable Live quota accounts, including built-in and discovered ones, with restore
- [x] Automatic detection of secondary `.codex-*`/`.claude-*` profiles in the native and WSL homes (automated tests only; the owner's machine has none)
- [x] Muse Code, Kilo CLI, and Gemini CLI usage sources; counts matched an independent recount on the owner's machine
- [x] Per-record model pricing from official rate pages (OpenAI incl. long-context and fast tiers, Meta, Google, Z.ai, Xiaomi, OpenCode Zen/Go incl. peak hours)
- [x] OpenCode Go monthly estimate anchored to the owner's renewal day
- [x] API accounts: encrypted, write-only provider keys for DeepSeek, Kimi, SiliconFlow, OpenRouter, OpenAI Admin, and Anthropic Admin balance/spend; DeepSeek owner-checked on Windows
- [x] Provider router (opt-in): `/router/v1` responses/chat/messages endpoints with its own key,
      upstream definitions with sealed keys, aliases and ordered failover combos, Responses/Chat/
      Anthropic translation with same-wire passthrough, cooldowns, and a proxy-observed SQLite ledger
      — automated tests only; no request has been sent to a real provider yet
- [ ] Router: policy routing scored on quota/health/cost, weighted and least-used combo strategies,
      Codex account pools, cost estimates on ledger rows, and quota thresholds raised as attention
      requests
- [ ] WSL-aware `install-harness` (hook commands need Linux paths and `python3`)
- [ ] Durable usage index with scan progress: parsed history is held in memory, so the first scan after a broker start takes about two minutes on a ~4.8B-token WSL history
- [ ] Complete local usage indexing: fork replay handling and historical/versioned pricing; recent-session grouping is implemented
- [ ] Expand live quota: a stable public Claude API or documented statusline bridge, context-tiered Go model rates, and opt-in, clearly labelled unofficial sources (Gemini CLI/Antigravity, Cursor, Copilot, Kilo) — Muse Code exposes quota only by spending a request
- [ ] API accounts: DeepSeek spend derived from balance changes; verify Anthropic cost-report units and OpenAI pagination with real Admin keys; xAI management-key billing
- [ ] Move the WPF Channels panel onto the portable `ProviderFormCatalog`, so provider validation exists once
- [ ] Human visual verification of the web interface on Windows, served by the tray app (the owner has used Dashboard, Live quota, and API accounts there; no systematic page-by-page or narrow-window check yet)
- [ ] Native macOS menu-bar and Linux tray clients
- [ ] Automatic updates and schema/config migration framework

## Quality

- [x] Windows .NET 10 release build: 0 warnings, 0 errors
- [x] Automated tests: 1,050 passed, 0 failed, 0 skipped
- [x] Installer packaging and embedded skill validation
- [ ] Human visual verification on 100%, 150%, and 200% DPI
- [ ] Human multi-monitor/taskbar-position verification
- [ ] Clean-profile install/uninstall verification on a second Windows user

## Future delivery channels

- [x] Hardened generic HTTPS webhook adapter and native provider/route/diagnostics UI
- [x] Authenticated SMTP adapter with required TLS and recipient allowlist
- [x] Official Telegram Bot API adapter with encrypted token/destination
- [x] Discord incoming-webhook adapter with encrypted URL and mention suppression
- [x] Slack/GovSlack incoming-webhook adapter with encrypted URL and control-sequence suppression
- [x] Microsoft Teams Workflows Adaptive Card adapter for current global-cloud trigger URLs
- [x] Zoho Cliq channel/bot webhook adapter for all nine official data centers
- [x] Google Chat incoming-webhook adapter with encrypted URL, safe threading, and mention suppression
- [x] Mattermost incoming-webhook adapter with explicit self-hosted network consent and silent mode
- [x] Matrix Client-Server API adapter for unencrypted rooms with encrypted token/room ID
- [x] ntfy push adapter with encrypted topic/token, self-hosting, and anonymous-topic consent
- [x] Gotify push adapter with encrypted application token and plain-text-only payloads
- [x] Pushover adapter with encrypted app/user keys, device/sound selection, and opt-in emergency priority
- [x] Pushbullet note adapter with encrypted token/target, quota consent, and stable retry GUID
- [x] Twilio SMS adapter with encrypted allowlists, one-segment cost bound, priority floor, and paid-send consent
- [ ] Email provider API adapters
- [x] Official WhatsApp Business Cloud API adapter for approved text templates, one opted-in recipient, and paid-send controls
- [x] Optional Twilio WhatsApp Content Template adapter with encrypted allowlists and consent/cost controls
- [x] MQTT 5 adapter with mandatory TLS, pinned validated DNS, encrypted fixed topic/authentication, optional certificate-store mTLS, and explicit QoS semantics
- [x] AgentNotify Relay adapter plus browser/CLI device-grant pairing, encrypted installation credential, DNS pinning, and durable outbox against the fixed hosted endpoint (experimental opaque transport; independent E2E crypto review pending)
- [ ] Email provider API adapters
- [ ] Additional SMS and mobile push adapters
- [ ] Quiet hours, snooze, escalation, grouping, and per-project routing
- [ ] Response buttons and acknowledgement callbacks to agents (tracked as A01/A02)
- [ ] Optional MCP elicitation/server and richer language SDKs after the interaction model stabilizes

External delivery must remain disabled by default and complete the security/privacy design in `SECURITY.md` and `docs/ROADMAP.md` first.

The first local usage and live quota views are implemented, and the first provider router ships behind
its own switch ([docs/ROUTER.md](docs/ROUTER.md)); remaining usage, quota, and routing work is recorded
in [docs/future-scope](docs/future-scope/README.md).

The complete task breakdown and provider-by-provider implementation order lives in `docs/FEATURE_BACKLOG.md`. The constraints the implementation is held to live in `docs/ARCHITECTURE.md`, and what has actually been verified lives in `docs/VERIFICATION.md`.
