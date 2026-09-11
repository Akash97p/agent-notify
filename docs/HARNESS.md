# Agent harnesses (auto-notify)

A skill teaches the model to call `agentnotify`. A **harness** removes the
remembering: the host itself calls AgentNotify at attention boundaries —
permission prompts, questions, session completion, session errors — through
its own hooks or plugin system.

Install both. The skill covers anything the harness does not (custom
milestones, mid-task decisions the host never sees); the harness covers the
model forgetting.

## Notify-only guarantee

Every harness in this directory is **notify-only**:

- It sends `agentnotify send` as a side effect and returns no decision.
- Hook scripts always exit `0`. The OpenCode plugin never throws.
- A missing CLI, stopped broker, or failed send is silent. The session
  continues exactly as if the harness were absent.
- Nothing the harness does can approve, deny, allow, or block a tool call.
  Returning a human answer into the waiting call (bidirectional response) is
  future work tracked as A02; see
  [BIDIRECTIONAL_AGENT_COMMUNICATION.md](BIDIRECTIONAL_AGENT_COMMUNICATION.md).

## Install

Offline, no downloads. One command per host:

```bash
agentnotify install-harness opencode
agentnotify install-harness codex
agentnotify install-harness claude
agentnotify install-harness gemini
agentnotify install-harness copilot
agentnotify install-harness cursor
agentnotify install-harness muse
```

`agentnotify install harness <agent>` is accepted as a readable alias.
`claude-code` is accepted for `claude`. Add `--scope project` to install
under the current repository, `--dry-run` to inspect the destination,
`--path DIRECTORY` for a custom harness root, or `--force` after reviewing
a locally modified harness file. Changed files are protected unless
`--force` is explicit. Hook JSON is merged: unrelated entries survive, and
reinstalling never duplicates the AgentNotify entries.

Restart the host session after installing. Hooks and plugins load at
startup.

### Where files land

```text
OpenCode     ~/.config/opencode/plugins/agentnotify.js
Codex        ~/.codex/agentnotify/agentnotify_hook.py + ~/.codex/hooks.json
Claude Code  ~/.claude/agentnotify/agentnotify_hook.py + ~/.claude/settings.json
Gemini CLI   ~/.gemini/agentnotify/agentnotify_hook.py + ~/.gemini/settings.json
Copilot CLI  ~/.copilot/agentnotify/agentnotify_hook.py + ~/.copilot/hooks/agentnotify.json
Cursor       ~/.cursor/agentnotify/agentnotify_hook.py + ~/.cursor/hooks.json
Muse Code    ~/.config/muse/agentnotify/agentnotify_hook.py + ~/.config/muse/settings.json
```

Project scope (`--scope project`) writes under the repository instead:

```text
OpenCode     <repo>/.opencode/plugins/agentnotify.js
Codex        <repo>/.codex/agentnotify/agentnotify_hook.py + <repo>/.codex/hooks.json
Claude Code  <repo>/.claude/agentnotify/agentnotify_hook.py + <repo>/.claude/settings.json
Gemini CLI   <repo>/.gemini/agentnotify/agentnotify_hook.py + <repo>/.gemini/settings.json
Copilot CLI  <repo>/.github/hooks/agentnotify.json + <repo>/.github/agentnotify/agentnotify_hook.py
Cursor       <repo>/.cursor/agentnotify/agentnotify_hook.py + <repo>/.cursor/hooks.json
Muse Code    <repo>/.muse/agentnotify/agentnotify_hook.py + <repo>/.muse/hooks.json
```

## What each harness watches

| Host | Permission / question | Completion | Error |
|---|---|---|---|
| OpenCode | `permission.asked`/`permission.updated` → `permission_required`; `question` tool → `input_required` | `session.idle` (and `session.status` idle) → `completed` | `session.error` → `error` |
| Codex | `PermissionRequest` → `permission_required` | `Stop`, `SessionEnd` → `completed` | — |
| Claude Code | `Notification` → `permission_required` | `Stop` → `completed` | — |
| Gemini CLI | `Notification` → `permission_required` | `AfterAgent`, `SessionEnd` → `completed` | — |
| Copilot CLI | `notification` → `permission_required` | `agentStop`, `sessionEnd` → `completed` | `errorOccurred` → `error` |
| Cursor | — (per-tool hooks are too noisy; `ask` mode is planned) | `stop`, `sessionEnd` → `completed` | — |
| Muse Code | `PermissionRequest` → `permission_required` | `Stop` → `completed` | — |

Permission notifications reuse one `--key` per project/session
(`<project>-<session>-permission`) so repeat prompts update rather than
pile up. Completion notifications carry no key. Resolve them from the CLI
(`agentnotify resolve ID`) or the notification center when done; the skill
describes the habit.

Subagent child sessions are skipped for OpenCode idle/error noise unless
`AGENTNOTIFY_INCLUDE_SUBAGENTS=1` is set. Override the CLI binary with
`AGENTNOTIFY_BIN` when it is not on `PATH`.

## Requirements

- The `agentnotify` CLI on `PATH` (`agentnotify.exe` on Windows/WSL).
- All hook-script harnesses (Codex, Claude, Gemini, Copilot, Cursor, Muse)
  need `python3` on `PATH` (the hook scripts use only the standard library).
  Copilot's PowerShell entries use `python`; on Windows, if only `python`
  exists, edit the recorded command or add a `python3` alias.
- OpenCode needs no extra runtime: the plugin uses `node:child_process`
  only, which Bun provides.

## Host notes

- **Gemini CLI**: all three events are advisory — the hook observes but never
  decides, which is exactly the notify-only contract. Google has announced
  Gemini CLI will be replaced by Antigravity CLI for unpaid tiers; if the
  binary or `~/.gemini` layout moves, correct `HarnessCatalog` rather than
  adding a parallel list.
- **Copilot CLI**: the `notification` event is fire-and-forget by design.
  The installer writes one owned file (`hooks/agentnotify.json`); keep custom
  hooks in a separate `*.json` file in the same directory.
- **Cursor**: user hooks (`~/.cursor/hooks.json`) do not run in cloud
  agents — only project hooks (`.cursor/hooks.json`) do. Install with
  `--scope project` for cloud-agent coverage.
- **Muse Code**: beta host, Developer Preview SDK, no stability promise.
  User-scope `settings.json` is the documented surface; the installer seeds
  the required `schema_version: 1` on fresh files and preserves yours.
  Project-scope `.muse/hooks.json` follows the Claude Code schema per
  third-party verification but is unconfirmed — start one session and check
  for a hooks warning.

## Verify tomorrow (manual checklist)

1. `agentnotify health` returns `ok`.
2. `agentnotify install-harness opencode --dry-run` prints the destination;
   without `--dry-run` it writes `agentnotify.js`. Restart OpenCode, run a
   task that asks a permission, and confirm a desktop notification appears.
3. Same for `codex`: accept the trust prompt if Codex asks about the
   project `.codex` layer, trigger an approval, and confirm the
   notification. `Stop` fires when the agent finishes a response.
4. Same for `claude`: `/hooks` or settings check shows the entries;
   trigger a permission prompt and a task completion.
5. Same for `gemini` (`Notification`/`AfterAgent`), `copilot`
   (`notification`/`agentStop`), `cursor` (`stop`), and `muse`
   (`PermissionRequest`/`Stop`, watching for a hooks warning on first run).
6. `agentnotify list --unresolved` shows the harness-sent rows; `resolve`
   clears them.
7. Temporarily stop the broker and confirm the session still works (the
   harness must fail silently).

Real-host display checks have **not** been performed in this branch —
automated tests cover install/merge/idempotency only. Record results in
`docs/VERIFICATION.md`.

## Manual install

Prefer the CLI, which handles absolute paths and JSON merging. To do it by
hand:

- OpenCode: copy `distribution/harness/opencode/agentnotify.js` to
  `~/.config/opencode/plugins/agentnotify.js` (legacy singular
  `~/.config/opencode/plugin/` also works on older builds) and restart.
- Codex: copy `distribution/harness/shared/agentnotify_hook.py` to
  `~/.codex/agentnotify/agentnotify_hook.py`, then merge
  `distribution/harness/codex/hooks.example.json` into `~/.codex/hooks.json`,
  replacing `HOOK_DIR` with `~/.codex/agentnotify`.
- Claude Code: copy the same script to
  `~/.claude/agentnotify/agentnotify_hook.py`, then merge
  `distribution/harness/claude/settings.example.json` into
  `~/.claude/settings.json`, replacing `HOOK_DIR` the same way.
- Gemini CLI: copy the same script to
  `~/.gemini/agentnotify/agentnotify_hook.py`, then merge
  `distribution/harness/gemini/settings.example.json` into
  `~/.gemini/settings.json`.
- Copilot CLI: copy the same script to
  `~/.copilot/agentnotify/agentnotify_hook.py`, then copy
  `distribution/harness/copilot/agentnotify.example.json` to
  `~/.copilot/hooks/agentnotify.json`, replacing `HOOK_DIR`.
- Cursor: copy the same script to
  `~/.cursor/agentnotify/agentnotify_hook.py`, then merge
  `distribution/harness/cursor/hooks.example.json` into
  `~/.cursor/hooks.json`.
- Muse Code: copy the same script to
  `~/.config/muse/agentnotify/agentnotify_hook.py`, then merge
  `distribution/harness/muse/settings.example.json` into
  `~/.config/muse/settings.json` (keep `schema_version: 1`).

## Uninstall

- OpenCode: delete `agentnotify.js` from the plugin directory.
- Codex: delete the three AgentNotify blocks from `hooks.json` and remove
  `~/.codex/agentnotify/`.
- Claude Code: delete the two AgentNotify blocks from `settings.json` and
  remove `~/.claude/agentnotify/`.
- Gemini CLI: delete the three AgentNotify blocks from `settings.json` and
  remove `~/.gemini/agentnotify/`.
- Copilot CLI: delete `hooks/agentnotify.json` and remove
  `~/.copilot/agentnotify/`.
- Cursor: delete the two AgentNotify blocks from `hooks.json` and remove
  `~/.cursor/agentnotify/`.
- Muse Code: delete the two AgentNotify blocks from `settings.json` and
  remove `~/.config/muse/agentnotify/`.

## Compatibility contract

- Hook entry points are versioned by the host, not by AgentNotify. When a
  host renames an event (OpenCode has done so before: `session.idle` →
  `session.status`), the plugin handles both names; correct
  `HarnessCatalog`/`HarnessInstaller` rather than adding a parallel list.
- Unknown future events are ignored silently by design.
- The tray Install tab does not install harnesses yet; the CLI is the
  installer until a Settings surface is built and visually verified.
