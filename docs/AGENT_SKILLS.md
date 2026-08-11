# Agent setup and skills

AgentNotify is agent-agnostic. Any process that can run a command or send an authenticated loopback HTTP request can use it.

## Preferred installation

Use the tray menu’s **Copy agent SKILL.md** or **Download agent SKILL.md…** command. The canonical distributable file is `distribution/agentnotify/SKILL.md`.

For agents that support Agent Skills, create an `agentnotify` skill directory in the agent’s configured skills location and place the file at `agentnotify/SKILL.md`. Restart or reload the agent if its skill catalog is cached. Skill directory conventions differ between products and versions, so use that agent’s documented skill directory rather than assuming a path.

For Codex, a common personal installation is:

```text
~/.codex/skills/agentnotify/SKILL.md
```

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

