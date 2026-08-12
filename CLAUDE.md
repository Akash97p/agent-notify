# AgentNotify - Claude Code instructions

These instructions apply to all work in this repository.

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

## Muse Worker Delegation

You have access to a secondary coding agent powered by **Muse Spark 1.2 Contributor** through a persistent OpenCode worker.

### Worker

The worker should be available at:

`http://127.0.0.1:4096`

If it is not running, start it in the background:

```bash
opencode serve --hostname 127.0.0.1 --port 4096 >/tmp/opencode-muse.log 2>&1 &
```

Delegate work with:

```bash
opencode run \
  --attach http://127.0.0.1:4096 \
  --model meta/muse-spark-1.2-contributor \
  --variant high \
  "<TASK>"
```

Always use:

* `--attach http://127.0.0.1:4096`
* `--model meta/muse-spark-1.2-contributor`

Do not substitute another model.

### Reasoning effort

Choose the variant per task.

#### `medium`

Only for simple, deterministic, well-specified work:

* rename/move files
* update imports
* formatting
* simple git operations
* obvious one-line fixes
* running a specific command
* trivial mechanical edits

#### `high` — DEFAULT

Use for normal engineering work:

* feature implementation
* bug fixes
* tests
* refactors
* repository investigation
* multi-file changes

When uncertain, use `high`.

#### `xhigh`

Use when substantial reasoning is justified:

* major implementations
* difficult debugging
* architectural refactors
* unclear root causes
* complex cross-cutting changes
* tasks where the implementation approach is uncertain

Do not use `xhigh` merely because many files are touched.

### Delegation workflow

For substantial implementation work:

1. Analyze the task yourself.
2. Decide the architecture and implementation approach.
3. Give Muse a detailed, self-contained implementation prompt.
4. Include relevant files/areas, constraints, and acceptance criteria.
5. Let Muse inspect the repository, modify the working tree, and run relevant tests.
6. After Muse finishes, inspect `git diff` yourself.
7. Review the implementation for correctness and unintended changes.
8. Run or verify relevant tests yourself.
9. Fix small issues yourself or delegate a narrowly scoped follow-up.

Do not blindly accept Muse's implementation.

### What to delegate

Prefer Muse for:

* implementing a well-defined plan
* codebase exploration
* feature implementation
* boilerplate
* tests
* repetitive edits
* mechanical refactors
* debugging that can be independently verified

Keep with Claude:

* architecture and design decisions
* interpreting ambiguous requirements
* security-sensitive/high-risk decisions
* final review
* deciding whether the completed work satisfies the user's request

### Token efficiency

Use Muse for implementation-heavy repository work instead of reading and editing every relevant file yourself.

Give Muse enough context to work independently, but request a concise completion summary. Afterward, inspect the resulting diff and only read files needed for final review.

Avoid asking Muse to dump large file contents, logs, or reasoning into its response unless needed for debugging.
