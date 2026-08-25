# Agent setup and skills

AgentNotify is agent-agnostic. Any process that can run a command or send an authenticated loopback HTTP request can use it.

## Preferred installation

Use the CLI's offline installer. The skill is embedded in every `agentnotify` binary, so this does not
download a package or require Node/npm/Python:

```bash
agentnotify install-skill codex
agentnotify install-skill claude
```

`agentnotify install skill codex` is accepted as a readable alias. Add `--scope project` to install
under the current repository, `--dry-run` to inspect the destination, `--path DIRECTORY` for a custom
skills root, or `--force` after reviewing a locally modified existing skill.

The Windows tray menu’s **Copy agent SKILL.md** and **Download agent SKILL.md…** commands remain
available. The canonical distributable file is `distribution/agentnotify/SKILL.md`.

For agents that support Agent Skills, create an `agentnotify` skill directory in the agent’s
configured skills location and place the file at `agentnotify/SKILL.md`.

Current personal defaults are:

```text
Codex:       ~/.agents/skills/agentnotify/SKILL.md
Claude Code: ~/.claude/skills/agentnotify/SKILL.md
```

These paths follow the current
[OpenAI Codex skill documentation](https://learn.chatgpt.com/docs/build-skills#where-codex-loads-local-skills)
and [Claude Code skill documentation](https://code.claude.com/docs/en/slash-commands#where-skills-live).
Codex also receives `agents/openai.yaml`; Claude Code needs only `SKILL.md`. Both products discover
project skills from their corresponding repository-local directory.

When a Windows `agentnotify.exe` is invoked from WSL, its user home is the Windows profile. To install
for a Linux-native Codex/Claude process, run the Linux CLI or pass the WSL skills root explicitly with
`--path`.

## Agents without skill discovery

Copy the following policy into the repository’s agent instructions (`AGENTS.md`, project rules, system prompt, or equivalent):

```text
Use the installed AgentNotify CLI at meaningful attention boundaries. Run
`agentnotify.exe health` once near task start. Send `input_required`,
`permission_required`, or `blocked` with a stable --key before waiting; send
`completed` after long work; avoid routine progress spam. Capture the returned
notification ID and resolve it when the condition is no longer active. Never
print or transmit the local bearer token.
```

Then provide the CLI examples from `docs/AGENT_INTEGRATION.md`.

## Compatibility contract

- The canonical types are `info`, `success`, `warning`, `error`, `input_required`, `permission_required`, `completed`, and `blocked`.
- Both underscore and hyphen spellings are accepted by the CLI.
- Always include `--project`; include `--agent` and, for concurrent runs, `--agent-instance`.
- Reuse a stable `--key` while updating one unresolved condition.
- Treat a missing CLI or stopped broker as non-fatal to the coding task. Report it when relevant and do not retry in a loop.
- Do not read or disclose `%LOCALAPPDATA%\AgentNotify\config.json` or the bearer token.

As custom notification types and delivery routes are introduced, the built-in values above will remain compatible so existing agents do not need immediate skill changes.
