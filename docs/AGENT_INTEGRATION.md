# Agent integration guide

Prefer the installed `agentnotify.exe` CLI. It discovers the local port/token, produces correct JSON, handles errors, and keeps the broker as the lifecycle source of truth.

## Install the agent skill

Right-click the AgentNotify tray icon and select **Copy agent SKILL.md** or **Download agent SKILL.md…**. The offline getting-started page also provides Copy and Download actions.

For Codex:

```bash
mkdir -p ~/.codex/skills/agentnotify
cp /path/to/downloaded/SKILL.md ~/.codex/skills/agentnotify/SKILL.md
```

The canonical source is `distribution/agentnotify/SKILL.md`.

## Availability check

An agent may check once near task start:

```bash
agentnotify.exe health
```

If AgentNotify is unavailable, continue useful work and report the notification failure only when relevant. Do not loop or block the user’s task trying to restore the broker.

## Attention request

```bash
agentnotify.exe send \
  --agent codex \
  --agent-instance codex-71dc \
  --project payments \
  --type input_required \
  --priority high \
  --key codex-71dc-schema-decision \
  --title "Need schema decision" \
  --message "Choose normalized or denormalized storage."
```

Capture the returned `id`. When the condition is answered or otherwise clears:

```bash
agentnotify.exe resolve NOTIFICATION_ID
```

## Completion, blocker, and permission examples

```bash
agentnotify.exe send --agent codex --project payments \
  --type completed --title "Task complete" --message "Implementation is complete and the test suite passes."

agentnotify.exe send --agent codex --project payments \
  --type blocked --priority high --key payments-blocker \
  --title "Blocked" --message "The required signing certificate is unavailable."

agentnotify.exe send --agent codex --project payments \
  --type permission_required --priority high --key payments-production-approval \
  --title "Approval required" --message "May I deploy this build to production?"
```

Use notifications at attention boundaries, not as a transcript. Routine progress, tool calls, and ordinary substeps should not generate toasts.

## Type selection

- `input_required`: user answer/decision blocks useful work.
- `permission_required`: explicit authorization is required.
- `blocked`: prerequisite/external condition prevents progress.
- `error`: significant failed operation needs attention.
- `completed`: requested work finished.
- `success`: important intermediate operation succeeded.
- `warning`: attention is advisable but progress can continue.
- `info`: meaningful non-actionable milestone; use sparingly.

Sticky attention types are shown until dismissed/resolved. Timed types transition to dismissed when their toast expires and appear under Recent.

## Deduplication

Use a stable key per logical unresolved condition:

```bash
agentnotify.exe send --key build-main --type error --title "Build failed" --message "12 errors"
agentnotify.exe send --key build-main --type warning --title "Build improving" --message "2 errors remain"
```

The second request updates the active notification. Once dismissed/resolved, reusing the key creates a new lifecycle.

## WSL

The Windows installer adds `agentnotify.exe` to the Windows user `PATH`. Open a new WSL shell after installation so WSL imports that change, then call the executable directly:

```bash
agentnotify.exe send --type info --title "From WSL" --message "The agent can reach Windows."
```

For repository development:

```bash
./scripts/package.sh
./scripts/install-wsl-wrapper.sh
agentnotify health
```

The wrapper first uses `agentnotify.exe` from PATH, then the local packaged payload. It does not read or print the token.

## Direct HTTP fallback

Use direct HTTP only when the CLI cannot be used. Obtain the token without echoing it into logs, send it as `Authorization: Bearer`, and follow `docs/API.md`. Never include the token in prompts, issue reports, notification metadata, or remote requests.

## Prompt snippet

```text
Use AgentNotify at meaningful attention boundaries. Notify before waiting for required input or permission, after exhausting safe alternatives when blocked, and on completion of long-running work. Use a stable key for one unresolved condition, capture the returned ID, and resolve it when the condition clears. Do not notify for routine progress.
```
