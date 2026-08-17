---
name: agentnotify
description: Send local desktop notifications through AgentNotify when a coding agent needs human input, permission, a decision, or attention, or when it completes, fails, or becomes blocked. Use during autonomous coding work to surface actionable state changes from Windows, PowerShell, Command Prompt, WSL, macOS, or Linux without interrupting routine progress.
---

# AgentNotify

Use the installed `agentnotify` CLI to alert the user at meaningful attention boundaries.
AgentNotify runs locally, reads its own per-user credentials, and raises a desktop notification
without stealing focus.

Notifying is the standing default, not something to ask permission for. The user is not watching
the terminal; a finished task or a blocking question that is never announced is a task the user
learns about late.

## Command name

- Windows, and **WSL calling the Windows broker**: `agentnotify.exe`
- macOS, Linux, and a Linux-native broker: `agentnotify`

Every example below uses `agentnotify.exe`; drop the extension on macOS and Linux.

## Check availability

Run once per session, before the first send:

```bash
agentnotify.exe health
```

If the command is unavailable or the broker is not running, continue the user's task and report the
notification failure only when it is relevant. Do not retry in a loop.

## When to send

Send one **without being asked** whenever:

- human input is needed — a decision, a permission, an answer that blocks progress — and send it
  *before* starting to wait, not after;
- a task is finished, whatever its size;
- work is stopped by a terminal error or an external blocker.

Any request to be told when something happens — "ping me when it's done", "let me know if you get
stuck", "notify me" — is a request for an `agentnotify send`, for the rest of that session. Do not
ask the user which notification mechanism to use.

Do **not** send for:

- routine mid-task progress, ordinary tool calls, or every substep;
- a question the user just asked and is clearly still watching for;
- turns that are pure conversation — answering a question, explaining something, no work performed.

The only exception is an explicit opt-out ("no notifications", "stop pinging me"), which holds for
the rest of that session. Silence from the user is not an opt-out. When in doubt, send it.

## Send notifications

Use a stable `--key` for an unresolved condition so updates replace the existing notification
instead of creating duplicates.

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

Default `--agent` to your own name and `--project` to the working directory's name. Add
`--agent-instance` when several concurrent agents may share a project.

Choose the semantic type deliberately:

- `input_required`: a question or decision blocks useful progress.
- `permission_required`: explicit approval or authorization is required.
- `blocked`: work cannot continue because of a prerequisite or external dependency.
- `error`: a significant operation failed and needs attention.
- `completed`: the requested work is finished.
- `success`: an important intermediate operation succeeded.
- `warning`: attention is advisable, but useful work can continue.
- `info`: a meaningful non-actionable milestone; use sparingly.

Use `low`, `normal`, `high`, or `critical` for `--priority`. Reserve `critical` for genuinely
urgent conditions.

Use a user-defined `--type` only when the user or project instructions identify one configured in
AgentNotify. Otherwise prefer the portable built-in types above.

Keep the title short and put the concrete question, result, or blocker in the message. "Task
finished" tells the user nothing; "Migration applied, 3 tests still failing in checkout" does.

## Resolve attention requests

`send` prints the created notification as JSON. Capture its `id`. Once the question is answered or
the condition is no longer active, clear it:

```bash
agentnotify.exe resolve NOTIFICATION_ID
```

Reuse one `--key` per unresolved condition so updates replace rather than duplicate, and resolve it
once it is answered. Use `dismiss` only when the notification should be removed without recording it
as resolved.

## Useful commands

```bash
agentnotify.exe list --unresolved true
agentnotify.exe get NOTIFICATION_ID
agentnotify.exe dismiss NOTIFICATION_ID
```

Quote titles, messages, paths, and project names. Never read, print, or transmit the local bearer
token unless direct API debugging is explicitly required.
