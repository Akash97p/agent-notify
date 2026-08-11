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
4. Next: custom notification definitions with name, presentation, lifetime, priority defaults, and migration from built-ins.
5. Sound profiles: global/per-type sound selection, preview, mute, and safe local file handling.
6. Delivery foundation: SQLite profiles/routes/outbox/attempts, DPAPI secret protection, dispatcher, retries, diagnostics, test-send.
7. Channels in the priority order maintained in `docs/FEATURE_BACKLOG.md`.

## Next resume action

Inspect the current branch and status, finish its documented acceptance criteria, run the required gates, update this file, merge the branch to `dev`, and create the next branch from `dev`.
