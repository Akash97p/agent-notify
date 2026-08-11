---
name: agentnotify
description: Send local Windows desktop notifications through AgentNotify when a coding agent needs human input, permission, a decision, or attention, or when it completes, fails, or becomes blocked. Use during autonomous coding work to surface actionable state changes from Windows, PowerShell, Command Prompt, or WSL without interrupting routine progress.
---

# AgentNotify

Use the installed `agentnotify` CLI to alert the user at meaningful attention boundaries. AgentNotify runs locally, reads its own per-user credentials, and displays a Windows toast without stealing focus.

## Check availability

Run once near the start of a task:

```bash
agentnotify.exe health
```

On Windows, `agentnotify health` is equivalent. If the command is unavailable or the broker is not running, continue the user's task and report the notification failure only when it is relevant; do not repeatedly retry.

## Send notifications

Use a stable `--key` for an unresolved condition so updates replace the existing notification instead of creating duplicates.

```bash
agentnotify.exe send \
  --agent "codex" \
  --project "project-name" \
  --type input_required \
  --priority high \
  --key "project-name-decision" \
  --title "Need your decision" \
  --message "Choose option A or B."
```

Choose the semantic type deliberately:

- `input_required`: a question or decision blocks useful progress.
- `permission_required`: explicit approval or authorization is required.
- `blocked`: work cannot continue because of a prerequisite or external dependency.
- `error`: a significant operation failed and needs attention.
- `completed`: the requested work is finished.
- `success`: an important intermediate operation succeeded.
- `warning`: attention is advisable, but useful work can continue.
- `info`: a meaningful non-actionable milestone; use sparingly.

Use `low`, `normal`, `high`, or `critical` for `--priority`. Reserve `critical` for genuinely urgent conditions.

Use a user-defined `--type` only when the user or project instructions identify one configured in AgentNotify. Otherwise prefer the portable built-in types above.

## Notification discipline

- Notify when user input or permission is actually needed, before waiting.
- Notify on final completion when the task was long-running or the user may be elsewhere.
- Notify on a terminal error or blocker after exhausting safe in-scope alternatives.
- Do not notify for routine tool calls, ordinary progress updates, or every substep.
- Keep the title short and put the concrete question, result, or blocker in the message.
- Include `--project`, and include `--agent-instance` when multiple concurrent agents may share a project.

## Resolve attention requests

Capture the notification `id` returned by `send`. Once the issue is answered or no longer active, clear it:

```bash
agentnotify.exe resolve NOTIFICATION_ID
```

Use `dismiss` only when the notification should be removed without recording it as resolved.

## Useful commands

```bash
agentnotify.exe list --unresolved true
agentnotify.exe get NOTIFICATION_ID
agentnotify.exe dismiss NOTIFICATION_ID
```

Quote titles, messages, paths, and project names. Never read, print, or transmit the bearer token unless direct API debugging is explicitly required.
