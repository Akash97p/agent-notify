# Feature backlog

This backlog turns the long-term product direction into independently testable branches. Ordering may change when a prerequisite or security concern is discovered, but each completed task must update this file.

## Foundation tasks

### F01 — Native Settings window

- Status: initial implementation complete; provider, route, and richer sound sections expand with their foundations.
- Add a tray action and single-instance WPF Settings window.
- Provide validated sections for general behavior, toast placement/lifetime, history, startup, sounds, custom types, channels, routes, and diagnostics.
- Save atomically and make restart requirements explicit.

### F02 — Extensible notification definitions

- Status: implemented and runtime-smoked; human Settings visual inspection pending.
- Preserve compatibility with the eight built-in API values.
- Add user-defined types with stable IDs, display names, colors/icons, default priority, sticky/expiry behavior, and enabled state.
- Define safe fallback behavior when a custom type is deleted or disabled.

### F03 — Sound profiles

- Status: implemented and covered by file/config tests; human playback/UI verification pending.
- Support global and per-type sound choices, mute, volume, and preview.
- Accept common formats supported reliably on Windows, beginning with WAV and MP3.
- Copy approved files into a managed per-user directory; validate size/type and handle missing files safely.
- Add quiet-hours and critical-notification override policy hooks.

### F04 — Delivery persistence and encrypted secrets

- Status: implemented with transactional schema v1, atomic outbox claiming, DPAPI current-user envelopes, bounded inputs, redacted summaries, and durable/concurrency repository tests.
- Add versioned SQLite migrations for provider profiles, encrypted secret values, routing rules, outbox messages, and delivery attempts.
- Add a DPAPI current-user secret protector with versioned ciphertext envelopes and test-only portable implementation.
- Ensure all DTOs, diagnostics, exports, and logs redact credentials.

### F05 — Routing and durable delivery engine

- Status: core engine implemented; advanced time/attention-state matching remains grouped with F07.
- Match on type, priority, project, agent, time, and attention state.
- Persist work before dispatch; use bounded exponential retry with jitter, timeout, idempotency, and dead-letter state.
- Never block local persistence, the API response, or the WPF dispatcher on network delivery.
- Add per-route payload redaction and test-send.

### F06 — Channel management UI

- Status: initial webhook provider, route, test-send, deletion, and redacted diagnostics UI implemented; extend per adapter.
- Create, edit, enable, disable, test, and delete provider profiles.
- Show health, last success/failure, retry state, and actionable validation errors without revealing secrets.
- Require explicit consent before sending notification bodies off-device.

### F07 — Rules, quiet hours, and escalation

- Add per-project/agent/type routes, quiet schedules, snooze, grouping, cooldowns, and escalation delays.
- Make local critical attention behavior explicit and testable.

## Outbound channel tasks

Implement each channel on its own `feature/channel-*` branch after F04–F06. All channels are disabled by default.

| ID | Channel | Integration approach | Confidence / constraint |
|---|---|---|---|
| C01 | Generic webhook | Configurable HTTPS POST, headers, HMAC signature, JSON template | Adapter and management UI implemented and tested |
| C02 | SMTP email | STARTTLS/TLS, authenticated SMTP, recipient allowlist | Adapter and Settings integration implemented; real-server smoke pending |
| C03 | Telegram | Official Bot API `sendMessage` | Adapter and Settings integration implemented; real-bot smoke pending |
| C04 | Discord | Incoming webhook | Adapter and Settings integration implemented; real-webhook smoke pending |
| C05 | Slack | Incoming webhook | Adapter and Settings integration implemented; real-webhook smoke pending |
| C06 | Microsoft Teams | Teams Workflows webhook | Global-cloud adapter and Settings integration implemented; real-workflow/sovereign-cloud smoke pending |
| C07 | Zoho Cliq | Incoming webhook/bot endpoint | Adapter and Settings integration implemented for all nine data centers; real-webhook smoke pending |
| C08 | Google Chat | Incoming webhook | Adapter and Settings integration implemented; real-webhook smoke pending |
| C09 | Mattermost | Incoming webhook | Adapter and Settings integration implemented; real-server smoke pending |
| C10 | Matrix | Client-server API message send | Unencrypted-room adapter and Settings integration implemented; E2EE/real-server smoke pending |
| C11 | ntfy | HTTP publish API | Adapter and Settings integration implemented for hosted/self-hosted servers; real-server smoke pending |
| C12 | Gotify | REST message API | Adapter and Settings integration implemented; real-server smoke pending |
| C13 | Pushover | Official message API | Adapter and Settings integration implemented; real-account smoke/receipt polling pending |
| C14 | Pushbullet | Official push API | Note adapter and Settings integration implemented; real-account smoke pending |
| C15 | Twilio SMS | Official Messages API | Adapter and Settings integration implemented with one-segment/at-most-once controls; real-account smoke and durable daily budget pending |
| C16 | WhatsApp | Official Meta WhatsApp Cloud API | Approved text-template adapter and Settings integration implemented; real-business-account smoke, delivery-status webhooks, and durable spend budget pending |
| C17 | Twilio WhatsApp | Twilio Messages API + Content Templates | Adapter and Settings integration implemented with text-template attestation and consent/cost controls; real-account smoke and delivery status pending |
| C18 | Signal | User-managed `signal-cli` process adapter | Experimental/unofficial; never imply official Signal support |
| C19 | MQTT | MQTT 5 publish to configured TLS broker/topic | Adapter and Settings integration implemented with DNS pinning, platform trust/mTLS, fixed encrypted topic, and explicit QoS semantics; real-broker smoke pending |
| C20 | AgentNotify Relay | Hosted HTTPS envelope API with per-device opaque transport | Adapter, browser/device-grant pairing, CLI pairing/status, encrypted credential storage, DNS pinning, and durable outbox implemented against the fixed hosted endpoint; owner Mac-to-phone notification and question-answer round trips verified. Independent crypto review, sealed response content, and delivery-status polling remain pending. |
| C21 | AWS SNS | Signed AWS API/SDK publish | Paused/not implemented; revisit only with explicit static credentials, no ambient credential-chain inheritance, fixed destination, cost controls, and a provider-specific retry review |
| C22 | Azure Communication Services | Email/SMS provider SDK/API | Medium; connection credentials and cost controls required |
| C23 | SendGrid | Mail Send API | High; useful when SMTP is unavailable |
| C24 | Mailgun | Messages API | High; region and domain configuration required |
| C25 | Postmark | Email API | High; server token and sender validation required |
| C26 | Apprise bridge | User-managed local Apprise CLI/API | Experimental bridge offering many community transports |

## Agent communication tasks

### A01 — Acknowledgement callbacks

Allow an agent to provide a safe loopback callback or polling correlation ID and observe delivered, viewed, dismissed, resolved, or failed state.

- Keep transport delivery, human view, human response, and native-host acceptance as separate
  states.
- Prefer authenticated polling or a broker-owned adapter connection over arbitrary callback URLs.

### A02 — Structured response actions

Support buttons and bounded text choices that return a structured response to a waiting agent without executing arbitrary commands.

- Status: broker, WebUI answering, and Relay/mobile answer path shipped. Durable interaction model (SQLite, keyed idempotency,
  supersede, first-valid-response-wins, digest/nonce binding, TTL, waiters) with loopback
  API (`request/list/get/wait/respond/cancel/publish`) and `interactions` CLI lives in
  [INTERACTIONS.md](INTERACTIONS.md). Host answer return works for Codex/Claude ask mode,
  Hermes transport, and the OpenClaw watcher; Relay publish, background response polling, and the
  mobile answer UI form the shipped path in [RELAY_INTERACTIONS.md](RELAY_INTERACTIONS.md). Remaining: native WPF response
  controls, sealed mobile responses, host-acceptance receipts, and broader answer adapters.
- Extend versioned interaction persistence with host-acceptance outcomes.
- Implement first-valid-response-wins across local desktop and mobile surfaces.
- Permission allow-once/deny, single choice, and bounded text are implemented; add multiselect/form
  and wider grant scopes only when the native host exposes matching semantics.
- Bind every remote response to the installation, session/turn, native request, request digest,
  expiry, and one-time nonce. Reject replay, stale, changed, and wrong-session responses.

### A03 — Agent registry and heartbeat

Track live agent instances, projects, working directories, last activity, and waiting state so the center answers “which agents need me?” reliably.

### A04 — SDKs and protocols

- Status: protocol assembly and ARC 0.2 create/update/respond/resolve ingestion implemented;
  agent-host research is complete; auto-notify harnesses for all eleven hosts (OpenCode,
  Codex, Claude, Gemini, Copilot, Cursor, Muse, Kilo, OpenClaw, Hermes, Pi) implemented with
  `install-harness`, embedded payloads, JSON-preserving merges, and ask mode for Codex/Claude —
  real-host smoke pending; ACP bridge and language SDKs remain planned.
- Publish small PowerShell, shell, Python, JavaScript, and .NET clients without replacing the stable
  REST/CLI path.
- Implement an Agent Client Protocol client as the common managed-session bridge. Do not confuse it
  with the Agent Communication Protocol that moved into A2A.
- Add direct native adapters for existing sessions where hosts expose synchronous hooks, plugins,
  SDKs, gateways, or RPC. Notify-only harnesses cover eleven hosts; Codex/Claude ask mode,
  Hermes, and OpenClaw return human answers through their host surfaces. Other answer adapters
  remain planned (see [HARNESS.md](HARNESS.md)).
- Keep provider streams and ACP/A2A/AEP adapters behind the same validation and persistence
  boundary as direct ARC events.
- Treat A2A, both current Agent Event Protocol drafts, and MCP elicitation as optional projections
  after the interaction contract and response model stabilize.

## Provider router

### R01 — Local provider router

- Status: implemented behind `routerEnabled` (off by default) and covered by automated tests;
  no traffic to a real provider yet, and the Windows tray build has not been compiled with it.
- Loopback `/router/v1` endpoints for OpenAI Responses, OpenAI Chat Completions, and Anthropic
  Messages, authenticated with a router-specific key and refused to browsers.
- Upstreams with sealed keys and validated destinations; aliases and ordered failover combos;
  translation through one intermediate model with same-wire passthrough.
- Proxy-observed SQLite ledger with per-attempt rows, kept separate from Usage and Live quota.
- See [ROUTER.md](ROUTER.md).

### R03 — Routed models in each agent's own picker

- Status: Codex and Claude Code connectors implemented and verified live on macOS with the real
  agents against a scripted upstream; web page not yet seen in a browser.
- Codex: generated `model_catalog_json`, provider block with an embedded key, reasoning effort,
  subagent and review models, and shell tool.
- Claude Code: `modelPicker` rows with `behavesAs`, and the built-in entries' model variables.
- A copy before every write, restore of any copy, and disconnect that restores the owner's values.
- Remaining: OpenCode, Kilo, Cursor, and Gemini CLI connectors; real context windows per model.

### R04 — One-step providers and subscriptions

- Status: implemented on `feature/router-easy-setup`; model fetching, the ChatGPT plan, and OpenCode
  Go verified against the real services on macOS through a scratch broker; the Muse Code plan is
  untested because Muse Code is not installed on the test Mac.
- Provider gallery, fetched model lists with tick boxes, reuse of OpenCode's keys, per-model wires for
  OpenCode Zen and Go, and the ChatGPT-plan and Muse Code subscription upstreams.
- Remaining: a Muse Code sign-in kept in the OS keychain; AgentNotify's own sign-in flow for a plan
  instead of reusing another tool's; non-streaming requests to the ChatGPT backend, which only
  streams.

### R02 — Policy routing and richer combos

- Score candidates on capability, health, quota, cost, and latency evidence (`policy/<id>`), with a
  bounded, redacted decision trace.
- Status: ordered, sticky-until-failure, and round-robin same-model switching, one-way native Claude
  fallback, family-level effort mapping with per-model overrides, and Codex-wire effort translation
  are implemented and merged. Compiled and test-gated on macOS (1262 tests) and installed on the
  owner's Mac; the Windows/WSL gates, a human browser pass, and a real provider request are still
  outstanding — see `docs/VERIFICATION.md`.
- Weighted round-robin, weighted random, least-used, and reset-window combo strategies.
- Pin a Codex account pool; add Gemini and Ollama-native wires.
- Price ledger rows and correlate them with log-derived Usage records without double counting.
- Raise router spend and quota thresholds as ARC attention requests.

### R05 — Adaptive routing (prompt-to-model selection)

> Competitive landscape, surveyed 2026-09-18: the core idea (prompt-difficulty → cheaper model) is
> already shipped by several small projects — the open ground is integration quality, a Jev backend,
> and a local measurement loop. See the notes at the end of this section.

Downshift easy prompts to a cheaper capable model before route resolution, and keep hard ones on the
user's chosen model. Chosen over renaming the existing feature: "smart routing" already names
same-model cross-provider failover (see `docs/ROUTER.md`), which routes around a broken or exhausted
target; adaptive routing instead chooses *which model* serves the request by its difficulty, and the
two compose (adaptive selection first, then smart-routing failover on the chosen model).

- Stage 1 — local heuristics, no new dependency: token/character size, absence of code blocks and
  tool/function-call structures, and short-message patterns select a cheap tier defined by the owner
  on the Routing page. Must never apply to structured agent traffic by default (tool calls, subagent
  and review models, `wire_api = "responses"` requests with tool definitions).
- Stage 2 — BYOK decision model behind the same stage-1 interface: a web-UI toggle and encrypted key
  field (existing provider-secret envelope path, like API accounts) for [Jev by TypeSafe AI](https://typesafe.ai),
  an early-access, hosted, decision-only model that returns typed choices with calibrated
  probabilities in tens to hundreds of milliseconds. Opt-in per user; off (and stage-1 heuristics
  only) when no key is set.
- Jev receives the prompt text, which sends user prompt content to a third party — document this in
  ROUTER.md and SECURITY.md under the same consent framing as outbound channels. Never send: stored
  credentials, notification history, or non-router traffic.
- Confidence gate: downshift only on a calibrated high-probability decision; low confidence, a Jev
  error or timeout, or the router being on a native Anthropic request falls through to the user's
  chosen model untouched. A Jev outage must behave exactly as if adaptive routing were off.
- Ledger every adaptive decision (source `heuristic`/`jev`, chosen vs. requested model) as redacted
  rows beside the proxy ledger, and show the routing page which tier a request would get.
- Loopback promise intact by default: no request leaves the machine unless Jev (or a later BYOK
  decision model) is enabled with a key.

Competitive notes (surveyed 2026-09-18; star counts are snapshots):

- [BitRouter](https://github.com/bitrouter/bitrouter) (Rust, Apache-2.0, ~228★, 890 commits) — the
  serious competitor: local proxy + `policy-lock.yaml`, routes LLM calls, tools, and sub-agents per
  loop step, multi-account failover, spend caps, ACP adapters for Codex/Claude, claims ~33% cost
  cut on Terminal-Bench 2.1. Enterprise/devops-flavored; our wedge is a single opt-in toggle in a
  UI the owner already runs. Verify hands-on before committing to this feature's shape.
- [claude-model-router-hook](https://github.com/tzachbon/claude-model-router-hook) (MIT, ~83★) —
  Claude Code-only hook; heuristic classifier with optional Haiku fallback, effort-first tiers
  (mechanical→haiku up to extreme→opus), opt-in autoswitch, fail-open. Validates demand; single
  harness, no ledger.
- [pi-litellm-autorouter](https://github.com/CoresoftHQ/pi-litellm-autorouter) (MIT, 0★) — Pi
  extension; four-tier prompt classifier (heuristic or LLM), adaptive Thompson-sampling selection.
  A TypeScript port of LiteLLM's Auto Router v2 complexity classifier.
- [RouteLLM](https://github.com/lm-sys/RouteLLM) (Apache-2.0, ~5.5k★) — LMSYS research routers
  (matrix factorization, BERT, etc.) between a strong and weak model; up to 85% cost cut at 95%
  quality in benchmarks. Lab framework; Python/PyTorch runtime does not fit the C# broker.
- [Autohand Routes](https://github.com/autohandai/routes) (Rust, 4★) — policy-driven OpenAI-
  compatible routing layer (balanced/fastest/local-first/privacy-first policies); freshly
  open-sourced internal tooling, claims unverifiable.
- [spawn-router](https://github.com/Afterbuild/spawn-router) (MIT, 0★) — local DeBERTa-v3-small
  classifier (complexity/task-type/risk) on ONNX, ~7ms; classifies once per task, then locks the
  model.
- Harness built-ins route on availability or phase only (Claude Code `opusplan` and fallback
  chains, Copilot Auto), never on prompt difficulty — and each hosting vendor is incentivized not
  to downshift. Not Diamond sells hosted cloud routing for coding agents; prompts leave the machine.

Positioning: RouteLLM proved the idea in the lab, the hook projects shipped it per-harness,
BitRouter sells policy-driven multi-host routing, and Not Diamond sells it as a cloud API.
AgentNotify's remaining differentiators: the first decision-model backend (BYOK Jev, a class
nothing here uses yet), a routing/savings ledger feeding the Usage page, and one loopback toggle
across eleven harnesses.

## Product and platform tasks

- Local usage: read-only Claude Code, Codex, OpenCode, Kilo CLI, Muse Code, and Gemini CLI token,
  project, provider, recent-session, and per-request priced summaries are in the WebUI, natively and
  inside WSL. Add a durable/versioned file index with scan progress (a cold scan re-parses every
  ledger after each broker start), fork replay attribution, and historical rate schedules before
  treating the numbers as a spend ledger.
- Live quota: Codex's documented app-server RPC and Claude Code's first-party account-usage
  endpoint feed separately cached windows for the current, secondary (`.codex-*`/`.claude-*`), WSL,
  and up to 16 hand-added profiles. Every account can be renamed, moved, removed, and restored. A
  native macOS client now shows one status item per selected account and lists every account and
  returned window; Live quota configures enabled state, 5–60 minute refresh, and menu-bar accounts.
  OpenCode Go has a per-model published-cap estimate whose monthly window can follow the renewal
  day. Add a stable Claude source or statusline bridge, context-tiered Go rates, and opt-in,
  labelled unofficial sources for tools without a public quota API (Gemini CLI/Antigravity, Cursor,
  Copilot, Kilo; Muse Code only reports quota on a billed request).
- API accounts: encrypted, write-only keys show DeepSeek, Kimi, SiliconFlow, and OpenRouter balance
  or spend and OpenAI/Anthropic Admin cost history from official endpoints. Remaining: spend history
  derived from balance changes for balance-only providers, real-key verification of the OpenAI and
  Anthropic cost reports, and xAI's management-key billing.
- WSL on Windows: running distributions are discovered for Usage, Live quota, and skill installs
  (web interface, Settings, `install-skill --wsl`) and were checked on the owner's Windows machine.
  Remaining: a WSL-aware harness installer.
- Insights dashboard: implemented as a responsive, animated browser composition of live account
  balances, 30-day usage/cost, agent mix, daily trend, top projects, Go estimates, and broker health.
  It preserves quota-versus-local-history provenance and reduced-motion behavior.

- Search, filtering, export, route/delivery audit views, backups, and retention controls.
- Safer terminal/editor activation, Windows Terminal integration, and virtual desktop awareness.
- Signed x64/ARM64 releases, checksums, schema migration recovery, automatic updates, and rollback.
- Accessibility, keyboard navigation, localization, high-contrast support, multi-DPI/multi-monitor verification.
- Native macOS quota menu bar is implemented; extend it into a full notification-center/settings client and add a Linux tray/desktop implementation.
- Documentation/wiki site, examples, architecture decision records, contributor guides, and integration recipes. GitHub Pages is published by the repository workflow.
