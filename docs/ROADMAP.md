# Roadmap

AgentNotify V1 is intentionally local-only. The local broker, notification lifecycle, and history remain the source of truth as delivery capabilities expand.

## Near term

- Settings UI for port, corner, durations, retention, startup, and pause state.
- Quiet hours, schedules, snooze, and escalation rules.
- Per-agent/project icons, sounds, grouping, and coalescing controls.
- Response/acknowledgement buttons with a safe path back to the waiting agent.
- Better “Open Agent” support for Windows Terminal tabs, editors, and virtual desktops.
- ARM64 builds, Authenticode-signed releases, checksums, and update/migration tooling.

## External delivery adapters

Potential adapters, in rough priority order:

1. Generic outgoing webhooks for self-hosted automation.
2. Email via user-configured SMTP or provider API.
3. Microsoft Teams, Slack, and Discord.
4. WhatsApp through the official WhatsApp Business Cloud API.
5. SMS and mobile push through explicitly selected providers.

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

No roadmap item is a compatibility promise. Security/privacy review and a small understandable implementation take precedence over provider count.
