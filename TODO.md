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
- [x] Local daily logs without token logging
- [x] History retention pruning

## Desktop experience

- [x] Branded tray/window/setup icon from `an.ico`
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

- [x] `send`, shorthand, `list`, `get`, `resolve`, `dismiss`, `health`, `token`, and version commands
- [x] Snake-case and kebab-case CLI type parsing
- [x] Friendly nonzero exits for invalid arguments, broker connection failures, and timeouts
- [x] WSL wrapper and Windows user PATH installation
- [x] Validated `agentnotify` skill and OpenAI UI metadata
- [x] Offline browser guide with Copy/Download skill actions

## Distribution and open source

- [x] One self-contained `AgentNotifySetup.exe` distributable
- [x] Per-user install, Start menu, optional desktop shortcut, startup, PATH, and uninstall registration
- [x] Publisher/author/version metadata
- [x] MIT License and setup no-warranty acknowledgement
- [x] README, contributing, security, architecture, installation, API, integration, roadmap, and verification docs
- [x] Preserve local user data on uninstall
- [ ] Authenticode signing and timestamping
- [ ] Published SHA-256 checksums
- [ ] ARM64 installer
- [ ] Automatic updates and schema/config migration framework

## Quality

- [x] Windows .NET 10 release build: 0 warnings, 0 errors
- [x] Automated tests: 176 passed, 0 failed, 0 skipped
- [x] Installer packaging and embedded skill validation
- [ ] Human visual verification on 100%, 150%, and 200% DPI
- [ ] Human multi-monitor/taskbar-position verification
- [ ] Clean-profile install/uninstall verification on a second Windows user

## Future delivery channels

- [x] Hardened generic HTTPS webhook adapter and native provider/route/diagnostics UI
- [x] Authenticated SMTP adapter with required TLS and recipient allowlist
- [x] Official Telegram Bot API adapter with encrypted token/destination
- [ ] Email provider API adapters
- [ ] Microsoft Teams, Slack, and Discord adapters
- [ ] Official WhatsApp Business Cloud API adapter
- [ ] SMS and mobile push adapters
- [ ] Quiet hours, snooze, escalation, grouping, and per-project routing
- [ ] Response buttons and acknowledgement callbacks to agents
- [ ] MCP server and richer language SDKs

External delivery must remain disabled by default and complete the security/privacy design in `SECURITY.md` and `docs/ROADMAP.md` first.

The complete task breakdown and provider-by-provider implementation order lives in `docs/FEATURE_BACKLOG.md`. Durable branch and decision state lives in `docs/DEVELOPMENT_STATE.md`.
