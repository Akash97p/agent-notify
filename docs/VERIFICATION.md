# Verification record

Date: 2026-08-12
Environment: Windows 11 host, WSL workspace, Windows .NET SDK 10.0.302 at `/mnt/d/dev/dotnet/dotnet.exe`, x64 publish target.

This record distinguishes automated/process verification from visual checks. No result from the inherited `/mnt/d/dev/AgentNotify` documentation was accepted without rerunning it.

## Verified

### Release build

Command:

```bash
./scripts/build.sh --no-restore
```

Result:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

All seven projects built: Contracts, Core, API, WPF App, CLI, Setup, and Tests.

### Automated tests

Command:

```bash
./scripts/test.sh --no-restore
```

Result:

```text
Passed: 597
Failed: 0
Skipped: 0
```

Coverage includes validation, lifecycle transitions, config recovery/round-trip, custom type normalization/definitions/default priority, managed sound import/sanitization/deduplication and sound profile resolution, SQLite notification and delivery migrations/CRUD/outbox/attempts, atomic concurrent outbox claiming, DPAPI/AES-GCM secret round-trip and redaction, provider input bounds, credential-preserving/merging/removal updates, validated route service updates, idempotent route materialization, dispatcher success/timeout/retry/dead-letter/recovery, sanitized diagnostics and bounded test-send, webhook templating/headers/HMAC/idempotency/status policy/private-address controls, SMTP message projection/authentication/TLS modes/recipient normalization/injection and address policy/status propagation, Telegram encrypted destination projection/plain-text limits/fixed endpoint JSON/status policy, Discord encrypted URL validation/mention suppression/Markdown escaping/content limits/thread/status policy, Slack/GovSlack URL validation/control-sequence suppression/threading/bounded response/status policy, Teams/Zoho Cliq/Google Chat URL and payload hardening, Mattermost self-hosted destination/acknowledgement/mention controls, Matrix/ntfy/Gotify/Pushover/Pushbullet/Twilio SMS/direct WhatsApp/Twilio WhatsApp/MQTT encrypted destination and acknowledgement contracts, routing/retry policy, authentication, health, create/list/get/patch/dismiss, malformed/null JSON, callback isolation including outbound queue failure, keyed deduplication including concurrent calls, CLI connection failure, bare `--unresolved`, hyphenated and custom notification type parsing, invalid identifier rejection, and version output.

### Delivery security foundation

Windows tests successfully round-trip a `dpapi-user:v1` envelope with current-user scope. Repository tests persist a provider secret, confirm the stored value is encrypted and the public profile omits both plaintext and ciphertext, decrypt only through the delivery-only service, preserve credentials on non-secret edits, reject malformed profile shapes and future schema versions, atomically claim a single item across concurrent workers, record schema migrations v1/v2, and exercise the route/outbox/attempt lifecycle. Network adapters are deliberately not active in this milestone.

### Durable dispatcher

Schema migration v2 adds a unique notification/route key so repeated coordination cannot duplicate delivery. Automated tests prove route filtering, atomic/idempotent enqueue, successful delivery, secret availability only at the adapter boundary, timeout-to-retry, retry exhaustion to dead-letter, interrupted-claim recovery, exact redacted diagnostics, bounded test-send, and isolation of outbox persistence failure from the already-committed local API response. Adapters perform no work without an explicitly enabled provider and matching route.

### Generic HTTPS webhook

Contract tests use an in-memory HTTP handler and verify JSON template expansion, encrypted endpoint/secret-header use, HMAC-SHA-256 calculation, timestamp and idempotency headers, status retry classification, and rejection before send for HTTP, URI credentials/fragments, missing secrets, newline injection, loopback, link-local metadata, multicast, private, benchmarking, and documentation addresses. The production handler disables redirects, cookies, ambient proxies, connection reuse, and response-body reads; its connect callback resolves and validates all destination IPs immediately before opening the socket. No external endpoint was contacted during automated verification.

### Channel management UI

The native Channels tab compiles with provider, route, and diagnostics sub-tabs. Service tests prove secret edits merge without plaintext UI readback, explicit optional-secret removal, route filter normalization, creation-time preservation, and missing-provider rejection. The UI starts new profiles/routes disabled, keeps stored password fields blank, requires explicit private-network and message-content consent, confirms cascade deletion, and calls the bounded dispatcher test path. Visual layout, keyboard flow, screen-reader behavior, and a real external webhook test remain human verification items.

### SMTP email

Contract tests with an injected sender verify strict STARTTLS and TLS-on-connect selection, encrypted username/password projection, recipient normalization/deduplication, invalid-address and CRLF-injection rejection, private/link-local destination rules, subject/body construction, route message redaction, and sanitized transport-result propagation. MailKit 4.17.0 compiles into the self-contained Windows target. No SMTP credentials were assumed and no external mail server was contacted; a real-server interoperability test remains explicitly unverified.

### Telegram Bot

Contract tests verify encrypted bot-token/chat projection, strict token/chat/topic validation, default content protection, silent/topic options, route message redaction, 4096-character surrogate-safe truncation, plain JSON without a markup mode, fixed official endpoint construction, token omission from the JSON body, link-preview suppression, malformed-response handling, and retry/permanent HTTP classification. The production handler pins connections to public DNS results for `api.telegram.org` and disables redirects, cookies, and ambient proxies. No Telegram token or destination was assumed and no external message was sent; real-bot interoperability remains explicitly unverified.

### Discord incoming webhook

Contract tests verify encrypted webhook use, strict official host/port/path/id/token/query validation, versioned API paths, optional thread routing, `wait=true`, display-name projection, route message redaction, Markdown escaping, complete mention suppression, 2000-character surrogate-safe truncation, token omission from JSON, network sanitization, and retry/permanent HTTP classification. The production handler pins connections to public DNS results for `discord.com` and disables redirects, cookies, proxies, decompression, and response-body reads. No webhook credential was assumed and no external Discord message was sent; real-webhook interoperability remains explicitly unverified.

### Slack incoming webhook

Contract tests verify encrypted webhook use, strict Slack/GovSlack host/port/path/token/query validation, optional thread timestamp, `mrkdwn` and automatic link-name suppression, control-sequence encoding, route message redaction, 4000-character surrogate-safe truncation, token omission from JSON, bounded `ok` acknowledgement handling, oversized/malformed success handling, network sanitization, and retry/permanent HTTP classification. The production handler pins connections to public DNS results for the selected official hook host and disables redirects, cookies, proxies, and decompression. No webhook credential was assumed and no external Slack message was sent; real-webhook interoperability remains explicitly unverified.

### Microsoft Teams Workflows

Contract tests verify encrypted signed-URL use, current global Power Platform host/path/query validation, rejection of HTTP/foreign/unsigned/retired shapes, Adaptive Card 1.2 envelope construction, route message redaction, Markdown and mention-tag escaping, bounded surrogate-safe text, token omission from JSON, network sanitization, and retry/permanent HTTP classification. The production handler validates every public DNS result before connecting and disables redirects, cookies, proxies, decompression, and response-body reads. No workflow credential was assumed and no external Teams card was sent; real-workflow and sovereign-cloud interoperability remain explicitly unverified.

### Zoho Cliq

Contract tests cover all nine documented regional hosts, encrypted URL use, channel-name/channel-ID/bot path validation, strict token/query rules, route redaction, Markdown escaping, 5000-character surrogate-safe truncation, token omission from JSON, and retry/permanent HTTP classification. The production handler validates every public DNS result before connecting and disables redirects, cookies, proxies, decompression, and response-body reads. No Cliq credential was assumed and no external message was sent; real-webhook interoperability remains explicitly unverified.

### Google Chat

Contract tests verify encrypted webhook use, strict official host/port/path/key/token validation, optional thread creation/reuse with both documented reply policies, route message redaction, formatting and user/everyone mention neutralization, serialized UTF-8 payload enforcement below 32,000 bytes, cancellation-aware write spacing, credential omission from JSON, network-error sanitization, and retry/permanent HTTP classification. The production handler validates every public DNS result for `chat.googleapis.com` immediately before connecting and disables redirects, cookies, proxies, decompression, and response-body reads. No Google Chat webhook credential was assumed and no external message was sent; real-webhook and human Settings interoperability remain explicitly unverified.

### Mattermost

Contract tests verify encrypted URL use, HTTPS and terminal hook-path validation, self-hosted subpaths and custom ports, rejection of query/fragment/user-info and malformed tokens, explicit private-network consent while always rejecting link-local metadata, optional silent delivery, route redaction, Markdown and Mattermost/Slack-compatible mention neutralization, 16,383-character surrogate-safe truncation, bounded `ok` acknowledgement handling, oversized/malformed success handling, network sanitization, and retry/permanent HTTP classification. The production handler validates every resolved address immediately before connecting and preserves platform TLS hostname/certificate validation. No Mattermost server or credential was assumed and no external message was sent; real-server and human Settings interoperability remain explicitly unverified.

### Matrix

Contract tests verify HTTPS homeserver and private-network policy, encrypted access-token/room-ID use, bearer-header authentication, percent-encoded room targeting, stable idempotent transaction paths, plain `m.text` content, explicit empty mention metadata and legacy mention neutralization, route redaction, 48 KiB serialized UTF-8 bounds, bounded event-ID acknowledgement parsing, and retry/permanent status policy. The production handler validates every resolved address before connecting and preserves platform TLS validation. End-to-end encrypted rooms are deliberately unsupported. No real homeserver or credential was assumed; real-server and human Settings smoke remain unverified.

### ntfy

Contract tests verify hosted/self-hosted HTTPS policy, private-network consent, encrypted topic and `tk_` token handling, bearer authentication, exclusion of credentials and topic from the URL, explicit anonymous-topic consent, priority/tag mapping, route redaction, stable sequence IDs, 4096-byte UTF-8 truncation, bounded message acknowledgement parsing, and retry/permanent status classification. The production handler validates every resolved address before connecting and preserves platform TLS validation. No ntfy server, topic, or credential was contacted; real-server and human Settings smoke remain unverified.

### Gotify

Contract tests verify self-hosted HTTPS policy, reverse-proxy subpaths/custom ports, private-network consent, encrypted application-token projection through `X-Gotify-Key`, token exclusion from URLs, title/message/priority mapping, route redaction, forced plain-text display extras, omission of remote/action extras, bounded message-ID acknowledgement parsing, and retry/permanent status classification. The production handler validates every resolved address before connecting and preserves platform TLS validation. No Gotify server or credential was contacted; real-server and human Settings smoke remain unverified.

### Pushover

Contract tests verify the exact official HTTPS message endpoint, URL-encoded encrypted application/user keys, optional encrypted device restriction, built-in/custom sound projection, route redaction, plain-text-only fields, low/normal/high/critical mapping, explicit emergency retry/expiry fields and mandatory receipt acknowledgement, 1024/250 Unicode-scalar message/title limits, bounded response parsing, provider rejection, permanent 4xx/quota policy, retryable 5xx/network failures, and secret omission from URLs. The production handler permits at most two pooled connections and validates every public DNS result for `api.pushover.net` before connecting. No Pushover account or credential was contacted; real-account, emergency acknowledgement, receipt polling, and human Settings smoke remain unverified.

### Pushbullet

Contract tests verify the exact official HTTPS Pushes endpoint, encrypted `Access-Token` header, token omission from URLs and JSON, all-devices/device/channel/email targeting with exactly one optional target, explicit quota consent, stable per-outbox `guid`, route redaction, plain note-only projection, 32 KiB serialized request bound, bounded active-note acknowledgement, rate-limit/server retry classes, permanent credential/target classes, and network-error sanitization. The production handler permits at most two pooled connections and validates every public DNS result for `api.pushbullet.com` before connecting. No Pushbullet account, quota, token, or destination was contacted; real-account and human Settings smoke remain unverified.

### Twilio SMS

Contract tests verify the exact Account-scoped Messages endpoint; Basic authentication with an encrypted API Key SID/secret or Account SID/Auth Token; encrypted one-recipient and sender allowlists; E.164, AC/SK/MG/SM SID validation; provider-mode transitions; paid-send consent; critical-by-default priority blocking with explicit test-send bypass; 6–36,000-second validity; route redaction; GSM-7 extension-septet and UCS-2 surrogate-safe one-segment bounds; smart encoding; content discard/address obfuscation; omission of media/callback/risk-disable fields; bounded queued/accepted acknowledgement; immediate rejection handling; and best-effort no-replay 408/cancellation/network/5xx/malformed-response policy with only 429 retries. The production handler allows one connection and validates every public DNS result for `api.twilio.com`. Process-termination replay is not eliminated. No Twilio credential, phone number, Messaging Service, or paid send was used; real-account, delivery-status, pricing, compliance, and human Settings smoke remain unverified.

### WhatsApp Cloud API

Contract tests verify the exact versioned `graph.facebook.com/{version}/{phone-number-id}/messages` endpoint, Bearer-token placement, encrypted one-recipient/phone-ID/token projection, E.164 and identifier validation, explicit opt-in/template-approval/paid-send gates, critical-by-default priority blocking with deliberate test-send bypass, approved-template name/language constraints, zero-to-five ordered allowlisted body variables, duplicate/unknown mapping rejection, route-redaction arity preservation, Unicode-scalar bounds, 32 KiB request/response limits, strict single-`wamid` acknowledgement, redirect/client-error classification, and best-effort no-replay 408/cancellation/network/5xx/malformed-response policy with only 429 retries. The production handler allows one connection and validates every public DNS result for `graph.facebook.com`. Process-termination replay is not eliminated. No Meta credential, business account, phone number, recipient, template, or paid send was used; real-account, template interoperability, delivery status, billing, compliance, and human Settings smoke remain unverified.

### Twilio WhatsApp

Contract tests verify the exact Account-scoped Messages endpoint; Basic authentication with an encrypted API Key SID/secret or Account SID/Auth Token; encrypted one-recipient, Messaging Service, and Content Template allowlists; E.164 and AC/SK/MG/HX/SM SID validation; `whatsapp:` recipient projection; explicit opt-in/template/text-only/paid acknowledgements; critical-by-default priority blocking with deliberate test-send bypass; queue validity; zero-to-five ordered numbered Content variables; duplicate/unknown/null mapping rejection; route-redaction arity preservation; Unicode-scalar bounds; omission of free-form body/sender/media/callback fields; content discard/address obfuscation; bounded accepted/queued/read acknowledgement; immediate rejection handling; and best-effort no-replay 408/cancellation/network/5xx/malformed-response policy with only 429 retries. The production handler allows one connection and validates every public DNS result for `api.twilio.com`. Process-termination replay is not eliminated. No Twilio/Meta credential, account, recipient, Messaging Service, Content template, or paid send was used; real-account, delivery status, billing, compliance, and human Settings smoke remain unverified.

### MQTT 5

Contract tests verify fixed encrypted topic and username/password/certificate-thumbprint projection; username/password, mTLS, combined, and separately acknowledged anonymous modes; strict ASCII broker host, port, stable client ID, topic, payload, expiry, and opaque identifier bounds; wildcard/system/empty-level topic rejection; route-redacted JSON preservation; QoS 0 terminal ambiguity; QoS 1/2 duplicate-risk acknowledgement and retry semantics; sanitized publisher outcomes; explicit private-network propagation; and rejection of mixed public/private DNS before any connection. Production-options tests prove the transport uses a validated `IPEndPoint` while retaining the configured broker host as the TLS target, and prove TLS/trust/chain/revocation bypass flags remain disabled. Source and compile review verify non-retained JSON, MQTT 5 content/expiry fields, stable user properties, clean sessions, and provider reason-code classification. No broker, credential, topic, certificate, or external network endpoint was used; real-broker interoperability, mTLS selection, broker ACLs, QoS behavior under connection loss, and human Settings smoke remain unverified.

### Skill

`distribution/agentnotify` was initialized using the official skill initializer. `quick_validate.py` reports:

```text
Skill is valid!
```

The installed `SKILL.md` was also checked for its expected `name: agentnotify` metadata.

### Packaging

Command:

```bash
./scripts/package.sh
```

Result:

- `artifacts/AgentNotifySetup.exe`: approximately 193 MB, one self-contained file.
- Embedded tray payload: approximately 89 MB.
- Embedded CLI payload: approximately 41 MB.
- Installer payload/resource validation passed.

Latest locally packaged artifact SHA-256:

```text
2000b536dc8eac4b72821d0ac6df7b79cb258f4ce7b2f0bfb7456a4df3d7e78b  AgentNotifySetup.exe
```

Regenerate the checksum after any rebuild because it necessarily changes with the binary.

### Published GitHub prerelease

The first hosted prerelease was published from tag `v0.0.1-alpha.1` after the release workflow verified the exact version match, SemVer-style metadata, Windows Release build, 597-test suite, packaging, and skill validity. GitHub Actions run [31566620009](https://github.com/Akash97p/agent-notify/actions/runs/31566620009) completed successfully. The [GitHub prerelease](https://github.com/Akash97p/agent-notify/releases/tag/v0.0.1-alpha.1) contains `AgentNotifySetup.exe`, `SHA256SUMS.txt`, and `SKILL.md`.

The prerelease is intentionally not the mature `v1.0.0` milestone. The release workflow marks any tag containing a hyphen as a prerelease; stable promotion to `v1.0.0` additionally requires a tested `main` release commit, signing, human checks, and release review.

The portable `scripts/package.ps1` implementation and WSL wrapper generated a matching `artifacts/SHA256SUMS.txt`. The static Pages source built locally into `_site`, and the hosted CI, Pages, and release workflows have now executed successfully on GitHub.

### Custom notification type smoke

The packaged tray and CLI accepted `--type deployment-waiting`, normalized and persisted `deployment_waiting`, returned the same identifier through `get`, displayed a fallback toast, and resolved the row. The Settings definition editor and configured accent/label rendering are compiled and covered by configuration tests but still require a human visual pass.

### Sound verification boundary

Automated tests prove WAV/MP3 extension and size validation, safe content-addressed import, duplicate reuse, managed-path resolution, configuration sanitization, and per-type fallback. WPF media playback compiles and is isolated from the API path, but audible playback/preview remains a human check because no user-selected audio file was assumed or modified during automation.

Windows version-resource inspection confirmed for setup, tray, and CLI:

```text
Product: AgentNotify
Company: Kabani Tech Private Limited
File version: 0.0.1.0
```

Authenticode status is `NotSigned`, as documented.

### Published tray/API/CLI smoke

The actual packaged `AgentNotify.Tray.exe` and `agentnotify.exe` were copied to a temporary directory on the Windows `D:` filesystem and executed. Verified:

- tray/API process started;
- authenticated `health` returned version `0.0.1-alpha.1`, API `v1`, and the running PID;
- `input-required` CLI spelling was accepted and serialized as `input_required`;
- create returned an active notification ID;
- a second create with the same key returned the same ID and updated content;
- `list --unresolved --agent smoke-test` returned the row;
- unauthenticated `/v1/health` returned HTTP `401`;
- malformed JSON returned HTTP `400`;
- resolving the ID removed it from the unresolved query; and
- launching the tray binary again left exactly one `AgentNotify.Tray.exe` process.

The smoke notification was resolved, the process was stopped, and the temporary binaries were removed.

Direct Linux `curl` in this WSL configuration could not reach the Windows loopback listener; Windows `curl.exe` and the Windows CLI succeeded. That is a WSL networking environment detail, not an API bind failure.

### Installer window smoke

The packaged installer was launched from the Windows `D:` filesystem. Process inspection confirmed:

```text
Main window title: Install AgentNotify
Main window handle: nonzero
Responding: true
```

The process was then stopped without installing, and the temporary copy was removed.

### Silent install/uninstall smoke

The packaged installer was run with explicit license acceptance into a temporary `D:` directory:

```text
--silent --accept-license --install-dir <temporary>\AgentNotify --no-startup
```

Verified installed outputs:

- `AgentNotify.Tray.exe`
- `agentnotify.exe`
- generated `GettingStarted.html` with no unreplaced skill placeholder
- valid `SKILL.md`
- `LICENSE.txt`
- `THIRD_PARTY_NOTICES.txt`
- `uninstall.ps1`

The installed CLI returned `agentnotify 0.0.1-alpha.1`. The Windows uninstall registry entry reported AgentNotify, version 0.0.1-alpha.1, and publisher Kabani Tech Private Limited. Running the registered uninstall script removed the known files and uninstall registration. The temporary test directory was then removed.

## Verified by implementation and compilation, not visually inspected

- Tray menu includes Notification Center, Getting Started, Copy/Download skill, Pause, Start with Windows, logs, and Exit.
- The supplied multi-resolution `assets/branding/an.ico` is compiled into app/setup resources and executable icon metadata.
- Toast HWND receives `WS_EX_NOACTIVATE`; XAML also sets `ShowActivated=false`, `Focusable=false`, and `Topmost=true`.
- Toast manager queues overflow and uses foreground-monitor work area with DPI conversion.
- Dashboard status changes feed back into the toast manager.
- Active attention rows are queried and restored at startup.
- Normal setup finish launches the tray app and offline guide when the default checkboxes remain enabled.
- Native Settings window opens from the tray and validates port, retention, stack size, placement, lifetimes, pause/DND, and the initial sound toggle.

These were code-reviewed and built, but the automation did not capture the user’s desktop because doing so could expose unrelated private screen content.

## Remaining human checks

- Inspect toast/dashboard/installer visual layout at 100%, 150%, and 200% scaling.
- Verify toast position with taskbars on each edge and mixed-DPI multi-monitor arrangements.
- Type continuously in an editor while toasts arrive and confirm focus/caret never moves.
- Click every tray/menu/toast/dashboard action in a clean installed profile.
- Complete a normal interactive install, confirm the browser guide opens on Finish, sign out/in to test startup, then uninstall through Windows Settings.
- Verify screen-reader naming, keyboard navigation, contrast, and reduced-motion behavior.
- Repeat install/uninstall under a second clean Windows user.

## Release blocker for public distribution

The app is functional but unsigned. Obtain an Authenticode certificate for Kabani Tech Private Limited, sign the two payload binaries before embedding them, sign the final installer last, timestamp all signatures, and publish a fresh SHA-256 checksum.

## Documentation audit — 2026-08-12

This documentation-only branch reconciled the README, roadmap, feature backlog, development handoff, outbound-channel index, and GitHub Pages source with the merged MQTT milestone (`e20e1c4`). It corrected the stale 87-test baseline, recorded the 597-test state and current local installer checksum, documented all 18 implemented outbound adapters, and marked AWS SNS as paused/not implemented. No application source, project file, generated artifact, or distributable skill content was changed by this audit.

Documentation gates for this branch:

- `git diff --check` — required before commit.
- `./scripts/build.sh` — full Windows Release build, required even for documentation branches by repository workflow.
- `./scripts/test.sh` — full automated suite, required before merge.
- `./scripts/build-site.sh` — static documentation site build.
- `quick_validate.py distribution/agentnotify` — skill remains valid; the skill was not modified.

## Settings readability and window chrome — 2026-08-12

The settings surfaces previously relied on WPF's default control templates,
which paint system-black text. Against the dark settings background this made
labels, checkbox captions, tab headers, and list contents effectively
unreadable, and the empty provider/route lists rendered as blank white boxes.

`src/AgentNotify.App/Theme.xaml` now restates every control the settings
surfaces use. It is merged into `SettingsWindow` only, so toast and
notification-center visuals are deliberately untouched.

Automated and mechanical checks performed:

- `./scripts/build.sh` — full Windows Release build, 0 warnings, 0 errors.
- `./scripts/test.sh` — 597 passed, 0 failed.
- Theme load smoke test: a throwaway WPF harness merged `Theme.xaml` through
  the same `pack://application:,,,/Theme.xaml` URI used by `SettingsWindow`,
  then instantiated and laid out one of every themed control type. The
  dictionary resolved with 28 top-level resources and every implicit template
  applied without error. This proves the dictionary resolves and the templates
  are structurally valid; it does **not** evaluate visual appearance.

Not yet performed — still requires a human at a Windows desktop:

- Visual confirmation that every settings tab is legible, including the
  Channels tab and the provider-type dropdown in its opened state.
- Confirmation that the Notification Center header no longer shows a second
  close affordance next to the native title-bar buttons.
- Confirmation that the new empty-state hints appear on the Providers and
  Routes lists before any profile exists, and disappear once one is saved.
- Contrast measurement against WCAG AA for the muted `#9CA3AF` and `#7C8699`
  text on the `#111827` panel background.

## Built-in notification tones — 2026-08-12

Four original tones (`chime.wav`, `ping.wav`, `alert.wav`, `knock.wav`) were
synthesised for this project and released under CC0, so the app ships with
usable sound out of the box and nothing third-party is redistributed. They are
embedded in `AgentNotify.Tray` and seeded into the managed sound directory on
first construction of `NotificationSoundService`; seeding is idempotent and
never overwrites an existing file of the same name. Users can still import
their own WAV/MP3 files, which is unchanged.

Automated and mechanical checks performed:

- `./scripts/build.sh` — 0 warnings, 0 errors.
- `./scripts/test.sh` — 601 passed, 0 failed (597 previous plus 4 new
  `ManagedSoundStore`/`BuiltInTones` tests).
- `./scripts/package.sh` — installer rebuilt; embedded resources changed, so
  packaging was required. SHA-256
  `bd7c8ac93cea2369438ec857566dd6d5e5c3d3825228ac1f03c56dc6056c446e`.
- Embedded-resource audit: the built `AgentNotify.Tray.dll` exposes exactly
  `AgentNotify.Resources.Tones.{chime,ping,alert,knock}.wav`, matching the
  names `NotificationSoundService` looks up. Each was extracted from the
  assembly through the reflection path the app itself uses and confirmed to
  carry a valid `RIFF`/`WAVE` header and to be byte-identical (SHA-256) to the
  source file in `assets/tones/`.
- `RefreshSoundTypes` always forces a selection, so the per-type tone picker
  cannot be driven with a null type.

Not yet performed — still requires a human at a Windows desktop:

- Listening to each of the four tones through Preview to judge loudness
  balance and whether they are pleasant at the default 0.8 volume.
- Confirming the tones are seeded into the sound directory on a genuinely
  clean per-user profile after a real install.
- Confirming the built-in tone dropdowns stay in sync with the file boxes when
  switching between built-in, custom, and cleared selections.

## Cross-platform Phase 1 and 2 (`feature/cross-platform-core`)

### What was actually run

WSL is a real Linux x64 userland, so a `linux-x64` self-contained publish of `agentnotifyd` and
`agentnotify` runs natively here. The Linux broker was therefore exercised for real, not only
compiled.

- `./scripts/build.sh` — 0 warnings, 0 errors, including the two new projects.
- `./scripts/test.sh` — 619 passed, 0 failed (601 previous plus 18 new cross-platform and desktop
  notifier tests).
- Cross-compilation of `AgentNotify.Cli` for `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`
  succeeded, as did `AgentNotify.Host` for `linux-x64`.
- The published Linux `agentnotify` binary is an ELF executable, runs in WSL, reports its version,
  extracts and loads the native `libe_sqlite3.so`, and fails with a clean message and exit code 1
  when no broker is listening.
- `agentnotifyd` was started in WSL against an isolated `HOME`. It reported the key-file protection
  and console notifier, created its data directory, and served the API.
- End-to-end through the Linux CLI against the Linux broker: `health` returned `ok`; `send`
  created an `input_required` notification and printed the DTO; a second `send` with the same
  `--key` updated the existing row in place rather than creating a duplicate; `list` showed one
  row; `resolve` moved it to `resolved`. The console notifier printed the attention line.
- File permissions on real Linux files: the data directory is `drwx------`, and `config.json`,
  `agentnotify.db`, and `secret.key` are `-rw-------`.
- Single instance: a second `agentnotifyd` against the same data directory refused to start with a
  clear message and exit code 1, and started successfully once the first had exited.
- `SIGTERM` handling: the broker printed `Stopping…`, logged `agentnotifyd stopped`, and exited
  within one second with a normal exit status.

### Three defects that only running it exposed

1. **Local state could land in the working directory.** On Unix
   `Environment.GetFolderPath(LocalApplicationData)` returns an empty string when the base
   directory does not exist yet, which is the normal state of a fresh account. The empty string
   combined to the relative path `AgentNotify`, so the first run wrote `config.json` — containing
   the local bearer token — plus `secret.key` and the history database into whatever directory the
   broker was started from. In this repository that was the checkout itself. `DefaultConfigDir`
   now resolves through `SpecialFolderOption.Create`, then `XDG_DATA_HOME`, then `$HOME`, and
   always returns an absolute path. Covered by a regression test.
2. **`SIGTERM` was ignored entirely.** The `PosixSignalRegistration` objects were created and
   discarded. Once finalized the handler is unhooked, so the broker neither shut down nor died and
   could only be stopped with `SIGKILL` — it would have been unmanageable under systemd or
   launchd. The registrations are now held for the lifetime of the process.
3. **Shutdown could hang indefinitely.** `DeliveryDispatcher.StopAsync` is unbounded; the WPF app
   had always bounded it with a two-second `Wait`. The host now bounds the dispatcher stop, bounds
   overall shutdown at fifteen seconds, and exits immediately on a second signal.

### Not verified

- No `notify-send` binary and no graphical session exist here, so **the Linux desktop notifier has
  never displayed a notification**. Only the console fallback was observed.
- No macOS machine, so **neither macOS notifier path nor the Keychain key store has ever run**.
- No `secret-tool`, so **the Linux Secret Service key store has never run**. Only the key-file
  fallback was exercised.
- No ARM64 hardware, so the `linux-arm64` and `osx-arm64` binaries are compiled but never executed.
- The Windows tray application was rebuilt and its tests pass, but **no WPF visual check was
  performed** after the adapter-factory and secret-protector-factory refactor. On Windows the
  factory returns the same `DpapiSecretProtector` the app constructed directly before, and the
  adapter list and disposal set are unchanged, but that is reasoning, not observation.

## Cross-platform Phase 3 (`feature/cross-platform-release`)

- `./scripts/publish-cross.sh linux-x64` produced `agentnotify-linux-x64.tar.gz` and a
  `SHA256SUMS.txt`. The archive was extracted and both binaries inside it ran and reported version
  `0.0.1-alpha.1`, so the released artefact — not just a build output — is known to work on Linux.
- `./scripts/publish-cross.sh linux-x64 osx-arm64` produced both archives.
- Both new shell scripts pass `sh -n`/`bash -n`. `shellcheck` is not installed here, so no lint
  beyond a syntax check was run.
- All four workflow files parse as YAML.

Not verified:

- **No GitHub Actions run has happened.** The Linux and macOS CI jobs and the release upload job
  are written but have never executed; the repository has not been pushed. Until a run is green,
  "builds and tests on macOS" is an expectation, not a result.
- `install.sh` has never been run end to end, because it downloads from a GitHub release that does
  not contain these archives yet. Its platform detection, checksum verification, and failure paths
  are unexercised.
- The `win-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64` archives have not been produced in a
  full run of the script, only the two above.

## Portable file-name sanitizing (`fix/portable-filename-sanitizing`)

The first Linux and macOS CI run failed on `ConfigTests.SoundProfiles_AreSanitizedAndResolvePerTypeOverride`,
identically on both runners. This was a real defect rather than a test artefact.

`Path.GetFileName` is platform-dependent. Windows treats `\`, `/`, and the volume separator as
boundaries; on Linux and macOS a backslash is an ordinary file-name character. A configured sound
of `C:\outside\global.MP3` therefore passed through sanitizing unchanged on Unix, so the invariant
that a configured sound is a bare file name inside the managed sounds directory did not hold there.
Configuration is portable between machines, so the same `config.json` must normalize identically on
every platform. `SafeFileName.Last` now strips both separators and any volume prefix using plain
string operations, and the three call sites in `AgentNotifyConfig` and `ManagedSoundStore` use it.

Verified:

- 628 tests pass on Windows (619 previous plus 9 covering platform-independent normalization).
- On real Linux: a `config.json` hand-written with `"defaultSoundFile": "C:\\outside\\global.MP3"`
  and `"input_required": "..\\attention.wav"` was loaded by the Linux `agentnotifyd`, which rewrote
  the file with `global.MP3` and `attention.wav`. This exercises the fix through the running broker
  rather than through the test host.

Not verified until the next CI run: that the previously failing test now passes on the macOS runner.
No Mac is available here.

## Test-harness timeout (`test/stabilize-concurrency-timeout`)

The Windows CI job failed once on `DedupTests.ConcurrentSameKey_CreatesOneActiveNotification` with
an `HttpClient.Timeout` of five seconds elapsing. The identical commit content passed on the `main`
push and failed on the `dev` push minutes later, so this was timing on a shared runner rather than
a defect: the test issues twelve concurrent same-key posts, keyed creation is deliberately
serialized inside the broker, and a slow two-core runner can hold the last request in that queue
past five seconds.

The fixture's request timeout is now thirty seconds. It exists to stop a hung request from wedging
the suite, not to assert throughput, and thirty seconds still catches a genuine hang. No assertion
was weakened and the concurrency of the test is unchanged, because twelve simultaneous same-key
requests are exactly the race worth exercising.

## First green Linux and macOS CI (`fix/ci-smoke-keyring-assumption`)

The second CI run passed the full test suite on **both** ubuntu-latest and macos-latest, confirming
the file-name sanitizing fix. Remaining results from that run:

- **ubuntu-latest passed the broker smoke test outright**: the headless broker started, `/v1/health`
  returned `ok`, a notification was created through the API, a second post with the same key
  returned the same id, `config.json` and `agentnotify.db` were mode `600`, and the broker stopped
  cleanly on `SIGTERM`. This is independent confirmation, on a machine that is not WSL, of the
  behaviour recorded earlier.
- **macos-latest reported `secrets: macOS login keychain`**. The Keychain key store therefore runs
  correctly on a real Mac, which was previously listed here as unverifiable.
- The macOS smoke step failed on a defect in the workflow, not in the product: it required
  `secret.key` to exist, but that file is only written when no platform keyring is available. macOS
  found its keychain and correctly wrote no key file. The step now checks `secret.key` only when
  present and asserts the broker reported whichever protection it chose.

Still unverified after this run:

- Neither desktop notifier has displayed a notification. The CI smoke test runs the broker with
  `--no-desktop`, and the runners have no graphical session, so the console backend is used by
  design.
- The Linux Secret Service store has still never run; `secret-tool` is absent from the runners, so
  Linux exercised the key-file fallback.
- ARM64 binaries have still never executed.

### Confirmed green run

Run `31607106390` passed both jobs. The broker smoke test completed end to end on each runner:

| Runner | Secret protection chosen | Smoke test |
| --- | --- | --- |
| ubuntu-latest | key file (no `secret-tool` installed) | health, create, keyed dedup, `0600` state, clean `SIGTERM` |
| macos-latest | macOS login keychain, no key file written | health, create, keyed dedup, `0600` state, clean `SIGTERM` |

Both platform branches of the secret-protection selection are therefore exercised by CI, and the
headless broker is confirmed to start, serve `/v1`, deduplicate by key, protect its local state,
and stop on `SIGTERM` on real Linux and real macOS.

## Release 0.0.2-alpha.1 preparation (`chore/release-0.0.2-alpha.1`)

- Version bumped to `0.0.2-alpha.1` in `Directory.Build.props`, with numeric assembly and file
  metadata at `0.0.2.0`. The version is centralized there; no other file hard-codes it.
- `./scripts/build.sh` 0 warnings/0 errors, `./scripts/test.sh` 628 passed, `./scripts/package.sh`
  rebuilt the installer. Installer SHA-256:
  `74877688e5bd6e26c0146b8e68b750ebe695e21ae51b01e57dee8eec7a2d208c`.
- `scripts/release-notes.sh` was run locally for `v0.0.2-alpha.1`. It produced the changelog
  section, prerelease banner, install instructions, checksum guidance, documentation links, and the
  compare range, and it exits non-zero for a version with no changelog section.
- Fixed a defect in the release workflow before it could ship: the Windows job and the portable job
  both uploaded an asset named `SHA256SUMS.txt`, so the second would have replaced the first rather
  than sitting beside it. The portable file is now `SHA256SUMS-portable.txt`.
- The release checkout now uses `fetch-depth: 0`; the default shallow checkout has no earlier tags,
  so the compare link would have been omitted.

Not verified until the tag is pushed: the release workflow has never run with these changes. The
`cross-platform-assets` job in particular has never executed at all, so the portable archives have
never been produced by CI or attached to a release.

## Release v0.0.2-alpha.1 outcome

The tagged run (`31609644846`) published the release with the written description, the Windows
installer, its checksum, and `SKILL.md`. The `cross-platform-assets` job built all five archives
successfully but failed at the upload step with `no matches found for 'checksums'`.

The cause was a shell quoting defect in the workflow, not in the archives: `gh release upload`
takes `file#Display label` arguments, and the label was written unquoted, so the shell split
`SHA256SUMS-portable.txt#SHA-256 checksums for the portable archives` into six words and `gh` looked
for files named `checksums`, `for`, and `the`. Every argument is now quoted and each archive has a
descriptive label. The fix was checked by reproducing the argument construction in bash and
confirming it yields two arguments rather than eight.

Because the tag and its release already existed, the archives for this release were built locally
with `./scripts/publish-cross.sh` and uploaded to the existing release rather than retagging. The
next tagged release will exercise the corrected workflow path, which has still never run to
completion.

### Publishing the v0.0.2-alpha.1 archives

`scripts/publish-cross.sh` hard-depended on `zip` for the Windows archive, which is absent from
many minimal Linux and WSL installs, including this one. The CI runners have it, which is why the
job built all five archives while the same script failed locally. The script now falls back to
Python when `zip` is missing and checks its tools before starting a multi-runtime publish.

Because the local `gh` CLI was authenticated as an account with read-only access to the repository,
GitHub answered the asset upload with a 404 that masked a permissions failure. After the owner
authenticated as `Akash97p`, the archives were uploaded to the existing release rather than
retagging.

Verified against the published release, not the local build: `agentnotify-linux-x64.tar.gz` was
downloaded back from GitHub, its SHA-256 checked against the published `SHA256SUMS-portable.txt`,
and both binaries extracted from it ran and reported `0.0.2-alpha.1`.

Still unverified: the `cross-platform-assets` job has never completed. Its archives were built and
attached locally for this release, so the corrected upload step remains unproven until the next tag.

## Settings window crash on a saved provider (`fix/settings-json-null-crash`)

Reported by the owner while configuring a real Telegram bot: the test send delivered a message to
Telegram successfully but the Settings window then showed an error mentioning null, and clicking the
saved Telegram provider in the list closed the application every time.

Both symptoms had one cause. `JsonElement.TryGetInt32` does not behave like a `Try` method: it
returns false only for a number that will not fit, and throws `InvalidOperationException` for every
other value kind, including JSON `null`. The Settings window read optional integers as
`root.TryGetProperty(name, out var e) && e.TryGetInt32(out var v)`, and optional settings are
serialized as `null` when left blank. A Telegram provider saved without a topic ID stores
`"messageThreadId": null`, so loading it threw.

- Clicking the provider runs `Provider_Selected`, which is not inside `RunAsync`, so the exception
  reached the WPF dispatcher unhandled and terminated the tray process — taking the broker and its
  API down with it.
- Test send runs inside `RunAsync`, which catches everything and shows `exception.Message`. It saves,
  sends, then reloads and reselects the provider, which threw after the message had already been
  delivered. That is why a successful send appeared to fail.

The same unsafe pattern was present at eight call sites: SMTP port, Telegram topic ID, Pushover
emergency retry and expiry, Twilio SMS validity, Twilio WhatsApp validity, and MQTT port, QoS, and
message expiry. All eight now use `JsonConfigReader`, which treats absent, null, and wrong-typed
values alike as "not set". `Provider_Selected` additionally catches anything a stored profile can
throw and reports it in the status line, so no saved row can terminate the process again.

Verified:

- The stored configuration was read directly from the user's SQLite database and confirmed to
  contain `"messageThreadId": null`. Encrypted secret columns were not read.
- A test asserts that `JsonElement.TryGetInt32` really does throw on a JSON null, so the diagnosis
  is demonstrated rather than assumed.
- 644 tests pass, including 16 new ones covering null, missing, wrong-typed, oversized, and
  fractional values, and the exact Telegram document that caused the crash.
- Release build 0 warnings/0 errors; installer repackaged, SHA-256
  `ae12982bff7fe2ca7fadba696772ce51eb4e5bb5fad2a866bb00746fc8b3863a`.

Not verified: **no WPF interaction was performed.** Whether clicking the Telegram provider now loads
its fields without closing the application must be confirmed by the owner on Windows.

## About section (`feature/about-section`)

Adds an About tab to the Settings window and an "About AgentNotify" tray menu entry that opens it.
The tab shows the app icon, name, version read from assembly metadata, a prerelease warning shown
only when the version carries a suffix, a description of the product, a local-first summary, links
to the repository/documentation/releases/issues, the per-user data directory, a warning that
`config.json` holds the local bearer token, and publisher/licence/unsigned notes.

The tray item is placed immediately above Exit rather than literally last, because Exit last is the
established convention and an entry below it reads as a mistake. Moving it is a one-line change.

Hyperlinks hand only `https` URIs to `ShellExecute`. The URIs are compiled into the XAML today, so
this cannot currently matter, but routing an arbitrary scheme through the shell is what turns a link
into a command launcher if the source ever becomes dynamic. Browser launch failures are swallowed so
a misconfigured default browser cannot close the Settings window.

Verified: Release build 0 warnings/0 errors, 644 tests pass, installer repackaged with SHA-256
`4cf2cb98416352f488083d20fc666a2994388ad57ccd9426d03bc9e5ba3778ad`.

**Not verified: no WPF interaction was performed.** Whether the icon renders from the packed
`Resources/an.ico` URI, whether the tab lays out correctly, and whether the tray entry opens the
Settings window on the About tab all need a human on Windows.

## Owner confirmation on Windows (0.0.3-alpha.1)

Two items previously recorded here as unverified were confirmed by the owner on Windows:

- The Settings crash fix: selecting the saved Telegram provider now loads its fields instead of
  closing the application, and a real Telegram bot delivered a test message with no error afterwards.
- The About section: the tray entry opens Settings on the About tab and the tab renders as intended.

A spread of eight notifications across every built-in type was also sent through the installed
build. The four high and critical ones routed to Telegram and were recorded as `Delivered` on the
first attempt; the four normal-priority ones correctly stayed local, exercising the route's
minimum-priority filter.

Documentation was swept for machine-specific paths and version drift: personal filesystem paths were
replaced with `/path/to/agent-notify`, and every current-version reference and test count now matches
the released version. Historical version references in `RELEASING.md` and `CHANGELOG.md` are left as
written, since they record what was published at the time.

## Provider and route editor reset (`fix/provider-editor-reset`)

Reworked the left-hand list columns of **Channels → Providers** and **Channels → Routes** so a saved
selection can always be left: adaptive `Grid` layout that keeps the buttons visible, `+ New provider`
/ `+ New route` above the list, `Delete selected` below it, an editor heading naming the profile
being edited, an explanation on the disabled provider-type dropdown, Escape to deselect, and a
form reset on every path that clears a selection. The cause is analysed in
[BUG.md](BUG.md).

Verified: Release build 0 warnings/0 errors, 644 tests pass. The XAML compiles, which is what proves
the new named elements and handlers resolve.

**Not verified: no WPF interaction was performed.** The clipping analysis is a reading of the layout,
not a measurement of a rendered window. A human on Windows needs to confirm that both buttons are
visible at the window's minimum height, that `+ New provider` after selecting a saved profile clears
the form and re-enables the type dropdown, that Escape does the same, and that saving afterwards
creates a second profile rather than overwriting the first.

## Initial event profile and agent skill installer

Local verification on 2026-08-26:

- `./scripts/build.sh -p:EnableWindowsTargeting=true` completed the full solution cross-build with
  0 warnings and 0 errors under the Linux .NET 10 SDK. This compiles the WPF and setup projects but
  is not a native Windows execution test.
- `./scripts/test.sh --no-restore` passed 659 tests with 0 failures and 0 skips.
- Event-profile coverage included envelope validation, authentication, type defaults, extension projection,
  unknown-extension policy, immutable replay after resolution, explicit condition-key updates, and
  one-time outbound enqueue behavior.
- CLI coverage installs the embedded skill for Codex and Claude Code, exercises both command forms,
  protects changed files unless forced, and verifies dry-run behavior.
- The skill-creator `quick_validate.py` check reported `Skill is valid!` for
  `distribution/agentnotify`.
- `./scripts/build-site.sh`, JSON Schema parsing, and the generated event/schema/skill page checks
  completed successfully.

Hosted verification on topic commit `d265ac0`:

- Windows Actions run
  [`32892500440`](https://github.com/Akash97p/agent-notify/actions/runs/32892500440) passed the native
  solution build, all 659 tests, and `scripts/package.ps1`, including the installer and embedded
  resources.
- Portable Actions run
  [`32892500455`](https://github.com/Akash97p/agent-notify/actions/runs/32892500455) passed on both
  Ubuntu and macOS. Each runner published self-contained single-file CLI and broker executables,
  installed the embedded Codex skill with that CLI, started the published broker executable, and
  exercised API persistence, keyed deduplication, owner-only files, and signal-driven shutdown.

Documentation Actions run
[`32893126444`](https://github.com/Akash97p/agent-notify/actions/runs/32893126444) deployed the site
successfully. Direct HTTPS checks returned `200` for the then-current event documentation, JSON
Schema, and [agent skill guide](https://akash97p.github.io/agent-notify/docs/agent-skills.html).

No WPF visual behavior changed, and no new visual check is claimed.

## ARC 0.1 (`feature/arc-contract`)

Local verification on 2026-08-26:

- `./scripts/build.sh -p:EnableWindowsTargeting=true` completed the full solution cross-build under
  the Linux .NET 10 SDK with 0 warnings and 0 errors. This compiled the WPF and setup projects but
  did not execute either Windows UI.
- `./scripts/test.sh --no-restore` passed 666 tests with 0 failures and 0 skips. The 17 focused ARC
  cases cover authentication, all request-kind defaults, strict field handling, vendor extensions,
  immutable replay after resolution, one-time outbound enqueueing, active-only updates, idempotent
  resolution, missing conditions, and AgentNotify presentation projection.
- `./scripts/build-site.sh` generated the complete documentation set, including ARC, and
  `python3 -m json.tool src/AgentNotify.Protocol/Schemas/arc-0.1.schema.json` parsed the published
  schema successfully.
- `git diff --check` passed, and a case-insensitive repository/site scan found no superseded protocol
  name or field references in the current tree.
- Packaging was not rerun because this branch changes no installer payload, embedded resource,
  publish setting, or release automation. The protocol schema is published documentation and NuGet
  package content; the Windows installer payload remains the tray and CLI executables.

No WPF visual behavior changed, and no visual or real external-provider check is claimed.

## Next.js and shadcn/ui documentation site (`feature/site-shadcn`)

Local verification on 2026-08-26:

- `./scripts/build-site.sh` completed from a clean `npm ci`, passed TypeScript checking, compiled the
  optimized Next.js 16 application, and generated all 21 static routes. The wrapper copied the export
  to `_site`, published the ARC schema and favicon set, and created compatibility aliases for the
  former `.html` documentation URLs.
- A generated-output checker inspected 39 HTML entry points and found no missing internal links,
  scripts, stylesheets, fonts, images, schema files, or documentation targets. The schema published
  at `_site/schemas/arc-0.1.schema.json` is byte-identical to the protocol project source.
- `npm audit --prefix site --audit-level=high` reported zero known vulnerabilities. A semantic scan
  of the current source and generated site found no reference to the superseded protocol name.
- The generated site was served from its real `/agent-notify/` base path and inspected in headless
  Google Chrome. Landing-page renders at 1440x1200 and 390x844 preserve hierarchy and do not overflow;
  ARC renders at 1440x1100 and 390x844 show the desktop navigation/TOC and responsive documentation
  menu respectively. This is an actual browser render check, not an inference from generated HTML.
- `./scripts/build.sh -p:EnableWindowsTargeting=true` completed the full solution cross-build with
  0 warnings and 0 errors. `./scripts/test.sh --no-restore` passed all 666 tests with 0 failures and
  0 skips.
- `git diff --check` passed. Packaging was not rerun because the site branch changes no installer
  payload, embedded application resource, publish setting, or release workflow. The Pages workflow
  itself has not run remotely yet, so hosted deployment of this new implementation is not claimed.

No WPF source or behavior changed, and no WPF visual or external-provider check was performed.

## Monochrome brand assets (`feature/monochrome-brand`)

Local verification on 2026-08-26:

- The owner-supplied white-on-black and black-on-white sources were copied byte-for-byte into
  `assets/branding/` as PNG and AVIF. The canonical 512-pixel PNG was regenerated from the dark source.
- The application ICO contains seven images at 16, 24, 32, 48, 64, 128, and 256 pixels; the web ICO
  contains 16, 32, and 48-pixel images. The Android, Apple touch, and standalone favicon PNGs have the
  exact dimensions declared by their filenames and manifest.
- `./scripts/build-site.sh` passed and generated all 21 routes. The export was served under the real
  `/agent-notify/` base path and inspected in Google Chrome at 1440x900 and 390x844. The new mark is
  sharp, aligned with the header text, readable at navigation size, and consistent with the black/white UI.
- `./scripts/build.sh -p:EnableWindowsTargeting=true` completed with 0 warnings and 0 errors, including
  the WPF tray, CLI, setup project, and their icon resource metadata. `./scripts/test.sh --no-restore`
  passed all 666 tests with 0 failures and 0 skips.
- `./scripts/package.sh` could not run locally because the configured Windows .NET SDK path is absent
  and no other Windows `dotnet.exe` is installed. A Linux SDK can cross-compile but cannot drive the
  repository's PowerShell/Windows packaging boundary.

Hosted verification on topic commit `12fc681`:

- Windows Actions run
  [`32953223592`](https://github.com/Akash97p/agent-notify/actions/runs/32953223592) passed the native
  solution build, all 666 tests, `scripts/package.ps1`, and installer/embedded-resource validation.
- Portable Actions run
  [`32953223540`](https://github.com/Akash97p/agent-notify/actions/runs/32953223540) passed on both
  Ubuntu and macOS, including native CLI/broker publish and smoke tests.

No Windows executable or WPF window was launched locally, so taskbar/tray rendering and the packed
About/setup image remain pending native Windows or hosted executable verification.

## Monochrome Windows UI (`feature/windows-monochrome-ui`)

Local verification on 2026-08-26:

- `Theme.xaml` now defines the neutral desktop tokens and control templates used by Settings and channel
  management. A source sweep found none of the former blue/purple surface, border, focus, or action tokens
  in the App or Setup XAML/C# files. Semantic success, warning, error, and notification-type accents were
  deliberately retained where color conveys state rather than decoration.
- Settings, channel management, Notification Center, toasts, and Setup retain every behavior-bearing
  `x:Name`, selection handler, click handler, and binding referenced by their code-behind. The redesign is
  confined to presentation plus minimize/maximize helpers for the new custom title bars.
- Targeted Release builds of `AgentNotify.App` and `AgentNotify.Setup` passed with 0 warnings and 0 errors,
  proving the XAML resources, control templates, WindowChrome declarations, packed icon URIs, and event
  handlers compile. The subsequent full solution Release build also passed with 0 warnings and 0 errors.
- `./scripts/test.sh --no-restore` passed all 666 tests with 0 failures and 0 skips.
- Local packaging remains unavailable because this WSL environment has no Windows `dotnet.exe`. The topic
  branch therefore used the hosted Windows installer/resource job.

Hosted verification on topic commit `807778c`:

- Windows Actions run
  [`32955922259`](https://github.com/Akash97p/agent-notify/actions/runs/32955922259) passed the native
  solution build, all 666 tests, `scripts/package.ps1`, and installer/embedded-resource packaging.
- Portable Actions run
  [`32955922411`](https://github.com/Akash97p/agent-notify/actions/runs/32955922411) passed on both
  Ubuntu and macOS, including native CLI/broker publish and smoke tests.

**Not verified: no WPF surface was rendered or interacted with.** A Windows human still needs to inspect
Settings at minimum/default/maximized sizes, provider and route editors, Notification Center active/recent
lists, stacked toasts, installer scrolling, 100/125/150/200% DPI, keyboard focus/order, high contrast, and
screen-reader labels. Compilation is not a substitute for those checks.

## Relay browser/device-grant connection (`feature/relay-connect`)

Local verification on 2026-08-31:

- `./scripts/build.sh --no-restore` completed the full Release solution build, including WPF XAML,
  with 0 warnings and 0 errors.
- `./scripts/test.sh --no-restore` passed all 706 tests with 0 failures and 0 skips. The 13 new
  `RelayPairingTests` cases cover sender metadata, authorization-header placement, pending-to-approved,
  denial/expiry/consumption, RFC-style `slow_down`, four tolerated transient failures and failure five,
  cross-origin browser URL rejection, HTTP/localhost policy, exception credential redaction,
  cancellation before a poll, discovery, and installation verification.
- `./scripts/package.sh` rebuilt the self-contained tray, CLI, and setup payload and created
  `artifacts/AgentNotifySetup.exe` with SHA-256
  `b9ff26b2b800ce58b331a27c57482361c75f134dc88b155e78936de47f1f0b9e`. Packaging succeeded but
  emitted the existing IL3000 single-file warning at `SettingsWindow.xaml.cs` for
  `Assembly.Location`; this branch did not introduce that line.
- `python3 /home/akash/.codex/skills/.system/skill-creator/scripts/quick_validate.py distribution/agentnotify`
  reported `Skill is valid!`.
- The packaged Windows CLI was launched through Windows PowerShell from the WSL workspace.
  `agentnotify.exe relay --help` printed the new command reference and exited `0`; a no-network
  validation smoke with `relay pair --url http://relay.example.com` rejected non-local HTTP and
  exited `1`.
- `git diff --check` passed. The default Relay UI source contains no credential value, and fake-handler
  tests prove synthetic poll/installation credentials are absent from URLs and thrown exception text.

**Not verified:** no live Relay server was available. The browser was not opened for a real approval,
no real one-time credential was persisted, no test envelope reached a Relay console/device, and no
post-pairing runtime log directory was available for the required `inst_`/`pol_` value scan. No WPF
window was rendered or exercised. A Windows human must still test Connect → browser approval → verified
identity → Save provider → Send test, plus Cancel, window close, provider switch, browser-launch failure,
expiry, rejection, DPI, keyboard, and screen-reader behavior.

## Relay no-device handling (`fix/relay-no-devices`)

Local verification on 2026-08-31:

- A user verified the sender approval flow against a Relay running on `http://localhost:4000`; the
  connection succeeded and the old test-send path reproduced `relay_400`. Source inspection confirmed
  the adapter had sent `relay-placeholder-device` because sender pairing had not created a phone.
- Focused `RelayChannelTests` and `RelayPairingTests` passed all 44 tests. New regression coverage proves
  an empty or revoked-only device list reports zero active phones, `no_devices_paired` is permanent and
  issues no envelope POST, an unknown pinned device issues no POST, and the paired installation identity
  is retained in parsed delivery configuration.
- `./scripts/build.sh` completed the full Release solution build, including WPF XAML, with 0 warnings and
  0 errors. `./scripts/test.sh --no-restore` passed all 710 tests with 0 failures and 0 skips.
- `./scripts/package.sh` rebuilt the Windows application and installer. The resulting
  `artifacts/AgentNotifySetup.exe` SHA-256 is
  `7d617019d08abc3383bcaff9b4fdbee15ef9454efd26473f3a171be3860a8689`. Publish emitted only the existing
  `SettingsWindow.xaml.cs` IL3000 warning about `Assembly.Location` in a single-file app.
- `git diff --check` passed. The distributable skill was unchanged, so skill validation was not required.

**Not verified:** no phone client is present in this workspace, no receiving device was enrolled, and no
successful envelope/device delivery was claimed. The updated WPF message was compiled but not visually
inspected. After installing this build, the reported no-phone state should be retested against the live
localhost Relay; a successful test delivery still requires an enrolled phone.

## Relay and Android mobile live status (owner report)

Manual report on 2026-09-03:

- The owner reports that the current Relay and Relay mobile builds were tested working, including
  the live Relay/mobile path.
- This supersedes the earlier statement that no phone client existed. The Android receiver now
  exists.

This AgentNotify documentation branch did not repeat the device test, inspect the phone, or capture a
step-by-step pairing/delivery/decryption/acknowledgement log. Treat the result as an owner-performed
integration confirmation, not as an independently reproduced security or compatibility audit.

## Bidirectional communication research (`docs/bidirectional-agent-communication`)

Local verification on 2026-09-03:

- With local Node 22 selected, `./scripts/build-site.sh` type-checked the site, compiled it, and
  generated all 23 static pages, including the new bidirectional research page. The first attempt
  with the shell's Node 18.20.4 stopped before compilation because Next.js 16 requires Node 20.9 or
  newer; no product defect was involved.
- `./scripts/build.sh` completed the full Release solution build with 0 warnings and 0 errors.
- `./scripts/test.sh` passed all 720 tests with 0 failures and 0 skips.
- `git diff --check` passed. Packaging was not rerun because no installer payload, embedded resource,
  publish setting, or release automation changed. The distributable skill was not modified.

No coding-agent adapter, response API, SQLite interaction migration, desktop response UI, Relay
reverse channel, or mobile response control was implemented or integration-tested. The new document
is a researched plan, and every feasibility rating remains a future adapter claim to prove against a
pinned host version.

## First real-Mac hardware verification (`docs/mac-hardware-verification`)

Verified on 2026-09-04 on owner hardware: Intel i5-10400H, macOS 26.6.2, running the
extracted `agentnotify-osx-x64` archive (CLI and broker both report `0.0.4-alpha.1`).
This is the first time the macOS build has run outside CI, and the first time the
`osascript` notifier path has displayed a notification anywhere.

**This was the published `0.0.4-alpha.1` release archive from GitHub, not a build from
`dev`.** Nothing merged after that tag was present — in particular the AgentNotify Relay
channel is newer, so its behaviour on macOS remains entirely unobserved. What follows is
a statement about the released binary and nothing else.

Observed:

- Both downloaded binaries carried `com.apple.quarantine` and both are adhoc-signed
  (`spctl -a` rejects them, as expected for an unsigned prerelease). One
  `xattr -dr com.apple.quarantine <dir>` cleared execution with no `sudo` required; the
  executable bits were already set. No elevation was needed at any point.
- `agentnotify --version` and `--help` work immediately after the quarantine clear.
- The broker starts and reports `secrets: macOS login keychain` and
  `notifications: osascript` (`terminal-notifier` is not installed on this machine).
- A login-keychain entry (service `AgentNotify`, account `provider-secrets`) is created and
  no `secret.key` fallback file is written, matching the CI-runner behaviour on a real
  login session.
- End-to-end broker behaviour confirmed against an isolated `--config-dir` on port 47899 and
  against the default data directory on port 47821: `health` returns `ok`, `send` creates,
  a second `send` with the same `--key` returns the same id (keyed dedup), `get`/`list`/`resolve`
  round-trip, a second broker instance refuses with `AgentNotify is already running for this
  user`, and `SIGTERM` shuts the broker down cleanly (`agentnotifyd stopped` in the log).
- The listening socket is loopback-only (`TCP 127.0.0.1:47899 (LISTEN)`).
- Owner-only state confirmed in both directories: data dir `0700`, `config.json` and
  `agentnotify.db` `0600`, `logs/` `0700`.
- A direct `osascript display notification` banner was shown on screen and confirmed by the
  owner, so the macOS notifier backend is proven end to end for the first time. Sticky
  attention types still degrade to ordinary banners under `osascript`, as documented.
- Default-path token discovery confirmed by the owner: `agentnotify health` and `send` work
  with no `--token` flag against the default `~/Library/Application Support/AgentNotify`
  data directory.

Not verified: the `osx-arm64` binary has never executed (no ARM64 hardware); the
`terminal-notifier` backend path has never run (not installed here); the launchd agent unit
and `install.sh` have never been exercised end to end; the AgentNotify Relay channel
postdates the tested archive and has never run on macOS; and the binaries remain
adhoc-signed, so Gatekeeper quarantine clearing is still required on every fresh download.

## macOS installer, launch agent, and agent skills (`fix/macos-unix-installer`)

Verified on 2026-09-10 on the same owner Intel Mac running macOS 26.6.2:

- The first real `install.sh` run exposed three release-path defects: macOS `/bin/sh` treated a
  Unicode ellipsis after each of two unbraced variables as part of the parameter name under
  `set -u`; the script requested the Windows-only `SHA256SUMS.txt` instead of
  `SHA256SUMS-portable.txt`; and GitHub's `/releases/latest` download route returned 404 because all
  AgentNotify releases are prereleases.
- After correcting those defects, `/bin/sh -n scripts/install.sh` passed. An unpinned
  `./scripts/install.sh` resolved `v0.0.4-alpha.2`, downloaded `agentnotify-osx-x64.tar.gz`, matched
  it against the published portable checksum, and installed both executables to `~/.local/bin`.
  `agentnotify --version` and `agentnotifyd --version` both reported `0.0.4-alpha.2`.
- A valid `~/Library/LaunchAgents/dev.agentnotify.broker.plist` was bootstrapped in the user's GUI
  domain. launchd reported the service running, `lsof` showed only `127.0.0.1:47821`, and
  `agentnotify health` returned `status: ok`, API `v1`, and version `0.0.4-alpha.2`. The broker log
  reported the `osascript` notifier and AES-GCM protection under the macOS login keychain.
- `agentnotify install-skill codex`, `claude`, and `opencode` installed the bundled skill at
  `~/.agents/skills/agentnotify`, `~/.claude/skills/agentnotify`, and
  `~/.config/opencode/skill/agentnotify`. Each installed `SKILL.md` matched the distribution copy;
  Codex also received `agents/openai.yaml`.
- Relay configuration was attempted only through read-only discovery. The supplied hostname
  `an.relay.dev.kabnitech.com` returned DNS `NXDOMAIN` from the system resolver and public resolvers
  `1.1.1.1` and `8.8.8.8`. Pairing was therefore not started, and
  `agentnotify relay status --json` remained `not_configured`.
- `/bin/sh -n scripts/install.sh tests/install-script-test.sh`, the offline mocked-release installer
  check, and `git diff --check` passed locally. `./scripts/build.sh` could not start on this Mac
  because the repository gate intentionally requires a Windows .NET 10 SDK path from WSL; no local
  .NET build was claimed.
- Hosted Windows Actions run
  [`34482095178`](https://github.com/Akash97p/agent-notify/actions/runs/34482095178) passed restore,
  the full Release build, tests, and installer/embedded-resource packaging. Portable run
  [`34482095194`](https://github.com/Akash97p/agent-notify/actions/runs/34482095194) passed the build,
  tests, installer regression check, and native CLI/broker smoke test on both Ubuntu and macOS.

### First live macOS Relay pairing

The owner corrected the Relay hostname to `https://an.relay.dev.kabanitech.com` during the same
2026-09-10 session. Public Cloudflare and Google DNS both resolved it to `212.227.243.171`, and its
`/.well-known/agentnotify-relay` response identified AgentNotify Relay `0.1.0`, API `v1`, envelope
version `1`, and the experimental opaque-transport capability.

`agentnotify relay pair` completed a real browser approval, verified the returned installation
credential, and saved provider **Hosted relay** without printing the credential. A subsequent
`agentnotify relay status --json` reported `status: connected`, identity `Akashs-MacBook-Pro`, and
`enabled: false`. A boolean scan of broker logs found no `inst_` or `pol_` credential prefix.

The pairing command deliberately saved the new provider disabled. The owner then explicitly asked to
enable it and route all priorities to the connected phone. **All notifications to mobile** was
created enabled with minimum priority `Low`, no type/project/agent filters, and
`include_message: true`. The provider was enabled without changing its encrypted credential.

A `success` test at priority `Low` with key `agent-notify-mac-relay-test` exercised the lowest route
threshold. Its durable outbox row changed from `Processing` to `Delivered`; attempt 1 succeeded with
HTTP `201` and no error code. This proves the live macOS broker matched the route, sealed the payload,
and had the hosted Relay accept the envelope. The owner then confirmed that the **Mac Relay test**
notification appeared on the connected mobile app, completing the first verified macOS-to-mobile
Relay path.

Still unverified: Apple Silicon execution, `terminal-notifier`, and signed/notarized installation.
The launchd registration, provider, route, and skill files are local user configuration, not
repository artifacts.

## Release packaging for `v0.0.4-alpha.2`

Built on 2026-09-04 from `chore/release-0.0.4-alpha.2`: Release build 0 warnings / 0 errors,
738 tests passed, `scripts/package.sh` produced `AgentNotifySetup.exe` with SHA-256
`a4c5c68138a11113469b97c74b292d768020d74f10aaa8b84fe1ad93dc522ac4`.

This is the local packaging run. The tag workflow builds, tests and packages independently
on a hosted Windows runner and publishes its own artifacts; the two checksums are not
expected to match, because the installer embeds build-time paths.

## Notify-only harnesses (`feature/harness-opencode-codex-claude`, 2026-09-11)

Automated gates (macOS, user-local .NET SDK 10.0.401, `EnableWindowsTargeting=true`):

- Full-solution Release build: 0 warnings, 0 errors.
- Full suite: 763 tests passed (738 baseline + 25 new harness tests), 0 failed.
- `node --check distribution/harness/opencode/agentnotify.js` passed.
- `python3 -m py_compile distribution/harness/shared/agentnotify_hook.py` passed, and the
  script exits `0` on a sample permission payload with no broker running.
- Both `hooks.example.json` (Codex) and `settings.example.json` (Claude Code) parse as JSON.
- `git diff --check` passed. No WPF surface changed or rendered. Packaging was not rerun:
  two new files are now embedded in the CLI (an installer-payload change), but
  `scripts/package.sh` requires the Windows .NET SDK and PowerShell, which this Mac does not
  have. The next hosted Windows run must confirm installer/resource packaging.

Not verified: no real OpenCode, Codex, or Claude Code session has loaded these harnesses,
and no harness-sent desktop notification has been observed. The owner manual checklist in
`docs/HARNESS.md` is the acceptance test. Record the outcome here when run.

## Relay interaction sync (`feature/relay-interaction-sync`, 2026-09-12)

Automated gates (macOS, user-local .NET SDK 10.0.401, `EnableWindowsTargeting=true`):

- Full-solution Release build: 0 warnings, 0 errors.
- Full suite: 835 tests passed (821 + 14 new publisher/sync/cursor/API/CLI tests), 0 failed.
- `git diff --check` clean. Site typecheck + static export passed with the new
  `interactions` and `relay-interactions` pages.
- No WPF surface changed or rendered. Packaging was not rerun (Windows-only
  script); the next hosted Windows run must confirm installer/resource packaging.

Not verified: no Relay server implements `POST/GET /v1/interaction-responses`
yet, so no live phone-to-host answer has flowed; the mobile UI does not exist.
The contract they will be built against is `docs/RELAY_INTERACTIONS.md`.

## Continuous answer polling (`fix/interaction-answer-delivery`, 2026-09-12)

Automated gates (macOS, user-local .NET SDK 10.0.401, `EnableWindowsTargeting=true`):

- Full-solution Release build: 0 warnings, 0 errors.
- Full suite: 838 tests passed (835 + 3 new sync-validation/transient-failure tests),
  0 failed.
- `git diff --check` clean. Three stale sync fixtures with short digests and missing
  nonces were corrected to the strict wire shape the Relay now enforces; two other
  suites already covered the nonce requirement.

Not verified here: no WPF surface was rendered, no hosted Windows packaging ran, and no
live Relay was available on this machine for a phone-answer round trip. The end-to-end
proof remains: answer from a real phone against a real Relay with the
desktop broker running, and observe the waiting host receive the decision.

## Interaction wait lifecycle and ask fallback (`fix/interaction-wait-lifecycle`, 2026-09-12)

Automated gates (macOS, user-local .NET SDK 10.0.401, `EnableWindowsTargeting=true`):

- Full-solution Release build: 0 warnings, 0 errors.
- Full suite: 842 tests passed (839 + 3 new), 0 failed.
- Each new test was run against the pre-fix code first and confirmed to fail, so the
  three defects are pinned by tests that can actually detect them.
- `python3 -m py_compile` passes on the edited harness hook.

Not verified here: no WPF surface was rendered, no hosted Windows packaging ran, and the
hook's new cancel/resolve path was not executed against a running broker — it is a
subprocess call made only on a fallback that needs a live ask session to reach.

## Release `v0.1.0-alpha.2` (2026-09-12)

Hosted Windows release workflow succeeded on tag `v0.1.0-alpha.2`: build, full suite,
packaging, and publication. Published assets: `AgentNotifySetup.exe` (191.77 MB),
`agentnotify-win-x64.zip`, the four `linux`/`osx` × `x64`/`arm64` portable archives,
`SHA256SUMS.txt`, `SHA256SUMS-portable.txt`, and `SKILL.md`. The desktop repository's
CI, Linux/macOS CI, and documentation-site workflows all passed on `main` for the same
commit.

Not verified: the installer has not been run on Windows from this release, and the
binaries remain unsigned.

## Bidirectional loop, end to end on real hardware (2026-09-13)

The round trip this project exists for was observed for the first time, on the owner's
Intel Mac against the owner's Relay and a real Android handset.

Performed: the installed `ask-permission` hook was invoked with a Claude Code
`PermissionRequest` payload. It opened a broker interaction, published it through the
Relay, and blocked. The phone rendered the question, the owner tapped a choice, and the
desktop poller ingested the answer and settled the interaction. The hook printed the
host-native decision JSON and exited 0.

Observed on the settled interaction: `status: answered`, `source: relay`,
`device_id: 29f6bad9…`, and a client-generated UUID `response_id` — so the answer came
off the Relay from the phone, not from the CLI or the local API. Both `allow` and `deny`
were exercised; `deny` produced
`{"decision":{"behavior":"deny","message":"Denied via AgentNotify."}}` and `allow`
produced `{"decision":{"behavior":"allow"}}`.

The fallback path was verified in the same session and is the half that had never been
executed: a question nobody answered left the hook printing nothing — so the host falls
through to its own prompt — and left the interaction `cancelled` rather than pending.
That is the `fix/interaction-wait-lifecycle` change, confirmed against a live broker
rather than only by unit test.

Environment: broker `0.1.0-alpha.2` on macOS x86_64 under the per-user launchd agent.
The published `osx-x64` binaries are SIGKILLed by macOS as shipped — they are
cross-published from a Linux runner and their adhoc signature is not accepted here.
`codesign --force --sign -` on both binaries fixes it. Worth treating as a packaging
defect rather than a local quirk.

## All three interaction kinds, answered from the phone (2026-09-13)

Following the permission round trip above, one of each kind the broker supports was
raised and answered from the paired handset. Every answer arrived with `source: relay`
and `device_id: 29f6bad9…`, so all three came off the Relay rather than the CLI or the
local API.

| Kind | Prompt | Answer received |
| --- | --- | --- |
| `permission` | delete the build cache at `/tmp/build` | `allow` — "Allow once" |
| `single_choice` | which database for the new service (four options, three with detail lines) | `pg` — "PostgreSQL" |
| `text` | name the next release | `"Next release name should be bla bli blu"` |

The text answer is the one worth noting: it round-tripped free-form content with spaces
and punctuation intact through the sealed envelope and the broker's bounds check, so the
return path carries typed content and not only a choice id. The three were answered out
of order — text first, permission last — and each settled independently, which is what
the per-interaction expiry and first-answer-wins are meant to allow.

What this does **not** cover: multi-select (no such kind exists — `single_choice` means
exactly one, and the broker rejects an answer carrying more) and free-form messaging to
an agent, which is deliberately outside this contract.

One gap this exercise exposed, not a defect in the loop itself: the distributable
`SKILL.md` teaches `send`, `resolve` and `list` only. It never mentions `interactions`,
so no agent currently knows it can ask a question on its own initiative, even though
every layer beneath it works. Tracked in `TODO.md`.

## Web interface (`feature/web-ui`, 2026-09-13)

Environment: Intel MacBook Pro (x86_64), macOS, .NET SDK 10.0.401, headless Google Chrome.

- `dotnet build AgentNotify.slnx -c Release -p:EnableWindowsTargeting=true`: succeeded, WPF app
  and installer included (cross-targeted from macOS; not a Windows run).
- Full suite: 887 tests passed (842 + 45 new), 0 failed. New: 32 provider-form tests (every
  registered adapter has an editor; builder and reader agree for all nineteen kinds; secret
  keep/clear semantics; Twilio and MQTT mode switches drop unused credentials; Relay pairing;
  a real Telegram adapter delivering from a built configuration) and 13 web interface tests
  (launch codes need the bearer token and work once; cookie is `HttpOnly`/`SameSite=Strict`;
  foreign `Host` gets 421; missing header or cross-site `Origin` gets 403 and changes nothing;
  sign-in throttles after ten failures; invalid settings change nothing; stored secrets never
  appear in responses; a browser answer binds to the displayed digest; CSP and asset serving;
  every imported module is served; sound upload sanitizes the name and refuses traversal).
- Two defects were caught by those tests before anything shipped: `GET /ui/` redirected to itself
  (routing treats `/ui` and `/ui/` alike), and expression-bodied `(HttpContext) => await …`
  handlers bound to `RequestDelegate`, silently discarding their result — provider and route
  saves answered 200 with an empty body, so validation errors never reached the page.
- Visual check, by driving headless Chrome over the DevTools protocol against a scratch broker
  with seeded data: every page at 1440 px, light and dark, and 390 px with the navigation drawer.
  No console errors or exceptions on any page. Found and fixed: a stray `null` rendered by native
  `append`, step cards underlined, a label misaligned in two-column grids, harness commands
  truncated, and long working-directory paths widening the layout at phone width.
- Interaction from the page: a text answer and a single-choice answer were entered and sent in
  the browser; `GET /v1/interactions?status=answered` showed both with `source: web`.
- Deployed to this Mac's launchd broker (`osx-x64` publish, ad-hoc re-signed, previous
  `0.1.0-alpha.2` binaries kept in `~/.local/bin/.agentnotify-backup-0.1.0-alpha.2`). `/ui/`
  answered 200, `/ui/api/overview` without a session 401, and `agentnotify ui --print` signed a
  headless browser in; overview, channels (the existing Relay profile, credential shown as
  stored, not revealed) and agents rendered against real data with no console errors. Nothing
  was saved from the page on the real broker.
- `scripts/publish-cross.sh` failed its checksum step on macOS (GNU `find -printf`, `xargs -r`,
  `sha256sum`); fixed to fall back to `shasum -a 256`.

Not verified: the web interface served by the Windows tray app, the tray menu's web-interface entry
(**Open in browser…** at the time; **Web interface…** since), sound preview of built-in tones (seeded only by the Windows app), and a Relay pairing
started from the page against a live Relay. (A person has since used it on macOS; see the next
section.)

## Web interface without sign-in (`fix/web-ui-no-sign-in`, 2026-09-13)

The first cut required a one-time launch link or the pasted token before the page would load. The
owner rejected that: the Windows app has no password, and a sign-in step on a loopback-only page is
friction without a matching threat. Removed the launch codes, sessions, cookie, sign-in page, and
`POST /v1/ui/launch`; `agentnotify ui` now opens the address directly. The host-name allowlist,
the required header and same-origin `Origin` on changes, the CSP, and write-only secrets remain.

- `dotnet build AgentNotify.slnx -c Release -p:EnableWindowsTargeting=true`: succeeded, 0 warnings.
- Full suite: 884 passed (887 − 4 sign-in tests + 1 test that the page needs no token while `/v1`
  still does), 0 failed.
- Deployed to this Mac's launchd broker: `/ui/` and `/ui/api/overview` answered 200 with no token,
  a foreign `Host` got 421, and `/v1/notifications` without the token still got 401.
- **Owner check (2026-09-13):** the repository owner opened `http://127.0.0.1:47821/ui/` on this
  Mac and confirmed it opens straight to the dashboard and works. This is the first time a person
  has used the web interface; the Windows tray app serving it is still unverified.

## Local Usage WebUI (`feature/local-usage-webui`, 2026-09-13)

Automated checks on the owner's Intel Mac with .NET SDK 10.0.401:

- `dotnet build AgentNotify.slnx -c Release -p:EnableWindowsTargeting=true --no-restore`
  succeeded with 0 warnings and 0 errors, including the Windows-targeted projects.
- The full test suite passed: 888 passed, 0 failed, 0 skipped. The new fixture tests cover
  malformed Claude lines, duplicate Claude assistant responses, cumulative and unchanged Codex
  samples, mirrored subagent exclusion, counter normalization, changed-file invalidation, and
  file deletion. A WebUI integration test confirms the aggregate route omits prompt text and log
  paths.
- `node --check` passed for the Usage page and application shell; `git diff --check` passed.
- The required WSL `scripts/build.sh`, `scripts/test.sh`, and `scripts/package.sh` were attempted
  but could not start on macOS because `/mnt/d/dev/dotnet/dotnet.exe` is absent. Native .NET build
  and tests ran instead. Windows installer packaging remains unverified for this branch.

Live local checks:

- A scratch broker on `127.0.0.1:47877` read 16 Claude Code/Codex log files with no skipped
  files and returned nonempty 30-day source, model, and daily aggregates. A headless Chrome
  screenshot at 1440 px showed the Usage page and its tables/charts rendering with real data.
  This was a browser rendering check, not a human visual review.
- A self-contained `osx-x64` broker was published and ad-hoc signed; `codesign --verify` and
  `agentnotifyd --version` passed. The prior installed broker was backed up to
  `~/.local/bin/.agentnotifyd-backup-local-usage-20260913`, and launchd restarted the updated
  broker. On the usual `127.0.0.1:47821` listener, `/ui/api/usage?days=30` returned HTTP 200
  with 16 files and no skips, `/ui/api/overview` returned HTTP 200, and unauthenticated
  `/v1/notifications` still returned HTTP 401.

Not verified: a Windows tray-hosted Usage page, Windows installer payload, a human visual check
of this new page, or exact agreement with provider invoices. The in-memory file cache is rebuilt
on broker restart; advanced fork/replay attribution, OpenCode, and pricing remain work.

## API-equivalent cost and project usage (`feature/usage-api-cost-projects`, 2026-09-13)

- The native macOS .NET SDK 10.0.401 full Release build, including Windows cross-targeted
  projects, succeeded with 0 warnings and 0 errors. The full suite passed: 889 tests, 0 failed,
  0 skipped. Fixture checks cover exact-rate arithmetic, separate Claude 1-hour cache writes,
  unknown-model cost coverage, two same-named but distinct working-directory projects, opaque
  project IDs, and omission of full paths and prompt text from the WebUI response.
- `node --check` passed for the edited Usage module. A self-contained `osx-x64` broker publish
  succeeded. The required WSL `scripts/build.sh`, `scripts/test.sh`, and `scripts/package.sh`
  could not start on this Mac because the Windows SDK path `/mnt/d/dev/dotnet/dotnet.exe` is absent;
  Windows installer packaging is unverified.
- A scratch broker on `127.0.0.1:47878` read 16 local Claude Code/Codex logs, returned 30 project
  groups with no unpriced events in the 30-day view, and did not return working-directory paths.
  A headless Chrome screenshot at 1440 px showed the new cost tile, source costs, dated pricing
  basis, and project rows. This was a browser rendering check, not a human visual review.

Not verified: exact agreement with either provider's bill or subscription allowance, historical
rate changes, long-context/priority/server-tool modifiers, Windows tray hosting, or installer
payload. The price catalog is an explicitly dated counterfactual standard-API estimate.

## OpenCode local Usage adapter (`feature/usage-opencode`, 2026-09-13)

- The native macOS .NET SDK 10.0.401 full Release build, including Windows cross-targeted
  projects, succeeded with 0 warnings and 0 errors. The full suite passed: 891 tests, 0 failed,
  0 skipped. New fixtures cover read-only OpenCode SQLite assistant-message extraction, malformed
  and user row exclusion, date filtering, separate reasoning normalization, provider/model price
  selection, project path and message-text omission, live DB refresh, and the WebUI endpoint.
- `node --check` passed for the edited Usage module and `git diff --check` passed. A self-contained
  `osx-x64` broker publish succeeded with `IncludeNativeLibrariesForSelfExtract=true`. The first
  publish omitted that flag and failed when installed without its sibling SQLite dylib; the
  corrected standalone binary was tested from `~/.local/bin` before installation. The required
  WSL `scripts/build.sh`, `scripts/test.sh`, and
  `scripts/package.sh` were attempted but cannot start on this Mac because their configured
  `/mnt/d/dev/dotnet/dotnet.exe` is absent. Windows installer packaging remains unverified.
- A scratch published broker on `127.0.0.1:47878` read 17 local usage stores with 0 skipped,
  including the owner's live OpenCode database. Its 30-day response included Claude Code, Codex,
  and OpenCode, 34 working-directory project groups, and explicitly unpriced OpenCode records.
  The response omitted full home paths; `/ui/api/overview` returned 200 and the bearer-protected
  `/v1/notifications` returned 401 without a token. Headless Chrome screenshots at 1440 and 500
  CSS pixels showed the Usage view rendering on desktop and narrow layouts. A 390-pixel Chrome
  screenshot was clipped by headless Chrome's 500-CSS-pixel minimum viewport, so it was not used
  as a layout verdict. These are browser rendering checks, not human visual review.
- The corrected broker was ad-hoc signed and installed on the owner's usual launchd service,
  with the previous binary backed up at
  `~/.local/bin/.agentnotifyd-backup-usage-opencode-20260913`. On `127.0.0.1:47821`, the 30-day
  Usage API returned all three sources and 34 project groups; overview returned 200 and the agent
  API returned 401 without a bearer token. The response omitted full home paths.

Not verified: reconciliation with OpenCode's account charges or quota, prices for models without
an exact published rate, historical prices, a physical-phone viewport, Windows tray hosting, or
the Windows installer payload. OpenCode Go estimates use published quota-equivalent token rates,
not additional subscription spend.

## Usage unpriced-banner removal (`fix/usage-unpriced-banner`, 2026-09-13)

The top-of-page unpriced-model warning was removed. Source, project, and model cost labels still
show `+ unpriced`, and the total cost tile still says it covers priced records only. Native macOS
.NET 10 Release solution build succeeded with 0 warnings and 0 errors; all 891 tests passed.
`node --check` and `git diff --check` passed. A self-contained `osx-x64` broker publish with
embedded native libraries succeeded. The required WSL build/test/package scripts were attempted
but could not start because `/mnt/d/dev/dotnet/dotnet.exe` is absent on this Mac. Windows installer
packaging and a human browser visual check for this edit remain unverified.

## Live quota WebUI (`feature/live-quota-webui`, 2026-09-13)

- Native macOS .NET SDK 10.0.401 Release solution build, including Windows cross-targeted
  projects, succeeded with 0 warnings and 0 errors. The full suite passed: 896 tests, 0 failed,
  0 skipped. New fixtures cover Codex multi-bucket windows and credits, Claude missing/scoped
  windows, five-minute cache and 30-second manual-refresh gate, stale-on-failure behavior,
  credential-scope invalidation, fixed-endpoint Claude request and token omission, and the WebUI
  quota route/refresh header guard.
- `node --check` passed for the app shell and Quota view; `git diff --check` passed. The required
  WSL `scripts/build.sh`, `scripts/test.sh`, and `scripts/package.sh` were attempted but could not
  start on this Mac because `/mnt/d/dev/dotnet/dotnet.exe` is absent. Windows installer packaging
  and Windows tray-hosted quota probing remain unverified.
- A self-contained `osx-x64` broker with native SQLite embedded was published. An isolated
  scratch broker on `127.0.0.1:47878` returned current Codex app-server five-hour/seven-day
  windows and Claude Code account five-hour/seven-day windows, plus an explicit unavailable
  OpenCode state. No access token or bearer header appeared in the JSON response. Its embedded
  Usage JavaScript no longer contained the unpriced-model banner. Headless Chrome screenshots
  at 1440 and 500 CSS pixels showed the Quota page and three provider cards rendering. These
  are browser rendering checks, not a human visual review.
- The initial installed binary exposed a launchd-only Codex failure: the npm Codex launcher uses
  `/usr/bin/env node`, while launchd supplied a minimal `PATH`. Reproducing that minimal `PATH`
  against a scratch broker returned Codex unavailable; after adding the launcher's bin directory
  to the child process environment, the same restricted-path scratch check returned both Codex
  and Claude windows. The corrected signed standalone binary was installed with the prior broker
  backed up at `~/.local/bin/.agentnotifyd-backup-live-quota-20260913`. On the normal
  `127.0.0.1:47821` listener, `/ui/api/quota` returned Codex and Claude `ok` with two windows
  each and OpenCode `unavailable`; the Usage script lacked the removed banner, overview returned
  200, and unauthenticated `/v1/notifications` returned 401.

Not verified: exact agreement with Codex or Claude account dashboards after subsequent activity,
stability of Anthropic's undocumented first-party OAuth usage endpoint, Windows CLI discovery,
Windows installer payload, or a human visual check from the owner's Windows browser.

## Multiple quota accounts and OpenCode Go estimate (`feature/multi-account-quota`, 2026-09-14)

- Native macOS .NET SDK 10.0.401 Release solution build with
  `-p:EnableWindowsTargeting=true` succeeded with 0 warnings and 0 errors. The full suite passed:
  902 tests, 0 failed, 0 skipped. New checks cover named-account cache isolation and removal,
  profile validation and startup normalization, persisted WebUI add/remove operations, per-model
  OpenCode Go cap arithmetic, and unknown estimates when a published rate is incomplete.
- `node --check` passed for the Quota view and `git diff --check` passed. The documentation site's
  TypeScript check and 27-page static export passed locally. Next.js 16.3.3 intermittently lost
  request context during cold static export (upstream issue 98200), so the site is pinned to
  Next.js 16.2.12 with its supported TypeScript CLI mode and one static worker. Cold builds also
  failed on this Mac's Node 22; a cold Node 24.21.0 export passed, and the Pages workflow runs Node
  24. The local build script uses that Node version when its host Node is older.
- A self-contained `osx-x64` broker with embedded native libraries was published, ad-hoc signed,
  and installed into the owner's launchd service. On `127.0.0.1:47821`, the quota response used
  contract version 2, returned two live windows each for the current Codex and Claude Code
  profiles, and returned local five-hour, seven-day, and rolling 30-day estimates for the owner's
  two observed OpenCode Go models. The response contained no access token, bearer header, or
  credential-file content. A 1440-pixel headless Chrome screenshot showed the account editor,
  named profile cards, and Go estimate. This was a browser rendering check, not a human visual
  review.

The required WSL `scripts/build.sh`, `scripts/test.sh`, and `scripts/package.sh` cannot start on
this Mac because `/mnt/d/dev/dotnet/dotnet.exe` is absent; Windows installer packaging is therefore
unverified. Also unverified are actual second-profile probes using the owner's credentials,
Windows tray hosting, exact provider-dashboard agreement, and Claude profiles whose macOS login is
stored only in Keychain rather than the selected profile's `.credentials.json`. OpenCode Go values
are local token-based estimates against published per-model caps, not provider-reported remaining
quota; the 30-day view is rolling rather than the provider's billing cycle.

## WebUI over an SSH forward with a different local port (`fix/webui-forwarded-port`, 2026-09-13)

- Reproduced the owner's `421` with `Host: 127.0.0.1:47822` against the Mac broker listening on
  `127.0.0.1:47821`. The original guard required the `Host` port to equal the listener port, which
  cannot hold when an SSH local forward uses a different browser-side port. A separate SSH command
  targeting Mac port 47822 would fail to connect because the broker does not listen there.
- The guard now accepts only loopback host names with an explicit port and compares state-changing
  requests' `Origin` to that exact `Host`, including its forwarded port. Foreign host names still
  return 421. A WebUI integration test covers page and API reads on the forwarded host, a successful
  same-origin write, and refusal of a write from the broker-port origin.
- Native macOS .NET 10 Release solution build passed with 0 warnings/errors; the full suite passed
  897 tests, 0 failed, 0 skipped. The repository's WSL `scripts/build.sh` and `scripts/test.sh` were
  attempted but cannot run on this Mac without `/mnt/d/dev/dotnet/dotnet.exe`. Windows installer
  packaging was not needed for this API guard change and was not run.
- A self-contained `osx-x64` broker publish succeeded, was ad-hoc signed and verified, and replaced
  the installed launchd broker. Its predecessor is backed up at
  `~/.local/bin/.agentnotifyd-backup-forwarded-port-20260913`. After restart, requests to the live
  `127.0.0.1:47821` listener carrying `Host: 127.0.0.1:47822` returned 200 for `/ui/`,
  `/ui/api/quota`, and a same-origin quota-refresh POST. A wrong-port `Origin` returned 403 and a
  foreign `Host` returned 421. The quota response reported Codex and Claude Code `ok` and OpenCode
  `unavailable`. These checks simulate SSH's forwarded `Host` and `Origin` headers; the owner's
  Windows browser and end-to-end SSH tunnel remain for owner verification.

## Usage recent-session view (`feature/usage-session-view`, 2026-09-14)

- Native macOS .NET SDK 10.0.401 Release solution build with
  `-p:EnableWindowsTargeting=true` succeeded with 0 warnings and 0 errors. After that build, the
  full suite passed without rebuilding: 903 tests, 0 failed, 0 skipped. The new fixture verifies
  session grouping after deduplication, chronological aggregation, opaque IDs, and omission of raw
  provider session IDs and full project paths. `node --check` and `git diff --check` passed.
- The documentation site's TypeScript check and 27-page static export passed. The required WSL
  build, test, and package scripts were attempted but cannot start on this Mac because
  `/mnt/d/dev/dotnet/dotnet.exe` is absent. GitHub Actions then completed the Windows Release build,
  all 903 tests, and installer/embedded-resource packaging successfully on commit `e74107d`. The
  previously failing Windows fixture had placed an unescaped `C:\\...` path in hand-built JSON;
  serializing that fixture properly fixed the test without changing production parsing. One local
  test run overlapped the Release build and collided on a generated runtime-config file; the clean
  sequential full-suite run above is the reported local result.
- A self-contained, ad-hoc-signed `osx-x64` broker was installed into the owner's launchd service.
  The live 30-day Usage response on `127.0.0.1:47821` reported contract version 2, 6,505 usage
  records, 58 attributable sessions, and the newest 50 session summaries, with no home-directory
  path in the response. The SSH-forwarded host header still returned the UI and quota API, and the
  quota report still returned both current provider accounts plus the OpenCode Go estimate. A
  1440-pixel headless Chrome rendering showed the updated Usage view; this was a browser rendering
  check, not a human visual review.

Not verified: Windows tray hosting, a human visual review, sessions missing from source records, or
fork/replay parentage. Session cost remains the same current-rate estimate used elsewhere in Usage.

## Compact Insights dashboard (`feature/insights-dashboard`, 2026-09-14)

- Native macOS .NET SDK 10.0.401 built the Release solution with
  `-p:EnableWindowsTargeting=true`: 0 warnings and 0 errors. The focused WebUI/quota/config run
  passed 36 tests, and the sequential full suite passed 903 tests with 0 failed and 0 skipped.
  The account-management fixture renames both a built-in profile and an additional profile,
  reloads their persisted labels, and rejects an empty label. Every embedded JavaScript module
  passed `node --check`, and `git diff --check` passed.
- `scripts/build-site.sh` completed its TypeScript check and generated all 27 static pages. The
  required WSL build/test/package entry points cannot run on this Mac because their configured
  Windows SDK path, `/mnt/d/dev/dotnet/dotnet.exe`, is absent; Windows packaging remains a CI gate.
- A self-contained, ad-hoc-signed `osx-x64` broker containing the new embedded assets was installed
  into the owner's launchd service, with the previous executable saved as
  `~/.local/bin/.agentnotifyd-backup-insights-dashboard-20260914`. `agentnotify health` returned
  `ok` on `127.0.0.1:47821`. The live Dashboard composed four healthy Codex/Claude profiles,
  OpenCode Go estimates, 30-day local usage, project rankings, and delivery health. The Live quota
  view showed remaining-balance bars whose fill matched the reported remaining percentage. A final
  owner-directed color pass assigns the neutral bar at 40–100%, yellow at 20–39%, and red at 0–19%.
- Headless Chrome rendered the Dashboard, Live quota, and simplified Usage pages with real data at
  1440 CSS pixels. A narrow render confirmed the single-column dashboard breakpoint. The account
  management panel was present with all four local profiles and remains collapsed by default.
  These were browser rendering checks, not an owner visual sign-off. Reduced motion is covered by
  the existing CSS media rule; assistive-technology behavior was not manually tested.
- GitHub Actions on commit `301ace7` passed the Windows Release build, all 903 tests, Windows
  packaging, and the Linux/macOS matrix. The preceding color-only merge had one unexplained Windows
  test-step failure after the same dashboard revision had passed; its anonymous run exposed no TRX
  detail. CI now writes the TRX to an explicit runner-temporary directory and emits a clear
  annotation even when `dotnet test` exits before producing that file. The clean rerun required no
  product-code change.

Not verified: Windows tray-hosted rendering, installer payload execution, manually renaming the
owner's real profiles, or a human visual review of the new layouts.

## Expanded OpenCode Go catalog (`feature/opencode-go-pricing`, 2026-09-14)

- Checked the official [OpenCode Go table](https://opencode.ai/docs/go/) on 2026-09-14 for
  exact model IDs, per-million-token rates, published cache-write prices where present, and
  monthly dollar caps. The catalog now contains 20 exact fixed-rate IDs. Context-tiered and
  peak/off-peak entries remain unpriced because the local ledger cannot select their rate.
- The focused usage tests passed 7/7. A native macOS .NET 10 Release solution build passed with
  0 warnings and 0 errors. After updating the catalog date assertion, the full suite passed
  903 tests, 0 failed, 0 skipped. The fixture covers a newly priced model, a published cache-write
  rate in both Usage and Go quota estimates, and an excluded tiered model. `git diff --check`
  passed. The WebUI module passed `node --check`.
- The documentation site's TypeScript check and all 27 static pages passed. The repository's
  WSL build/test/package scripts were attempted but cannot run on this Mac without
  `/mnt/d/dev/dotnet/dotnet.exe`. GitHub Actions for `f52a5b3` passed the Windows Release build,
  all 903 tests, installer/embedded-resource packaging, and the Linux/macOS matrix.
- A first manual `osx-x64` single-file publish omitted
  `IncludeNativeLibrariesForSelfExtract=true`. The executable worked alongside its SQLite dylib
  in the publish directory but failed under launchd when copied alone; the prior broker was
  immediately restored and confirmed healthy. The corrected publish used the repository release
  flags for native-library embedding and compression, was ad-hoc signed, and started from a
  directory containing only that executable. After installation and launchd restart,
  `agentnotify health` returned `ok` on `127.0.0.1:47821`. The previous executable is backed up
  at `~/.local/bin/.agentnotifyd-backup-opencode-go-pricing-20260914`.
- The live 30-day Usage API reported `pricing_as_of: 2026-09-14`; Live quota reported three
  windows each for the two locally observed Go models. The embedded quota module served the new
  20-model coverage text, and the page returned HTTP 200 with a forwarded browser-side `Host`
  port of 47822. A 1440-pixel headless Chrome screenshot rendered the compact quota page and
  its 20-model coverage line. This was a browser rendering check, not a human visual review.

Not yet verified: Windows tray-hosted Go estimates or provider-reported Go quota. Local Go usage
on this Mac currently includes only Muse Spark 1.3 Contributor and GLM-5.3, so newly priced models
need future local usage before they appear as model cards.

## GitHub Pages and documentation status refresh (`docs/webui-landing-page`, 2026-09-14)

- Updated the landing page and published guide descriptions for the shipped WebUI, three local
  usage sources, published-rate cost estimates, named Codex/Claude Code quota profiles, the
  separately labeled OpenCode Go local estimate, and the Insights dashboard. Reconciled the
  bidirectional, interaction, harness, Relay, architecture, roadmap, backlog, cross-platform,
  installation, troubleshooting, and release docs with current implementation status.
- `./scripts/build-site.sh` passed TypeScript checking and exported all 27 pages. A read-only check
  confirmed that internal links from the exported landing and guide pages resolve and that the
  landing, WebUI, and bidirectional guide contain the updated claims. `git diff --check` passed.
- A native macOS .NET 10 Release solution build passed with 0 warnings and 0 errors. The full
  `AgentNotify.Tests` suite passed 903 tests, 0 failed, 0 skipped. The repository's WSL
  `./scripts/build.sh` and `./scripts/test.sh` could not run on this Mac because their configured
  Windows SDK path, `/mnt/d/dev/dotnet/dotnet.exe`, is absent. Installer packaging was not run;
  this branch changes documentation and the separate GitHub Pages site, not installer payloads.
- Headless Chrome rendered the exported landing page at desktop width and at an emulated 390-pixel
  mobile viewport. The mobile document's `scrollWidth` equaled its 390-pixel viewport width after
  constraining the grid columns. These are browser rendering checks, not a human visual review.

Not verified locally: Windows tray-hosted behavior, provider interoperability, or a human review
of the updated Pages layout. Those product behaviors are unchanged by this documentation branch.

## Hosted-only Relay, tray label, and ARC 0.2 (2026-09-16)

Run on the owner's Mac with the local .NET 10 SDK at `~/.dotnet` rather than `scripts/*.sh`,
which resolve a Windows SDK path that does not exist on this machine. The whole solution,
including the WPF app and the installer, compiles there with `-p:EnableWindowsTargeting=true`.

Performed:

- `dotnet build AgentNotify.slnx -c Release -p:EnableWindowsTargeting=true` — **succeeded, 0 warnings,
  0 errors**, covering `AgentNotify.App` and `AgentNotify.Setup`. This is a compile of the WPF
  project including XAML markup compilation, so the removed `RelayDeploymentBox` and
  `RelayBaseUrlBox` controls leave no dangling `x:Name` reference in the code-behind.
- `dotnet test tests/AgentNotify.Tests/AgentNotify.Tests.csproj -c Release` — **915 passed, 0 failed**,
  up from 906 before this work.
- `npm run build` in `site/` — the GitHub Pages export generated all 27 routes, and the built output
  contains no link to any Relay repository.
- The ARC 0.2 JSON Schema was checked with `jsonschema` 4.26 (Draft 2020-12): the schema itself is
  valid, all four `docs/ARC.md` examples validate against it, 14 malformed envelopes are rejected
  (0.1 version, a response on a created event, an outcome on a created event, a text kind carrying
  choices, a choice kind without them, a single choice, an answer with both or neither answer field,
  an answer without a key, an answer with no response object, a short digest, a resolution carrying a
  message, an unknown outcome, an unknown core field), and three further valid shapes are accepted.

Not verified:

- **No WPF surface was rendered or exercised.** The Relay panel with its deployment selector and
  server-address field removed, and the renamed **Web interface…** tray entry, compile but have not
  been looked at by a person. Layout, spacing, and the read-only hosted-endpoint line are unconfirmed.
  A cross-compile is not a running Windows app; run it before trusting the appearance.
- No live Relay was contacted. Pairing against the hosted endpoint, and a real phone answering an
  ARC 0.2 answerable request end to end, remain unobserved.
- Packaging was not run.

## Release `v0.1.0-alpha.3` (2026-09-16)

Tag `v0.1.0-alpha.3` on `main` at `a8c27e2`. The hosted Windows release workflow
([`35105119307`](https://github.com/Akash97p/agent-notify/actions/runs/35105119307)) succeeded in
under six minutes: tag/version match, SemVer-style metadata check, Release build, the full suite,
`package.ps1`, and prerelease publication. The Linux job then attached the portable archives. Every
other workflow on the same commits also passed — CI and CI (Linux and macOS) on both `dev` and
`main`, and the documentation site deploy.

Published assets and checksums:

```text
5ddd439eec47b39e8167d7693b5c5214e5f81b0c37f680bd428604fdd80253ce  AgentNotifySetup.exe
c62552fd67aa8c3920fe3799fd2f5f64c6d2106bd0b6e89efef4511253ceaf51  agentnotify-linux-arm64.tar.gz
e9bceab3e2de27373229a946b532937c2c68a39e76beee8a6b89c9b16f913bf5  agentnotify-linux-x64.tar.gz
1b58e63904dee6ca5aac85624a751ece50e567604d286ee14c547a388112ac60  agentnotify-osx-arm64.tar.gz
bf334dd04609c460bfee053508976f8afac01bef9f1de91841f866f9707260dc  agentnotify-osx-x64.tar.gz
890a137070b7f1d0347a666b049df838c94e36f7b4664a95f49855490d6168bb  agentnotify-win-x64.zip
```

Not verified: **nobody has installed or run this build.** The installer was produced and checksummed
by CI, not executed. Everything listed as unverified in the section above still stands — no WPF
surface has been looked at, no live Relay has been contacted with the hosted-only client, and no
phone has answered an ARC 0.2 request end to end. The binaries remain unsigned.

## WSL agent discovery (`feature/wsl-agent-discovery`, 2026-09-16)

Environment: macOS (Darwin 25.6), .NET SDK 10.0.401 at `~/.dotnet`. There is no Windows machine or
WSL in this session, so the repository scripts were not used and `WslDiscovery` itself returns
nothing here.

Ran:

- `dotnet test tests/AgentNotify.Tests/AgentNotify.Tests.csproj -c Release` — **933 passed, 0 failed,
  0 skipped**, up from 915 on `dev` on the same machine. New coverage: share-path parsing, UTF-8 and
  UTF-16 `wsl --list` output, `/etc/passwd` home lookup, usage from a fake WSL home including a Linux
  project path and a distribution that stops, discovered/hand-added/renamed WSL quota accounts,
  label normalization, the skills-root home override, the Codex-in-WSL start arguments and
  `WSLENV`, and web interface skill installs by distribution name (including a stopped one).
- Release builds of `AgentNotify.Tests`, `AgentNotify.Host`, `AgentNotify.Cli`, and
  `AgentNotify.App` (`-p:EnableWindowsTargeting=true`) — **0 warnings, 0 errors**.
- `node --check` on the four edited web interface modules and `bash -n scripts/agentnotify`.

Not verified — none of this has run on Windows or WSL:

- Registry enumeration, `wsl.exe --list --running` on a real machine, and reading `/etc/passwd` and
  agent logs through `\\wsl.localhost`.
- That a stopped distribution is left stopped when the pages load.
- The Codex app server started through `wsl.exe` and a login, interactive shell (including nvm), and
  Claude quota read through the share.
- SQLite reading OpenCode's database over the WSL share.
- The Agents page, Live quota account list, Usage "Includes WSL" line, and the WPF Install tab's WSL
  rows were not rendered.
- `install-skill --wsl` and the wrapper's `WSL_DISTRO_NAME` forwarding.

## In-place installer update (`feature/installer-in-place-update`, 2026-09-16)

Environment: macOS (Darwin 25.6), .NET SDK 10.0.401 at `~/.dotnet`. There is no Windows machine or
WSL in this session, so the repository scripts were not used.

Ran:

- `dotnet build src/AgentNotify.Setup/AgentNotify.Setup.csproj -c Release -p:EnableWindowsTargeting=true`
  — **0 warnings, 0 errors**, including XAML markup compilation of the renamed/new `x:Name` elements.
- `dotnet build src/AgentNotify.App/AgentNotify.App.csproj -c Release -p:EnableWindowsTargeting=true`
  — **0 warnings, 0 errors**.

Not verified — none of this has run on Windows:

- Update detection from the uninstall registration, the update-mode window layout (collapsed licence
  panel, read-only folder, version line), and the finish/relaunch actions.
- That the tray receives `Local\AgentNotify.Exit.v1` and exits cleanly, and the kill fallback
  against an alpha.3 tray that has no such event.
- Renaming an in-use `agentnotify.exe` during an update, and deleting the `.old` leftover on the next
  install and in `uninstall.ps1`.
- Silent update, relaunch, and `--no-launch`.
- Packaging was not run, so no installer containing this change exists yet.

## WSL discovery on a real Windows machine (`fix/wsl-default-user`, 2026-09-17)

Environment: Windows 11 host with WSL 2.7.11 running `Ubuntu-20.04`, WSL workspace, Windows .NET SDK
at `/mnt/d/dev/dotnet/dotnet.exe`. The distribution sets its user through `/etc/wsl.conf`
(`[user] default=akash`); its `Lxss` registry entry has `DefaultUid` `0`.

At `a19304e` (`dev`) the repository scripts ran: `scripts/build.sh` **0 warnings, 0 errors**,
`scripts/test.sh` **933 passed, 0 failed, 0 skipped**, and `scripts/package.sh` produced
`artifacts/AgentNotifySetup.exe`. The owner installed that build and reported:

- Adding `\\wsl.localhost\Ubuntu-20.04\home\akash\.codex` and `...\.claude` by hand on Live quota
  worked; both cards showed **Live** with real 5-hour and 7-day balances, so Codex's app server ran
  inside the distribution and the Claude credentials were read through the share.
- No WSL account was discovered automatically, and the dashboard showed no usage (0 tokens,
  0 sessions) although agents run in that distribution.

Cause, confirmed on this machine: `wsl.exe --list --running --quiet` does list `Ubuntu-20.04` (as
UTF-16LE without a byte-order mark, which the parser handles), but discovery took the home of
`DefaultUid` `0`, `/root`. The installed `agentnotify.exe install-skill claude --wsl Ubuntu-20.04
--dry-run` reported `\\wsl.localhost\Ubuntu-20.04\root\.claude\skills\agentnotify`.

After the fix (default user read from `/etc/wsl.conf`, falling back to `DefaultUid`) the same
dry run from the fixed CLI build reported
`\\wsl.localhost\Ubuntu-20.04\home\akash\.claude\skills\agentnotify`. `WslTests` pass, including
new `wsl.conf` parsing and passwd lookup by name.

Not verified yet: discovered quota cards and WSL usage in an installed build with this fix, and a
distribution without `wsl.conf`. Linux and macOS are unaffected (discovery is Windows-only).

## Editable and removable Live quota accounts (`feature/editable-quota-accounts`, 2026-09-17)

Environment: the same Windows 11 host and WSL workspace, repository scripts.

Ran:

- `scripts/build.sh` — **0 warnings, 0 errors**. `scripts/test.sh` — **945 passed, 0 failed,
  0 skipped**. New coverage: removing, restoring, renaming, and moving built-in and discovered WSL
  accounts through the web API (including refusing a directory another account already monitors and
  a relative path), removed IDs filtered from the live quota report, config normalization of
  `removedQuotaAccounts`, and Usage reading a hand-added profile's ledger while counting a
  directory that is both added and discovered once.
- `node --check` on `quota.js`.

Not verified: the Manage accounts layout (desktop and narrow widths) has not been looked at in a
browser, and the installed build has not yet shown discovered WSL cards or WSL usage. Packaging
produced a new `artifacts/AgentNotifySetup.exe` for the owner to test.

## OpenCode usage over the WSL share (`fix/opencode-usage-over-wsl`, 2026-09-17)

Environment: the same Windows 11 host; `Ubuntu-20.04` holds ~1 GB of Codex JSONL (331 files, the
largest 256 MB), 72 MB of Claude Code JSONL, and a 261 MB OpenCode database with a 10 MB WAL.

The owner reported that every Insights tab loaded for minutes after the WSL discovery fix. Measured
against the installed build with `curl.exe`: the first `/ui/api/usage?days=30` took **123 s**, and
every later `/usage` and `/quota` call still took **~30 s**, while `/overview` and `/quota/accounts`
answered in milliseconds. Reading the whole database sequentially through `\\wsl.localhost` took
1.8 s and listing the JSONL files 0.6 s, so the time was SQLite's page-level reads over the share,
repeated on every request (JSONL results were already cached). That installed build also reported
`files_skipped: 1` and no OpenCode source at all: the query was failing.

The fixed `agentnotifyd` was run from the build output on port 47899 with a throwaway
`--config-dir`, next to the installed tray:

- `/usage?days=30`: **62 s** cold (JSONL parsing), then **0.45 s** and **0.40 s**. `/quota`: 2.2 s
  (live account probes), then 0.008 s.
- Claude Code and Codex totals were identical to the installed broker's. OpenCode now appeared with
  **1,866 events and 228,606,112 tokens**, exactly what a native Python read of the same database
  in WSL counted for the last 30 days. `files_skipped` was 0, and no snapshot directory remained in
  `%TEMP%`.
- `scripts/build.sh` **0 warnings, 0 errors**. One `scripts/test.sh` run failed a single test whose
  name was not captured; four reruns passed. Two same-sized writes within one clock tick would leave
  size and modification time unchanged, so the cache stamp now also includes the SQLite header's
  change counter and the WAL header. After that, `scripts/test.sh` passed **945 of 945 in five
  consecutive runs**, and the broker was measured again: 60 s cold, then 0.48 s and 0.43 s, 7,571
  events, nothing skipped.

Not verified: a cold load is still about a minute after each broker start, because parsed JSONL is
held only in memory; the installed tray with this build; and a snapshot taken during an OpenCode
checkpoint.

## Model pricing, Muse Code / Kilo CLI / Gemini CLI usage (`feature/model-pricing`, `feature/more-agent-usage`, 2026-09-17)

Environment: the same Windows 11 host and `Ubuntu-20.04`. Rates were read on 2026-09-17 from the
official pages linked in `docs/WEB_UI.md` (OpenAI pricing and model pages, Meta Model API, Gemini API
pricing and Gemini 3 guide, Z.ai, Xiaomi MiMo pay-as-you-go, OpenCode Zen and Go). Gemini 3 Pro
Preview and Poolside Laguna have no official rate page and stay unpriced.

Ran:

- `scripts/build.sh` **0 warnings, 0 errors**; `scripts/test.sh` **978 passed, 0 failed, 0 skipped**
  (new: per-provider rates, free models, long-context and fast tiers, Go peak hours, Muse sessions
  with a subagent, Gemini chats with `projects.json`, a Kilo database, and the native `find` listing
  parser).
- Local data found on this machine: Muse Code (5,673 session files, 5,639 of them subagents), Kilo CLI
  (`kilo.db`, 279 MB), Gemini CLI (22 chat files in WSL, 6 on Windows). Antigravity and Windsurf
  conversation stores are opaque binary; Cursor, Kiro, Copilot, and the Kilo VS Code extension hold
  no per-request token counts; Cline's CLI data held no tasks.
- An independent Python recount inside WSL matched the fixed broker exactly for Muse Code
  (9,571 calls, 1,037,211,966 tokens) and Gemini CLI (325 messages, 17,524,462 tokens); Kilo differed
  by 143,839 tokens from a recount taken minutes earlier while Kilo was in use.
- `agentnotifyd` from the build output on port 47899, `/ui/api/usage?days=all`: before the listing
  and buffer changes, **401 s** cold and **9.9 s** warm (walking 6,699 Muse files through the share
  alone took 10.4 s). After them, **132 s** cold, then **1.06 s** and **0.82 s**, with identical
  totals: 48,660 events, 7,064 files, nothing skipped, **$2,535.94** API-equivalent (was $1,441.76
  with the old catalog), 56.9M of 4.8B tokens unpriced (Codex records before a model is known, Gemini
  3 Pro Preview, Poolside Laguna, a custom OpenCode provider).
- A 30-day Codex check found 4 turns in fast mode (`service_tier: priority`) and no GPT-5.5/5.4
  request above 272K input tokens.

Not verified: the installed tray with these builds; macOS and Linux locations for Muse Code (only the
XDG default is read); Gemini CLI projects whose folder is a hash not listed in `projects.json`; a cold
scan is still over two minutes on this machine because parsed history is kept only in memory.
`CrossPlatformTests.ConfigStore_WritesTheTokenFileOwnerOnlyOnUnix` failed once in a full run on the
profiles worktree and passed alone and in two further full runs; it is unrelated to these changes.

## OpenCode Go billing cycle (`feature/opencode-go-renewal`, 2026-09-17)

Environment: the same Windows 11 host and WSL workspace. `scripts/build.sh` **0 warnings, 0 errors**;
`scripts/test.sh` **1,019 passed, 0 failed, 0 skipped**. New coverage: the cycle calculation around the
renewal day, month ends, leap years, and a year boundary in a fixed +05:30 zone; config normalization;
the monthly window counting only the current cycle with `starts_at`/`resets_at`; and the renewal-day
API including validation and the cross-origin refusal. `node --check` passed for `quota.js` and
`insights.js`.

Not verified: the renewal control has not been looked at in a browser, and no real OpenCode Go
console figure has been compared with the estimate. OpenCode's documentation does not state whether
its 5-hour and weekly limits roll or reset at fixed times, or the exact renewal time of day.
## API accounts (`feature/api-billing-accounts`, 2026-09-17)

Automated verification only:

- `./scripts/build.sh` — 0 warnings, 0 errors.
- `./scripts/test.sh` (worker run) — 1035 passed, 0 failed, 0 skipped, including new per-provider parsing
  (DeepSeek, Moonshot, SiliconFlow, OpenRouter, OpenAI/Anthropic pagination and cents
  conversion), error mapping (401, 404, 429 with `Retry-After`, 500, oversize body, invalid
  JSON, timeout), redirect handling, key-absence in endpoint JSON/snapshots/database bytes,
  acknowledgement, validation, rename/key-replacement, delete, cache/refresh throttling, and
  WebUI endpoint coverage.
- No live provider was called: every provider response in tests comes from a fake
  `HttpMessageHandler`.

Not verified: a human visual check of the API accounts cards and the Manage form in a browser,
including a narrow window; a real key against any provider; and whether Anthropic's cost report
amounts are cents as its guide states. Endpoint shapes come from the providers' official
documentation (DeepSeek, Moonshot/Kimi, SiliconFlow, OpenRouter, OpenAI Costs API, Anthropic Usage
and Cost API) read on 2026-09-17; OpenAI's pagination fields were not shown there and are treated
as optional.

The implementation was written by a delegated worker and reviewed before merging. Review fixes:
negative balances (Moonshot documents a non-positive available balance) were rejected as unreadable
and are now reported; a stored key that cannot be decrypted produced "could not be reached" and now
asks for the key to be replaced, without any request being sent; OpenAI and Anthropic date ranges
used the system clock instead of the injected one; the default service silently fell back to a
throwaway in-memory encryption key when the secret protector could not be created, which would have
stored keys no later start could decrypt, and now fails instead; and the API accounts section
blocked the whole Live quota page until every provider answered, and now loads on its own. After
the fixes `scripts/test.sh` passed **1,037 of 1,037** on the branch.

## Owner verification still outstanding

These need the repository owner and a real machine; nothing in CI can close them.

- The human WPF checks listed earlier in this file, for the settings theme and the
  built-in tones. No visual surface has been confirmed by a person.
- A human visual check of the new Usage page, including a narrow browser window.
- A human visual check of Live quota's Manage accounts, API accounts, and the OpenCode Go renewal
  control on Windows, including a narrow window. The owner has used these on the tray-hosted
  interface and reported them working.
- API accounts against real OpenAI and Anthropic Admin keys, Kimi, SiliconFlow, and OpenRouter; only
  DeepSeek has been checked with a real key.
- Apple Silicon and `terminal-notifier` on macOS remain unobserved.
- Redistribution rights for the four personal MP3s in the ignored `notification-tone/`
  folder. If they are clear, add them under `assets/tones/`, extend `BuiltInTones.All`,
  and record their provenance in `THIRD_PARTY_NOTICES.md`.
