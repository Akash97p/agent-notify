# Changelog

All notable changes to AgentNotify are recorded here. This project is a prerelease; the mature
`1.0.0` milestone is intentionally reserved and has not been reached. Versions follow the scheme in
[docs/RELEASING.md](docs/RELEASING.md), and any tag containing a hyphen is published as a GitHub
prerelease.

The release workflow reads the section for the tagged version out of this file and uses it as the
release description, so each entry should be written for someone deciding whether to install the
build.

## [Unreleased]

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

- **Two-way interactions.** Choice prompts (with optional free text) and plain text
  questions, raised from the tray, the CLI (`agentnotify ask choice …` /
  `agentnotify ask text …`), or the loopback `/v1/interactions` API. Answers come back
  through toast buttons, `agentnotify answer`, or the API. Deduplication, expiry, and
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
- Relay is self-hosted only. There is no hosted service.

## [0.0.4-alpha.2] - 2026-09-04

The previous release shipped Relay but said plainly that it did not yet deliver
end-to-end confidentiality: the desktop sent an experimental opaque transport rather
than a sealed box. **It does now.** Notifications are sealed on your computer for one
specific device and can only be opened there — the relay stores ciphertext it cannot
read, and the push that wakes your phone carries no content at all.

There is also a phone to receive them. The Android client is real, and a receiver no
longer has to be a phone: an ESP32, a Raspberry Pi or anything that speaks MQTT can be
added from the console and will get the same sealed envelopes.

Still an alpha. The binaries are unsigned, the hosted Relay Go service does not exist,
and Relay remains something you host yourself.

### Added

- **End-to-end encrypted notifications.** Envelopes are sealed with X25519 key agreement
  and XChaCha20-Poly1305. Your phone generates its own key pair and the relay only ever
  sees the public half, so a relay operator — including you — cannot read what passes
  through. The sender, the recipient, the key, the event id and the expiry are all bound
  into the envelope's authenticated data, so none of them can be swapped in transit.
- **An Android client.** [agent-notify-relay-mobile](https://github.com/Akash97p/agent-notify-relay-mobile)
  pairs by scanning a QR code from the console. Notifications arrive whether or not the app
  is open, and an operator can sign in to see senders, receivers and the audit log from the
  phone.
- **Hardware receivers over MQTT.** The console's Receivers tab can add a device that has
  no camera and no app store — an ESP32 display, a Raspberry Pi, a desk gadget. It is given
  broker credentials and a single-use enrollment token, generates its own key pair on first
  boot, and receives the same sealed envelopes. Seven receiver libraries are published at
  [agent-notify-relay-sdk](https://github.com/Akash97p/agent-notify-relay-sdk): portable C,
  Arduino, ESP-IDF, MicroPython, Rust, Python and Node.
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
- Relay is self-hosted only. There is no hosted service.
- Sticky attention types degrade to ordinary banners under `osascript`.

## [0.0.4-alpha.1] - 2026-08-31

Adds AgentNotify Relay: a self-hostable service that carries notifications from your computers to
your phone, without routing them through somebody else's messaging product. Connecting a computer
is now a browser approval rather than a pasted token.

Relay is a separate open-source project —
[github.com/Akash97p/agent-notify-relay](https://github.com/Akash97p/agent-notify-relay) — and is
documented at [Relay](https://akash97p.github.io/agent-notify/docs/relay/). The mobile client and
the hosted Relay Go plan do not exist yet, and the desktop still sends an experimental opaque
transport rather than a sealed box, so end-to-end confidentiality is not yet delivered.

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

[0.1.0-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.1.0-alpha.1
[0.0.3-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.0.3-alpha.1
[0.0.2-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.0.2-alpha.1
[0.0.1-alpha.1]: https://github.com/Akash97p/agent-notify/releases/tag/v0.0.1-alpha.1
