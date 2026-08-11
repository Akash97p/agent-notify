# Development state

This is the durable handoff record for long-running AgentNotify development. Update it at every merged milestone and before pausing work.

## Current baseline

- Local repository only; no remote is configured or authorized.
- Stable branch: `main`.
- Integration branch: `dev`.
- Imported working baseline: `4be4c1a` (`chore: import working AgentNotify baseline`).
- Baseline verification on 2026-08-12: Release build succeeded with 0 warnings and 0 errors; 87 tests passed.
- Manual user verification: tray menu actions work; notification center receives events; custom Windows toasts were seen; skill copy/download works.
- Current product version: `1.0.0`, Windows x64, unsigned.

## Decisions that must survive context compaction

1. Use a native WPF Settings window opened from the tray as the primary configuration UI. An optional browser dashboard may be added later, but secrets must not be exposed through it.
2. Keep SQLite as the source for notification history and add provider profiles, routing rules, durable outbox entries, and delivery attempts through explicit schema migrations.
3. Protect provider credentials before persistence with Windows DPAPI using current-user scope. Store only encrypted envelopes in SQLite and redact secret fields from APIs/logs.
4. Keep local desktop notification delivery authoritative. Outbound channels are opt-in secondary deliveries and cannot make notification creation fail.
5. Implement one bounded capability per topic branch, verify it, merge it locally into `dev`, then branch from the updated `dev`.
6. Prefer official APIs. Signal support, if added through `signal-cli`, is experimental and must be clearly labeled as an unofficial user-managed bridge.
7. No commits or edits on `main`; no remote creation or push without a new explicit user request.
8. Future macOS and Linux clients are first-class roadmap goals. Avoid putting portable routing/domain logic in WPF classes.

## Active sequence

1. Completed on `docs/foundation-and-onboarding`: README image/build guide, Getting Started title fix and GitHub link, contributor workflow, durable backlog, cross-agent skill guide.
2. Completed on `chore/github-release-pages`: portable PowerShell packaging, CI/tagged-release workflows, checksum generation, and a locally buildable GitHub Pages documentation site. No remote was created.
3. Completed on `feature/settings-window`: native tray-opened Settings window with validated general, toast placement/lifetime, pause/DND, and initial sound controls. Build/tests passed; human visual inspection remains pending.
4. Completed on `feature/custom-notification-types`: backward-compatible string IDs, custom definition editor, accent/label/default priority/lifetime/enabled settings, legacy normalization, safe fallback, CLI/API/SQLite runtime smoke, and 96 passing tests. Human Settings visual inspection remains pending.
5. Completed on `feature/notification-sounds`: managed WAV/MP3 import, global/per-type selection, preview, volume, pause/DND policy, safe fallback, platform-neutral file store, and 101 passing tests. Human audio/UI verification with a real user-selected file remains pending.
6. Completed on `feature/delivery-foundation`: atomic SQLite v1 migration, provider/route/outbox/attempt repositories, atomic concurrent claiming, DPAPI current-user and test AES-GCM protectors, bounded/validated encrypted profile service, routing/payload policy, bounded retry schedule, and 112 passing tests.
7. Completed on `feature/delivery-dispatcher`: schema v2 outbox idempotency, failure-isolated API persistence hook, single-worker adapter contract, atomic claiming, restart recovery, timeout, bounded jittered retries, dead-letter state, sanitized attempts/logging, exact diagnostics, bounded test-send, and 120 passing tests.
8. Completed on `feature/channel-webhook`: encrypted endpoint URL, HTTPS-only POST, redirect/cookie/proxy suppression, connect-time DNS/IP policy, explicit private-network consent, safe header/secret-header maps, idempotency key, optional HMAC-SHA-256 signature, bounded JSON templates, status retry classification, response-body avoidance, dispatcher registration, and 144 passing tests.
9. Completed on `feature/channel-management-ui`: native webhook provider CRUD, encrypted blank-means-preserve secret patching/removal, enable/private-network consent, test-send, validated route CRUD/filters/off-device message consent, destructive confirmations, exact redacted diagnostics, wider Settings layout, and 147 passing tests. Human visual/accessibility inspection remains pending.
10. Next: SMTP email adapter on `feature/channel-smtp`, then Telegram and the remaining priority list.
11. Additional channels in the priority order maintained in `docs/FEATURE_BACKLOG.md`.

## Next resume action

Inspect the current branch and status, finish its documented acceptance criteria, run the required gates, update this file, merge the branch to `dev`, and create the next branch from `dev`.
