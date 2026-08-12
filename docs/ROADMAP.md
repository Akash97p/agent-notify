# Roadmap

AgentNotify keeps the local broker, notification lifecycle, and history as the source of truth as opt-in delivery capabilities expand.

## Near term

- Native Settings UI for port, corner, durations, retention, startup, pause state, channels, and diagnostics.
- Extensible built-in/custom notification definitions and global/per-type WAV or MP3 sounds.
- SQLite delivery profiles/routes/outbox/attempts with DPAPI-encrypted provider secrets.
- Quiet hours, schedules, snooze, escalation, routing, grouping, and coalescing controls.
- Response/acknowledgement buttons with a safe path back to the waiting agent.
- Better “Open Agent” support for Windows Terminal tabs, editors, and virtual desktops.
- ARM64 builds, Authenticode-signed releases, checksums, and update/migration tooling.

## External delivery adapters

Potential adapters, in rough priority order:

1. Generic outgoing HTTPS webhooks for self-hosted automation (adapter and management UI implemented).
2. Email through authenticated TLS SMTP (implemented), followed by provider APIs where they add value.
3. Telegram Bot API, Discord/Slack webhooks, global-cloud Teams Workflows, Zoho Cliq, Google Chat, Mattermost, and unencrypted Matrix rooms (implemented).
4. ntfy, Gotify, Pushover, and Pushbullet (implemented).
5. Twilio SMS with an encrypted single-recipient allowlist and one-segment/priority cost controls (implemented); durable daily/account spend budgets remain planned.
6. WhatsApp approved-template delivery through the official Meta Cloud API and optional Twilio Content Templates (implemented); delivery-status webhooks and durable spend controls remain planned.
7. MQTT and selected cloud messaging services for self-hosted/enterprise automation.

Each provider must be opt-in and independently configurable. Required design work includes Windows-protected credential storage, verified destinations, content-redaction rules, retry queues, idempotency, delivery status, quotas/cost limits, provider rate limits, and a test-send workflow.

WhatsApp must use the official business platform rather than browser automation or personal-account scraping. Email must not silently inherit machine credentials or expose notification bodies without explicit user configuration.

## Agent ecosystem

- Optional MCP server.
- Small SDKs for PowerShell, .NET, Python, JavaScript, and shell environments.
- Agent heartbeat and “currently waiting” status.
- Acknowledgement callbacks and structured user responses.
- Additional local transports such as named pipes, without replacing the REST API prematurely.

## Longer term

- Notification policies per project, agent, type, priority, and time window.
- Multiple profiles and destination routing.
- Secure remote/LAN mode with a separate authentication design.
- Search, export, audit views, and bounded retention policies.
- Native Windows Notification Center integration as an optional secondary surface.
- Extract portable contracts/routing components and build native macOS menu-bar and Linux desktop editions. Contributors for both platforms are explicitly welcome.

The complete provider-by-provider task list and confidence constraints are maintained in `FEATURE_BACKLOG.md`.

No roadmap item is a compatibility promise. Security/privacy review and a small understandable implementation take precedence over provider count.
