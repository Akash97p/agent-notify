# Roadmap

AgentNotify keeps the local broker, notification lifecycle, and history as the source of truth as opt-in delivery capabilities expand.

## Near term

- Keep the completed native Settings UI, custom notification definitions, managed WAV/MP3 sounds, and channel diagnostics stable while human visual/audio/accessibility checks are completed.
- Quiet hours, schedules, snooze, escalation, routing, grouping, cooldowns, and coalescing controls.
- Native Windows toast/center answer controls and host-acceptance receipts for the shipped
  interaction broker and WebUI/Relay answer path. See
  [BIDIRECTIONAL_AGENT_COMMUNICATION.md](BIDIRECTIONAL_AGENT_COMMUNICATION.md).
- A managed Agent Client Protocol bridge, broader answer adapters, and sealed mobile responses.
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
7. MQTT 5 with TLS/mTLS for self-hosted/enterprise automation (implemented).
8. AWS SNS and other cloud messaging services are backlog candidates, not current implementations. They require a separate security and cost review before work begins.

Each provider must be opt-in and independently configurable. Required design work includes Windows-protected credential storage, verified destinations, content-redaction rules, retry queues, idempotency, delivery status, quotas/cost limits, provider rate limits, and a test-send workflow.

WhatsApp must use the official business platform rather than browser automation or personal-account scraping. Email must not silently inherit machine credentials or expose notification bodies without explicit user configuration.

## Agent ecosystem

- Agent Client Protocol client for AgentNotify-managed coding sessions.
- More native answer adapters for existing coding-agent sessions; Codex/Claude ask hooks, Hermes,
  and OpenClaw answer paths already exist.
- Optional MCP elicitation and A2A/AEP projections after the response contract is stable.
- Small SDKs for PowerShell, .NET, Python, JavaScript, and shell environments.
- Agent heartbeat and “currently waiting” status.
- Host-acceptance acknowledgements and richer structured responses beyond the shipped
  permission, single-choice, and text interactions.
- Additional local transports such as named pipes, without replacing the REST API prematurely.

## Longer term

- Notification policies per project, agent, type, priority, and time window.
- Multiple profiles and destination routing.
- Secure remote/LAN mode with a separate authentication design.
- Search, export, audit views, and bounded retention policies.
- Native Windows Notification Center integration as an optional secondary surface.
- Extend the shipped macOS quota menu bar into a full notification center/settings client, and build a Linux tray/desktop edition. Contributors for both platforms are explicitly welcome.

The complete provider-by-provider task list and confidence constraints are maintained in `FEATURE_BACKLOG.md`.

No roadmap item is a compatibility promise. Security/privacy review and a small understandable implementation take precedence over provider count. MQTT 5 was the last outbound adapter merged; roadmap items do not imply that a provider branch is active.
