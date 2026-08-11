# AgentNotify agent workflow

These instructions apply to every coding agent working in this repository.

## Start or resume work

1. Read `docs/DEVELOPMENT_STATE.md`, `docs/FEATURE_BACKLOG.md`, `TODO.md`, and the relevant architecture/security documentation.
2. Inspect `git status`, the current branch, and recent commits before editing.
3. Preserve user changes and runtime secrets. Never read or print the bearer token unless direct API debugging was explicitly requested.
4. Update `docs/DEVELOPMENT_STATE.md` whenever a decision, verification result, migration, or next step would otherwise be lost after context compaction.

## Local Git workflow

- This repository is local-only unless the user explicitly asks to configure or use a remote.
- `main` is the stable release line. Never edit or commit while checked out on `main`.
- `dev` is the integration branch. Do not implement features directly on `dev`.
- Create each branch from an up-to-date local `dev`:
  - `feature/<short-name>` for product capabilities;
  - `fix/<short-name>` for defects;
  - `docs/<short-name>` for documentation;
  - `test/<short-name>` for test-only work;
  - `chore/<short-name>` for tooling and maintenance.
- Keep commits focused and use imperative Conventional Commit subjects such as `feat: add webhook delivery`.
- Build and test the branch in proportion to its risk. Update documentation and migrations in the same branch as the behavior they describe.
- Merge a verified branch into local `dev` with `git merge --no-ff`. Delete the local topic branch after the merge when safe.
- Start the next topic branch from the newly merged `dev`.
- Never rewrite shared history, force-push, or use destructive Git commands.

## Required quality gates

Run Windows .NET tooling through the repository scripts from WSL:

```bash
./scripts/build.sh
./scripts/test.sh
./scripts/package.sh
```

For narrow iterations, targeted tests are acceptable during development; the full build and test suite are required before merging to `dev`. Packaging is required when installer payload, embedded resources, publish settings, or release automation changes.

Validate the distributable skill after modifying it:

```bash
python3 /home/akash/.codex/skills/.system/skill-creator/scripts/quick_validate.py distribution/agentnotify
```

Record manual WPF checks honestly in `docs/VERIFICATION.md`. Never claim a visual or integration test that was not performed.

## Architecture and security guardrails

- Keep the local API loopback-only and bearer-authenticated.
- Persist provider definitions, routes, and delivery attempts in SQLite.
- Encrypt provider secrets with Windows DPAPI scoped to the current user before SQLite storage. Never log, expose through list APIs, or include secrets in notification metadata.
- Keep outbound channels disabled until explicitly configured and enabled by the user.
- Dispatch outbound delivery away from the WPF dispatcher and API request path through a durable outbox/retry boundary.
- Apply bounded retries, timeouts, payload limits, redaction controls, idempotency, and provider-specific rate limits.
- Prefer official provider APIs. Clearly label unofficial bridges and keep them opt-in.
- Add schema migrations that preserve existing notification history and tolerate upgrades.
- Keep the app usable without any external channel or internet connection.

## Product direction

AgentNotify should become the common human-attention layer for coding agents. Preserve agent/project identity, unresolved state, deduplication, and local history as the source of truth. Windows is the current implementation; design portable core boundaries so contributors can add macOS and Linux applications later.

