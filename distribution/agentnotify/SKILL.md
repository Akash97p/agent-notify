---
name: agentnotify
description: Notify the user through AgentNotify when a coding agent completes, fails, becomes blocked, or needs attention - and ask the user a question and wait for their answer when work cannot continue without one, such as what to name something, which of several approaches to take, or which value is correct. The user answers from the desktop or a paired phone. Use during autonomous coding work on Windows, PowerShell, Command Prompt, WSL, macOS, or Linux to surface actionable state changes without interrupting routine progress.
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

When you need an **answer** rather than an acknowledgement — you cannot continue until the user
decides something — raise an interaction and wait for it instead; see *Ask a question and wait for
the answer* below.

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

## Ask a question and wait for the answer

A notification is one-way: it tells the user something. When you genuinely cannot
continue without their answer — what to name something, which of two approaches to take,
which value is correct — raise an **interaction** instead and wait for it. The user can
answer from the desktop or from a paired phone, and the answer comes back to you.

This is what to use instead of guessing and instead of stopping to ask in the terminal
the user is not watching.

### A free-text answer

```bash
id=$(agentnotify.exe interactions request \
  --kind text \
  --prompt "What should I name the new service?" \
  --text-max 80 \
  --agent "codex" --project "shop" --ttl 900 \
  | python3 -c "import sys,json; print(json.load(sys.stdin)['id'])")

agentnotify.exe interactions wait "$id" --timeout 300
```

`wait` blocks until it is answered, cancelled, or expires, then prints the interaction as
JSON. The answer is `.response.text`.

### A choice between options

```bash
agentnotify.exe interactions request \
  --kind single_choice \
  --prompt "Which database should the new service use?" \
  --choice "pg:PostgreSQL"   --choice-detail "pg:Handles real concurrency" \
  --choice "sqlite:SQLite"   --choice-detail "sqlite:One file, no server" \
  --choice "later:Decide later" \
  --agent "codex" --project "shop" --ttl 900
```

Two to twelve choices. `--choice ID:LABEL` and `--choice-detail ID:DETAIL` are separate
flags — putting the detail after a second colon in `--choice` makes it part of the label.
The answer is `.response.choice_id`, which is always one of the ids you offered.

### Rules that matter

- **Offer only choices you will honour.** The user can only pick what you listed, so a
  missing option means a wrong answer or none at all. Add an escape route — "Decide
  later", "Something else" — whenever the list might not be exhaustive.
- **Ask the question in full.** The prompt is the entire context the user gets on a lock
  screen. "Which one?" is useless; "shop: migrate to Postgres now, or ship on SQLite and
  migrate after the release?" is answerable without opening anything.
- **Set a TTL you can actually wait out** (30–3600s, default 600). When it expires the
  interaction is over; decide for yourself and say plainly what you assumed and why.
- **`wait` caps at 300s per call.** For a longer window, call it again while the status
  is still `pending`.
- **One answer, first valid wins.** Do not raise the same question twice; reuse `--key`
  so a repeat returns the existing pending interaction instead of a second card.
- **Do not raise `--kind permission` yourself.** Host approval prompts are handled
  automatically by the harness (`agentnotify install-harness <agent> --ask`), which hooks
  the host directly. Raising your own would ask the user to approve something twice.

Ask sparingly. An interaction interrupts a person and holds your own work still; a
question you could have answered by reading the repository is worse than no question.

## Write for someone who is not at the keyboard

A notification may be forwarded to the user's phone. That is a setting they control and nothing you
can see from here, so write every message as though it will be read away from the machine: name the
project, the decision, and the options. "Which one?" is useless on a lock screen. "auth-service:
migrate to Postgres now, or ship on SQLite and migrate after the release?" is not.

The forwarding path is end-to-end encrypted and the push that wakes the phone carries no content, so
nothing you send is readable by the server in between. That is not a reason to put secrets in a
message: it will be stored on the user's devices, and a notification is not a private channel.

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

`list --unresolved true` is worth running when picking work back up: it is the set of questions the
user has not yet answered, and re-asking one of them is worse than not asking.

Quote titles, messages, paths, and project names. Never read, print, or transmit the local bearer
token unless direct API debugging is explicitly required.
