# Agent setup and skills

AgentNotify is agent-agnostic. Any process that can run a command or send an authenticated loopback HTTP request can use it.

## Preferred installation

Use the CLI's offline installer. The skill is embedded in every `agentnotify` binary, so this does not
download a package or require Node/npm/Python:

```bash
agentnotify install-skill codex
agentnotify install-skill claude
```

A skill relies on the model remembering to call AgentNotify. For automatic
notification at permission prompts, questions, and session completion,
install the host harness as well (notify-only hooks/plugin, no model
cooperation needed):

```bash
agentnotify install-harness opencode
agentnotify install-harness codex
agentnotify install-harness claude
```

See [HARNESS.md](HARNESS.md).

`agentnotify install skill codex` is accepted as a readable alias. Add `--scope project` to install
under the current repository, `--dry-run` to inspect the destination, `--path DIRECTORY` for a custom
skills root, or `--force` after reviewing a locally modified existing skill.

The Windows tray menu’s **Install agent skill…** command opens Settings on the Install tab, which
does the same thing with a button per agent and reports whether each one already has the file. Its
**Copy agent SKILL.md** and **Download agent SKILL.md…** commands remain available for agents that
are not listed. The canonical distributable file is `distribution/agentnotify/SKILL.md`.

For agents that support Agent Skills, create an `agentnotify` skill directory in the agent’s
configured skills location and place the file at `agentnotify/SKILL.md`.

Current personal defaults are:

```text
Codex:       ~/.agents/skills/agentnotify/SKILL.md
Claude Code: ~/.claude/skills/agentnotify/SKILL.md
OpenCode:    ~/.config/opencode/skill/agentnotify/SKILL.md
```

These paths follow the current
[OpenAI Codex skill documentation](https://learn.chatgpt.com/docs/build-skills#where-codex-loads-local-skills)
and [Claude Code skill documentation](https://code.claude.com/docs/en/slash-commands#where-skills-live).
Codex also receives `agents/openai.yaml`; Claude Code and OpenCode need only `SKILL.md`. All three
discover project skills from their corresponding repository-local directory.

Each entry is a claim about another product's on-disk layout, and a wrong claim writes the file
somewhere that agent never reads — which looks exactly like a successful install. When one of them
moves, correct `AgentSkillCatalog` rather than adding a second list; the CLI and the tray app share
it. Any agent not listed is installed with `--path`, or from the Install tab's **Another agent** row.

### Agents inside WSL

A Windows `agentnotify.exe` resolves `~` to the Windows profile, where an agent running inside WSL
never looks. On Windows the web interface's **Agents** page and the tray's Settings → Install tab
therefore also list every agent for each *running* WSL distribution, labelled `WSL · <distribution>`,
and install into that distribution's home through `\\wsl.localhost\<distribution>`. Start the
distribution (open a WSL shell) if it is missing.

From the command line, name the distribution:

```bash
agentnotify.exe install-skill claude --wsl Ubuntu-20.04
```

When the command runs inside WSL through the repository's `scripts/agentnotify` wrapper, the wrapper
forwards `WSL_DISTRO_NAME` and `install-skill` targets that distribution automatically. `--path`
still overrides everything. Harnesses are not WSL-aware yet: `install-harness` writes Windows paths
into hook commands, so it prints a warning when started from WSL.

## Agents without skill discovery

Copy the following policy into the repository’s agent instructions (`AGENTS.md`, project rules, system prompt, or equivalent):

```text
Use the installed AgentNotify CLI at meaningful attention boundaries. Run
`agentnotify.exe health` once near task start. Send `input_required`,
`permission_required`, or `blocked` with a stable --key before waiting; send
`completed` after long work; avoid routine progress spam. Capture the returned
notification ID and resolve it when the condition is no longer active.

When you need an answer rather than an acknowledgement — you cannot continue
until the user decides something — raise a question instead and wait for it:
`agentnotify.exe interactions request --kind text --prompt "..."` for free text,
or `--kind single_choice --choice ID:LABEL` (2-12 options, `--choice-detail
ID:DETAIL` for the explanatory line) for a choice. Then
`agentnotify.exe interactions wait <id> --timeout 300`, which blocks and prints
the settled interaction; the answer is `.response.text` or `.response.choice_id`.
Offer only choices you will honour, include an escape option when the list may
not be exhaustive, and set a --ttl you can actually wait out. Do not raise
`--kind permission` yourself: host approval prompts are handled by the harness.

Never print or transmit the local bearer token.
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
