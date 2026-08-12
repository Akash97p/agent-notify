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
Passed: 552
Failed: 0
Skipped: 0
```

Coverage includes validation, lifecycle transitions, config recovery/round-trip, custom type normalization/definitions/default priority, managed sound import/sanitization/deduplication and sound profile resolution, SQLite notification and delivery migrations/CRUD/outbox/attempts, atomic concurrent outbox claiming, DPAPI/AES-GCM secret round-trip and redaction, provider input bounds, credential-preserving/merging/removal updates, validated route service updates, idempotent route materialization, dispatcher success/timeout/retry/dead-letter/recovery, sanitized diagnostics and bounded test-send, webhook templating/headers/HMAC/idempotency/status policy/private-address controls, SMTP message projection/authentication/TLS modes/recipient normalization/injection and address policy/status propagation, Telegram encrypted destination projection/plain-text limits/fixed endpoint JSON/status policy, Discord encrypted URL validation/mention suppression/Markdown escaping/content limits/thread/status policy, Slack/GovSlack URL validation/control-sequence suppression/threading/bounded response/status policy, Teams/Zoho Cliq/Google Chat URL and payload hardening, Mattermost self-hosted destination/acknowledgement/mention controls, Matrix/ntfy/Gotify/Pushover/Pushbullet/Twilio SMS/direct WhatsApp/Twilio WhatsApp encrypted destination and acknowledgement contracts, routing/retry policy, authentication, health, create/list/get/patch/dismiss, malformed/null JSON, callback isolation including outbound queue failure, keyed deduplication including concurrent calls, CLI connection failure, bare `--unresolved`, hyphenated and custom notification type parsing, invalid identifier rejection, and version output.

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
b803d891b4150d9f3acddb884469e7ce6bcc53d3ecc2191482ec55e299e22e9a  AgentNotifySetup.exe
```

Regenerate the checksum after any rebuild because it necessarily changes with the binary.

The portable `scripts/package.ps1` implementation and WSL wrapper generated a matching `artifacts/SHA256SUMS.txt`. The static Pages source built locally into `_site`; GitHub-hosted workflows cannot be execution-tested until the owner chooses to push the local repository.

### Custom notification type smoke

The packaged tray and CLI accepted `--type deployment-waiting`, normalized and persisted `deployment_waiting`, returned the same identifier through `get`, displayed a fallback toast, and resolved the row. The Settings definition editor and configured accent/label rendering are compiled and covered by configuration tests but still require a human visual pass.

### Sound verification boundary

Automated tests prove WAV/MP3 extension and size validation, safe content-addressed import, duplicate reuse, managed-path resolution, configuration sanitization, and per-type fallback. WPF media playback compiles and is isolated from the API path, but audible playback/preview remains a human check because no user-selected audio file was assumed or modified during automation.

Windows version-resource inspection confirmed for setup, tray, and CLI:

```text
Product: AgentNotify
Company: Kabani Tech Private Limited
File version: 1.0.0.0
```

Authenticode status is `NotSigned`, as documented.

### Published tray/API/CLI smoke

The actual packaged `AgentNotify.Tray.exe` and `agentnotify.exe` were copied to a temporary directory on the Windows `D:` filesystem and executed. Verified:

- tray/API process started;
- authenticated `health` returned version `1.0.0`, API `v1`, and the running PID;
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

The installed CLI returned `agentnotify 1.0.0`. The Windows uninstall registry entry reported AgentNotify, version 1.0.0, and publisher Kabani Tech Private Limited. Running the registered uninstall script removed the known files and uninstall registration. The temporary test directory was then removed.

## Verified by implementation and compilation, not visually inspected

- Tray menu includes Notification Center, Getting Started, Copy/Download skill, Pause, Start with Windows, logs, and Exit.
- The supplied multi-resolution `an.ico` is compiled into app/setup resources and executable icon metadata.
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
