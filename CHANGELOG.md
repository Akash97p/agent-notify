# Changelog

All notable changes to AgentNotify are recorded here. This project is a prerelease; the mature
`1.0.0` milestone is intentionally reserved and has not been reached. Versions follow the scheme in
[docs/RELEASING.md](docs/RELEASING.md), and any tag containing a hyphen is published as a GitHub
prerelease.

The release workflow reads the section for the tagged version out of this file and uses it as the
release description, so each entry should be written for someone deciding whether to install the
build.

## [Unreleased]

### Changed

- **Smart switching lives on the Routing page.** The Model router's one-card Settings page is folded
  into Routing, next to the routes and the unknown-model fallback it belongs with, so the group is
  five pages instead of six.
- **Effort mapping is one table per model family.** The Effort mapping page now opens on Families:
  every routed model is grouped by vendor family (DeepSeek, GLM, Kimi, Qwen, MiniMax, Grok, Muse,
  OpenAI, Claude), so a hundred models are ten cards instead of a hundred. The **Per model** tab
  keeps the exact per-`provider/model` override and wins over the family table.
- **Codex efforts are mapped too.** Claude Code's and Codex's `low/medium/high/xhigh/max` mean the
  same thing, so one table now serves either client. The automatic tables are identity wherever the
  target can spell a level — no client's effort is silently renamed, and only levels above a
  target's top collapse onto it (Claude's `max` at a four-level family becomes `xhigh`). OpenAI's
  `minimal` and provider-specific words pass through when the target accepts them, and otherwise
  fall back to the target's default.

### Added

- **Provider marks in the macOS menu bar.** The quota status item now shows the Claude starburst or
  the OpenAI knot (Codex) beside the percentage, and every account row in the dropdown carries its
  provider's mark, dimmed when that account is not in the headline selection. The marks ship as
  embedded vector path data with a small in-process parser; no image assets are added to the build.
- **Native macOS quota menu bar.** The broker starts a thin AppKit status item that shows the lowest
  selected Codex/Claude five-hour balance like a battery percentage. Its menu lists every monitored
  account and quota window, reset times, plan/credit data, stale state, manual refresh, and a link to
  Live quota. The WebUI controls enabled state, a 5–60 minute refresh interval, and which accounts
  drive the headline; the native child receives no bearer token or provider credential.
- macOS release archives now include `agentnotify-menubar`; the installer places and locally signs it
  beside `agentnotifyd`, and portable release packaging runs on macOS so Swift/AppKit can be built for
  Intel and Apple Silicon.


## [0.2.0-alpha.2] - 2026-09-18

The model router grows up. **Smart routing** is one switch: when a model fails at one provider (a
usage limit, an outage, a refused key), the same model is used from another provider you have, plans
first and pay-per-token APIs last. Every Codex account you're signed in to becomes its own ChatGPT-plan
provider automatically, providers are added in one step, and Claude Code keeps its own Claude models
and sign-in while it's connected to the router.

**Update both ends together:** after installing, reconnect Claude Code once under Model router →
Agents. It now receives the router key as `ANTHROPIC_CUSTOM_HEADERS`, not `ANTHROPIC_AUTH_TOKEN`.

Still unofficial: the ChatGPT plan and Muse Code plan providers reuse another tool's sign-in, which
those plans do not document. They're opt-in and labelled.

### Added

- **Smart routing.** One switch on Model router → Routing. The model you picked is tried first; if it
  fails, the same model from your other providers follows — each ChatGPT account, then OpenCode Go,
  then models on this computer, then pay-per-token APIs, always last. A model name several providers
  list starts with the cheapest instead of being refused as ambiguous. Your nicknames and fallback
  chains still work, with the same-model fallbacks added after them. The page lists which models it can
  switch and in what order.
- **Every Codex account is used.** Once the ChatGPT plan is added, each Codex account you're signed in
  to (the ones Insights shows, such as `~/.codex-second`) gets its own provider automatically, and a
  provider that was pointing at the same account as another is moved to the unused one.

- **Add a model provider in one step.** Providers is now a gallery: pick one, paste its key (or reuse
  the key OpenCode already has for it), tick models from the list the provider returns, and save. The
  models are immediately in your connected agents' model pickers as `provider/model`; no route is
  needed. Slug, API format, and base URL moved under Advanced.
- **New providers:** OpenCode Go and OpenCode Zen (each model on the API format it needs, from one
  key), Meta Model API for Muse Spark, and two subscriptions that reuse a sign-in already on your
  computer: **ChatGPT plan** through Codex's sign-in, and **Muse Code plan** through Muse Code's.
  Both subscriptions are unofficial and opt-in; AgentNotify stores no key for them.
- In Codex, models from the ChatGPT plan keep Codex's own prompt, context window, and reasoning
  levels, so they behave as they do without the router.

- **One list of agent accounts everywhere.** Second profiles such as `~/.codex-second` and
  `~/.claude-second` (and any account added under Live quota) now appear on Model router → Agents,
  where each can be connected on its own, and on Configuration → Agents, which shows each account's
  skill and hook harness with the exact command for that account.
- **Providers reuse what AgentNotify already knows.** A key saved under Live quota → API accounts
  (DeepSeek, Moonshot, SiliconFlow, OpenRouter) can be a provider's key without pasting it again; the
  provider follows that account when the key changes. The ChatGPT plan can be added once per Codex
  account, so a second Codex login is a second subscription provider.

### Changed

- The Configuration pages (Notifications, Sounds, Agents) come before the Model router in the
  navigation.
- Routing explains that routes are optional, and calls aliases **nicknames** and combos **fallback
  chains**. Targets and the fallback for unknown models are picked from your providers' models
  instead of typed.
- Claude Code's "Show only routed models" is a switch, off unless you turn it on.

### Fixed

- The "On" and "Key" badges in the provider list no longer spill over the form beside it.
- **Claude Code's own models keep working when it is connected to the router.** Opus, Sonnet, and
  Haiku used to fall through to the router's default route, or fail with `400 Invalid role system`.
  They now go to Anthropic with Claude Code's own sign-in. Connecting now writes the router key as
  `ANTHROPIC_CUSTOM_HEADERS` instead of `ANTHROPIC_AUTH_TOKEN`, so **reconnect Claude Code once** after
  updating.
- Disconnecting Claude Code while a session is open no longer breaks that session. Claude Code keeps a
  removed setting until it restarts, so the session keeps talking to the router, and its own models
  pass through.
- Reconnecting an agent and then disconnecting it no longer leaves the router's base URL behind.
- Routed models no longer fail on Claude Code's mid-conversation system messages or on content blocks
  another provider cannot carry.
- The Routing page's route list no longer spills over the form beside it; a fallback chain lists one
  model per line.
- The ChatGPT plan now answers requests that don't stream. Its backend only streams, so the router
  streams to it and collects the answer.
- A provider that refuses its key (`401`/`403`) or doesn't serve a model it lists (`404`) now hands the
  request to the next target instead of failing it.
- Router errors now say why a provider refused: `Upstream returned HTTP 429 (usage_limit_reached)`
  instead of the bare status. A provider out of credit (`402`) fails over to the next target, and when
  every target is rate limited the agent gets `429` with `retry-after` instead of a bare `503`.

## [0.2.0-alpha.1] - 2026-09-18

This release adds a local model router. AgentNotify can now sit between your coding agents and the
model providers you pay for: Codex and Claude Code can run on DeepSeek, OpenRouter, Kimi, Z.ai, Groq,
or a model on your own machine, pick those models from their own `/model` menus, and fail over to
another provider when one is rate-limited or down. It is off until you turn it on, and nothing else in
AgentNotify changes when it is off. The minor version moves to 0.2 because this is a new capability
rather than a refinement of the notification broker.

Not yet exercised against a real provider: every live check so far used a local stand-in upstream,
with the real Codex and Claude Code clients. Treat provider-specific behaviour as unproven.

### Added

- **Provider router (opt-in, off by default).** The broker can act as a local proxy for coding
  agents: point Codex, Claude Code, or any OpenAI-compatible client at `http://127.0.0.1:<port>/router`
  and the router picks the upstream provider and model for each request. It speaks OpenAI Responses,
  OpenAI Chat Completions, and Anthropic Messages on both sides and translates between them, so Codex
  can run on DeepSeek, OpenRouter, Kimi, Z.ai, or a local Ollama model, and Claude Code can run on an
  OpenAI-compatible provider. A same-wire request is passed through untouched apart from the model
  name. Model selectors are `provider/model`, an alias, `combo/<name>`, a bare model declared by one
  upstream, or the default route. A combo tries its targets in order and fails over on a connection
  error, timeout, 408, 429, 5xx, or 529 — but never once a byte has reached the client, because a
  half-delivered stream cannot be resent. Failed targets cool down, honoring `Retry-After`.
  Upstream keys are encrypted with the same protector as channel secrets, only travel to their own
  validated `https` destination (plain `http` only for a model server on this computer), and never
  appear in any response, log, or ledger row. The router has its own key, separate from the `/v1`
  bearer token, so an agent's config file holds a credential that can spend money but cannot read
  notifications; requests carrying a browser `Origin` are refused. Every request and every physical
  attempt is recorded in SQLite with token counts and outcome, pruned after 30 days. See
  [docs/ROUTER.md](docs/ROUTER.md).
- **Router page** in the web interface: the on/off switch, a one-time key reveal, copyable Codex and
  Claude Code setup snippets, upstream and route management, the default route, and the request
  ledger with per-attempt detail. The router ledger is kept visibly separate from the Usage page:
  they observe the same calls from different sides and must not be added together.
- **Routed models in the agents' own `/model` menus.** One Connect button (or
  `agentnotify router connect codex|claude_code`) writes that agent's configuration so every routed
  model, alias, and combo appears in the picker it already has. Codex gets a generated model catalogue
  and a provider block carrying the router key; its reasoning effort, subagent model and effort,
  review model, and shell tool are set from the same page. Claude Code gets rows in its `/model`
  picker that declare which known model each behaves as, plus its Opus, Sonnet, Haiku, and background
  entries pointed wherever you choose. Every write copies the file first, the Agents page lists those
  copies and restores any of them, and Disconnect puts your own settings back. Verified live: real
  Codex executed a tool call through the router, and real Claude Code answered through it.
- The router's pages now sit together under **Model router**: Providers, Routing, Agents, Activity.
- `agentnotify router key`, `status`, `agents`, `connect`, and `disconnect`.

## [0.1.0-alpha.4] - 2026-09-17

This release is mostly about seeing where coding-agent time and money go on a Windows machine whose
agents run inside WSL, and across more agents and providers.

### Added

- **Agents inside WSL are no longer invisible on Windows.** Usage, Live quota, and the Agents page
  include every running WSL distribution: agent history from its home, a quota card for each
  `~/.codex` and `~/.claude` found there (Codex is asked from inside the distribution), and skill
  installs labelled `WSL · <distribution>` in the web interface and the tray's Settings. The home is
  the default user's — the one named by `[user] default=` in `/etc/wsl.conf` when set, otherwise the
  registered default user. A WSL profile such as `\\wsl.localhost\Ubuntu\home\you\.codex` can also be
  added by hand, and `agentnotify.exe install-skill <agent> --wsl <distribution>` installs from the
  command line (the WSL wrapper picks the calling distribution). Stopped distributions are not read,
  so opening the dashboard never starts one.
- **Usage now includes Muse Code, the Kilo CLI, and the Gemini CLI**, natively and inside WSL,
  grouped by project.
- **Cost estimates cover far more models, priced per request.** Official rates were added for
  GPT-5.5, GPT-5.4 (and Mini/Nano), GPT-5.3/5.2/5.1 Codex, GPT-5.1 Codex Max, GPT-5 Codex, Meta Muse
  Spark 1.1–1.3 (standard and Contributor), Gemini 2.5 Pro/Flash and 3.x, Z.ai GLM-5.x/4.7, Xiaomi
  MiMo V2.5, OpenCode Zen and Kilo free models, and DeepSeek V4 on OpenCode Go. Long-context requests,
  Codex fast mode, and OpenCode Go peak hours use the rate that actually applied.
- **API accounts show provider balances and spend.** Store a DeepSeek, Kimi, SiliconFlow, or
  OpenRouter API key, or an OpenAI or Anthropic Admin key, and Live quota shows that account's balance
  or spend from the provider's official endpoint, leading with one balance per card at the top of the
  page. Keys are encrypted for this user, never shown again, and need an explicit risk
  acknowledgement. Meta, Z.ai, Xiaomi, Google, and xAI offer no usable official endpoint and are not
  listed.
- **Secondary Codex and Claude Code profiles are found automatically.** A `.codex-<name>` or
  `.claude-<name>` folder (also with `_`) directly under your home or a running WSL distribution's
  home appears as its own Live quota card once it holds that agent's sign-in, with its history in
  Usage.
- **The OpenCode Go monthly estimate can follow your billing cycle.** Set the day your plan renews
  and the monthly bar counts from the latest renewal and shows when it resets.

### Changed

- **Every Live quota account can be edited or removed.** Each account — including the built-in and
  discovered ones — has an editable name and profile directory and a **Remove** button; removed
  detected accounts can be restored. Usage also counts the history of profiles added by hand.
- **Updating no longer starts from scratch.** Running a newer `AgentNotifySetup.exe` finds the
  existing installation and offers **Update AgentNotify**: it keeps the install folder and
  startup/shortcut choices, does not ask for the licence again, and stops and restarts AgentNotify
  cleanly. An `agentnotify.exe` an agent is still waiting on is replaced without being killed.
  `AgentNotifySetup.exe --silent` updates without `--accept-license`; `--no-launch` leaves the tray
  stopped.
- **Scanning agent history inside WSL is much faster.** Files are listed from inside the
  distribution and read in large chunks, and only lines that can carry usage are parsed. On a machine
  with about 4.8 billion tokens of WSL history a first scan fell from over six minutes to about two,
  and later visits take about a second. Parsed history is still kept only in memory, so the first
  visit after AgentNotify starts pays that scan.
- The Usage response is now `contract_version: "4"` and the Live quota response
  `contract_version: "3"`; the web interface and broker must be updated together.

### Fixed

- **Usage and Live quota no longer take half a minute on every visit when OpenCode runs in WSL**, and
  OpenCode usage there is no longer silently missing: its database was queried page by page over the
  WSL share and could time out. It is now re-read only when it changes, from a quick local copy.

## [0.1.0-alpha.3] - 2026-09-16

### Added

- **ARC 0.2 — the contract can now carry the answer back.** 0.1 could say a person was needed
  but not what was being asked, and had no way to return what the person said. A request may
  now carry a `response` specification — a permission, one of a bounded list of choices, or a
  short text, with a deadline — which opens a durable question alongside the notification. A
  new `response.submitted` event delivers one answer, bound to the exact question by the
  digest and nonce the broker issued, settled first-valid-wins. `request.outcome` records why
  a condition closed, and `context` gained `turn_id` and `native_request_id` so a host adapter
  can return an accepted answer into the call blocking on it. `POST /v1/events` now returns
  `{ "notification": …, "interaction": … }` for every event. See `docs/ARC.md` and the
  published `schemas/arc-0.2.schema.json`.

- **A web interface, on every platform.** The broker now serves its own settings and
  notification center at `http://127.0.0.1:<port>/ui/`, from both `agentnotifyd` and the
  Windows tray app. macOS and Linux had no way to configure channels, routes, custom types or
  sounds except by editing files; now `agentnotify ui` opens all of it in a browser: overview,
  attention, questions (answer permissions, choices and text), channels (all nineteen, including
  connecting a Relay), routes and delivery counts, notification and toast settings, custom
  types, sounds with upload and preview, and agent skill installs. On Windows the tray menu
  gains **Web interface…**.
  Like the tray app it asks for no password. It defends against other web sites instead:
  foreign host names are refused (DNS rebinding), state changes need a custom header and a
  same-origin `Origin`, a strict content security policy applies, and stored credentials and
  question nonces are never sent to the page. See `docs/WEB_UI.md`.
- `agentnotify ui [--print]`.
- Provider validation for every channel now lives in Core (`ProviderFormCatalog`), so a
  profile saved from the browser is validated exactly as one saved from the Settings window.

- **The skill can now ask.** The distributable `SKILL.md` documented `send`, `resolve`
  and `list` only, so an agent had no way to know it could raise a bounded question — and
  the harness covers `permission` alone, which left `single_choice` and `text` with no
  path at all. It now covers both, with `interactions wait` to block for the answer, and
  the rules that make an asked question answerable: offer only choices you will honour,
  include an escape option, put the whole question in the prompt because a lock screen is
  all the context the user gets, and never raise a `permission` yourself because the
  harness already owns those. Mirrored into the portable snippet in `docs/AGENT_SKILLS.md`
  for agents without skill discovery.

### Changed

- **Relay is hosted only.** The deployment selector and the "Relay server base URL" field are
  gone from the Channels panel, the web interface and the CLI: there is one endpoint, and
  connecting is just **Connect** plus a browser approval. `agentnotify relay pair` no longer
  needs `--url`. Profiles saved by an older build keep working — a leftover `deployment` key
  is ignored rather than rejected.
- **AgentNotify Relay is not open source.** Its repository links are gone from the
  documentation and the site; the service, the mobile app and the receiver SDKs are still
  documented, as products rather than as repositories. Running your own relay is no longer a
  supported path.
- The tray menu's web interface entry is named **Web interface…** rather than
  **Open in browser…**, which read as a second way to open the Settings window.
- `arc_version` must be exactly `"0.2"`. ARC 0.1 events are rejected; the 0.1 schema stays
  published so its URL keeps resolving, but it is no longer normative.

### Fixed

- The `0.1.0-alpha.1` notes named `agentnotify ask …` and `agentnotify answer` as the
  CLI surface for interactions. Neither command has ever existed — both fall through to
  the `send` shorthand and quietly create a notification titled "ask" or "answer". The
  real commands are `agentnotify interactions request` and `agentnotify interactions
  respond`; the entry below has been corrected. The published release notes for that tag
  still carry the wrong names.

### Verified

- All three interaction kinds were answered from a paired device and delivered to the
  broker on 2026-09-13: `permission` (allow/deny), `single_choice` (a four-option list
  with per-choice detail lines), and `text` (free text, round-tripped intact). Each
  arrived with `source: relay` and the answering device's id. See
  [docs/VERIFICATION.md](docs/VERIFICATION.md).

## [0.1.0-alpha.2] - 2026-09-12

A fix release for the bidirectional loop shipped in `0.1.0-alpha.1`. Nothing here changes
how you use it; all four items are cases where a question, or an answer to one, could be
left in a state that told you something untrue.

### Fixed

- **A wait no longer reports a dead question as still pending.** Nothing woke a waiting
  caller when a question simply ran out of time, and the timeout path returned the stored
  row without settling it first. `agentnotify interactions wait` could answer `pending`
  for a question whose deadline had already passed, and supervised ask mode spent another
  two-minute slice on it before falling back to the local prompt.
- **Supervised ask mode cancels the question when it gives up.** On any fallback to the
  local prompt — the broker timing out, an unreadable result, an unexpected choice — the
  question was left open. Your phone kept showing a live card, counting down, for a
  decision the session had already made on its own; tapping it was accepted and applied to
  nothing, and the phone still said the answer had been recorded. The hook now cancels the
  question on its way out, and clears its own "waiting for approval" notification, which
  previously stayed in the notification centre after the decision was made.
- **Answers that could never be applied are refused at the Relay instead of stored.** The
  Relay accepted a longer answer than the broker will take, replied that it had recorded
  it, and the desktop then discarded it as malformed on the next poll — with nothing
  telling you. Requires the matching Relay release.
- **A long-running broker no longer leaks a small amount of memory per question asked.**
  One empty waiter list was retained for every question ever waited on.

## [0.1.0-alpha.1] - 2026-09-12

AgentNotify is now bidirectional. Until now your coding agents could only get your
attention; now they can ask you things. An agent raises a question — pick one of these
options, or type an answer — and carries on with something else while you decide. Your
answer is waiting when it checks back: in a toast action, on the command line, or through
the local API. Questions expire, only the first answer counts, and anything late or
replayed is rejected rather than delivered twice.

Two agents already use it end to end. Codex and Claude Code run in a supervised mode
where every shell command and every file write comes to you for approval first, with the
exact command or diff shown before you allow it, and the choice of allowing once, always,
or never is remembered per session.

### Added

- **Two-way interactions.** Permission prompts, bounded choice lists, and plain text
  questions, raised from the tray, the CLI (`agentnotify interactions request`), or the
  loopback `/v1/interactions` API. Answers come back through toast buttons,
  `agentnotify interactions respond`, or the API. Deduplication, expiry, and
  first-answer-wins are enforced by a durable broker, so a retried or replayed answer can
  never be applied twice.
- **Supervised ask mode for Codex and Claude Code.** Opt-in per session: shell commands
  and file writes pause for your approval, with full details shown before you decide.
  Allow-once, allow-always, and deny are remembered for the session, and the session ends
  cleanly — no stranded prompts — when you quit or it times out.
- **Thirteen agent harnesses.** OpenCode, Codex, Claude Code, Gemini CLI, Copilot CLI,
  Cursor, Muse, Kilo Code, OpenClaw, Hermes, and Pi all report completions,
  failures, and blockers in their own words, and every one of them can raise a question
  back through the same harness.
- **Phone answers in transit.** The desktop publishes sealed question payloads through
  the existing Relay envelopes and polls for answers, so once the Relay and the mobile
  app implement their halves of the published contract either side can answer from the
  phone. The contract ships in the repository as `docs/RELAY_INTERACTIONS.md`; the Relay
  endpoints and the mobile answer cards are not in this build.

### Known limitations

- Answering from the phone is not wired up yet — the Relay endpoints and the mobile UI
  are specified but unimplemented, so questions can only be answered on the computer
  itself in this build.
- Binaries are adhoc-signed, not notarized or Authenticode-signed. macOS quarantines a
  fresh download until you run `xattr -dr com.apple.quarantine <dir>`; Windows
  SmartScreen will warn.
- There is still no graphical application on macOS or Linux — the broker runs headless
  and every setting is edited in `config.json` by hand.

## [0.0.4-alpha.2] - 2026-09-04

The previous release shipped Relay but said plainly that it did not yet deliver
end-to-end confidentiality: the desktop sent an experimental opaque transport rather
than a sealed box. **It does now.** Notifications are sealed on your computer for one
specific device and can only be opened there — the relay stores ciphertext it cannot
read, and the push that wakes your phone carries no content at all.

There is also a phone to receive them. The Android client is real, and a receiver no
longer has to be a phone: an ESP32, a Raspberry Pi or anything that speaks MQTT can be
added from the console and will get the same sealed envelopes.

Still an alpha, and the binaries are unsigned.

### Added

- **End-to-end encrypted notifications.** Envelopes are sealed with X25519 key agreement
  and XChaCha20-Poly1305. Your phone generates its own key pair and the relay only ever
  sees the public half, so a relay operator — including you — cannot read what passes
  through. The sender, the recipient, the key, the event id and the expiry are all bound
  into the envelope's authenticated data, so none of them can be swapped in transit.
- **An Android client.** The AgentNotify Relay mobile app
  pairs by scanning a QR code from the console. Notifications arrive whether or not the app
  is open, and an operator can sign in to see senders, receivers and the audit log from the
  phone.
- **Hardware receivers over MQTT.** The console's Receivers tab can add a device that has
  no camera and no app store — an ESP32 display, a Raspberry Pi, a desk gadget. It is given
  broker credentials and a single-use enrollment token, generates its own key pair on first
  boot, and receives the same sealed envelopes. Seven receiver libraries are published for
  portable C, Arduino, ESP-IDF, MicroPython, Rust, Python and Node.
- **An Install tab, and `Install agent skill…` in the tray menu.** One row per coding agent,
  showing where the skill will go and whether it is already there and current. Claude Code,
  Codex and OpenCode have known locations; anything else installs into a folder you pick.
  `agentnotify install-skill opencode` does the same from a terminal.
- **A device page in the Relay console.** Clicking a receiver's name opens its connection
  details — broker address, username, topics, whether it ever enrolled — rather than only
  offering a rename. The broker password and enrollment token cannot be shown again, because
  neither is stored in a readable form, so the dialog offers to replace them instead.

### Changed

- **Reconnecting a computer no longer creates a duplicate.** A sender now carries a stable
  installation identity across reconnects, so pressing Connect again re-enables the existing
  entry instead of leaving a second row with the same name beside a phantom of the first.
  Reconnecting a revoked computer says so before you approve it.
- **Revoking and deleting are separate everywhere.** Revoking a sender or a receiver stops it
  immediately and keeps the row, marked, so you can still tell what it was; deleting is a
  second, deliberate act offered only on an already-revoked row.
- **The Getting Started page** was rewritten to match the application's own appearance, and
  corrected: it had been telling people to install the Codex skill to `~/.codex/skills`, a
  path the CLI has never used.
- **The bundled agent skill** now tells an agent that a notification may be forwarded to a
  phone, and to write accordingly. "Which one?" is useless on a lock screen.

### Fixed

- A notification deleted on the phone could reappear as new on the next refresh. The delivery
  worker wrote a job's status back unconditionally after a push, and because the push is what
  causes the phone to fetch, an acknowledgement arriving mid-flight was overwritten — leaving
  the notification pending forever while the phone believed it had dealt with it.
- Deleting a receiver failed with a foreign-key error when it had ever been through pairing.

### Verified

- The macOS build ran on real hardware for the first time (Intel, macOS 26.6.2), and the
  `osascript` notification backend was seen displaying a banner. Apple Silicon has still never
  executed one of these binaries, and the Relay channel has not been exercised on macOS.
  [VERIFICATION.md](docs/VERIFICATION.md) records exactly what was and was not observed.

### Known limitations

- Binaries are adhoc-signed, not notarized or Authenticode-signed. macOS quarantines a fresh
  download until you run `xattr -dr com.apple.quarantine <dir>`; Windows SmartScreen will warn.
- There is still no graphical application on macOS or Linux — the broker runs headless and
  every setting is edited in `config.json` by hand.
- Sticky attention types degrade to ordinary banners under `osascript`.

## [0.0.4-alpha.1] - 2026-08-31

Adds AgentNotify Relay: a service that carries notifications from your computers to your phone,
without routing them through somebody else's messaging product. Connecting a computer is now a
browser approval rather than a pasted token.

Relay is a separate service, documented at
[Relay](https://akash97p.github.io/agent-notify/docs/relay/). At this release the mobile client did
not exist yet, and the desktop still sent an experimental opaque transport rather than a sealed box,
so end-to-end confidentiality was not yet delivered.

### Added

- **Browser-based AgentNotify Relay connection.** The Windows Channels panel now discovers a Relay,
  opens its approval page, displays a short verification code, polls with cancellation and bounded
  retry, verifies the approved installation, and saves the one-time credential only when the
  provider is saved. Manual credential entry remains under a collapsed Advanced section.
- **Headless Relay pairing.** `agentnotify relay pair` runs the same device-authorization handshake
  on Windows, macOS, and Linux, while `agentnotify relay status` verifies saved Relay providers.
  `--json` provides line-delimited state transitions for scripts.

### Security

- Relay discovery, pairing, verification, and delivery now share one redirect-free, proxy-free,
  DNS-pinned HTTP transport. Browser approval URLs must match the configured Relay origin exactly;
  polling and installation credentials are never placed in URLs, UI text, logs, or exceptions.

### Fixed

- Relay test sends no longer submit a synthetic `relay-placeholder-device` after sender pairing.
  An installation with no active phone now returns `no_devices_paired` locally and the Channels UI
  explains the required next step; an unknown pinned device is rejected before any envelope is sent.
- **Every Relay send failed with `relay_400`.** Two serialization details were rejected by the
  Relay before any handler ran: `expires_at` used `DateTimeOffset.ToString("O")`, which emits a
  numeric offset instead of the trailing `Z` the schema accepted, and absent optional metadata was
  written as an explicit `null`. Timestamps now use the `Z` form and nulls are omitted.
- **The route editor's provider dropdown showed a type name** instead of each provider's name, so
  every entry looked identical. The themed ComboBox draws the closed control from
  `SelectionBoxItemTemplate`, which `DisplayMemberPath` alone leaves empty; an explicit
  `ItemTemplate` now drives both the list and the selection.
- **Envelopes carried a `sender_id` that did not match the value sealed into the ciphertext.** The
  recipient rebuilds its authentication data from the `sender_id` it is given, so the mismatch
  would have made every envelope fail authentication on the device once the transport is really
  encrypted. Both now come from one value.
- **The setup window and settings header rendered a soft, low-resolution logo.** Windows selects a
  small icon frame for an image with an explicit size; on-screen logos now use the 512px source
  while window and executable icons keep the multi-resolution `.ico`.

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

[0.1.0-alpha.4]: https://github.com/Akash97p/agent-notify/releases/tag/v0.1.0-alpha.4
[0.1.0-alpha.3]: https://github.com/Akash97p/agent-notify/releases/tag/v0.1.0-alpha.3
[0.1.0-alpha.2]: https://github.com/Akash97p/agent-notify/releases/tag/v0.1.0-alpha.2
[0.1.0-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.1.0-alpha.1
[0.0.3-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.0.3-alpha.1
[0.0.2-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.0.2-alpha.1
[0.0.1-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.0.1-alpha.1
