# Feature backlog

This backlog turns the long-term product direction into independently testable branches. Ordering may change when a prerequisite or security concern is discovered, but each completed task must update this file and `docs/DEVELOPMENT_STATE.md`.

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
| C19 | MQTT | Publish to configured TLS broker/topic | High for self-hosted automation; credentials/certificates protected |
| C20 | AWS SNS | Signed AWS API/SDK publish | Medium; IAM and credential-chain UX must be carefully scoped |
| C21 | Azure Communication Services | Email/SMS provider SDK/API | Medium; connection credentials and cost controls required |
| C22 | SendGrid | Mail Send API | High; useful when SMTP is unavailable |
| C23 | Mailgun | Messages API | High; region and domain configuration required |
| C24 | Postmark | Email API | High; server token and sender validation required |
| C25 | Apprise bridge | User-managed local Apprise CLI/API | Experimental bridge offering many community transports |

## Agent communication tasks

### A01 — Acknowledgement callbacks

Allow an agent to provide a safe loopback callback or polling correlation ID and observe delivered, viewed, dismissed, resolved, or failed state.

### A02 — Structured response actions

Support buttons and bounded text choices that return a structured response to a waiting agent without executing arbitrary commands.

### A03 — Agent registry and heartbeat

Track live agent instances, projects, working directories, last activity, and waiting state so the center answers “which agents need me?” reliably.

### A04 — SDKs and protocols

Publish small PowerShell, shell, Python, JavaScript, and .NET clients; then evaluate an optional MCP server without replacing the stable REST/CLI path.

## Product and platform tasks

- Search, filtering, export, route/delivery audit views, backups, and retention controls.
- Safer terminal/editor activation, Windows Terminal integration, and virtual desktop awareness.
- Signed x64/ARM64 releases, checksums, schema migration recovery, automatic updates, and rollback.
- Accessibility, keyboard navigation, localization, high-contrast support, multi-DPI/multi-monitor verification.
- Portable core extraction followed by native macOS menu-bar and Linux tray/desktop implementations.
- Documentation/wiki site, examples, architecture decision records, contributor guides, and integration recipes.
