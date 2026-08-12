# AgentNotify documentation

AgentNotify is a local human-attention broker for coding agents. This directory holds the project
documentation. The current release line is the prerelease `0.0.3-alpha.1`; the mature `1.0.0`
milestone is intentionally reserved and has not been reached.

## Start here

| Document | What it covers |
| --- | --- |
| [INSTALLATION.md](INSTALLATION.md) | Installing the Windows build, what setup writes, and uninstalling |
| [INSTALLATION_UNIX.md](INSTALLATION_UNIX.md) | Installing and running the broker on macOS and Linux |
| [CLI.md](CLI.md) | Every `agentnotify` command, flag, output shape, and exit code |
| [API.md](API.md) | The loopback `/v1` HTTP API: routes, request/response bodies, and errors |
| [CONFIGURATION.md](CONFIGURATION.md) | The on-disk config file, every setting, and custom notification types |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Symptoms, causes, and fixes for common problems |

## Connecting agents

| Document | What it covers |
| --- | --- |
| [AGENT_INTEGRATION.md](AGENT_INTEGRATION.md) | How an agent should send, key, and resolve notifications |
| [AGENT_SKILLS.md](AGENT_SKILLS.md) | Installing the distributable `SKILL.md` into agents that support skills |

## Design and internals

| Document | What it covers |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Project layout, process model, and the boundaries between components |
| [CHANNELS.md](CHANNELS.md) | Every implemented outbound delivery adapter and its security policy |
| [CROSS_PLATFORM.md](CROSS_PLATFORM.md) | The macOS and Linux plan, its phases, and current status |
| [../SECURITY.md](../SECURITY.md) | Trust boundary, secret handling, and reporting a vulnerability |

## Planning and process

| Document | What it covers |
| --- | --- |
| [ROADMAP.md](ROADMAP.md) | Direction and explicitly non-committed items |
| [FEATURE_BACKLOG.md](FEATURE_BACKLOG.md) | The ordered backlog with per-item status |
| [DEVELOPMENT_STATE.md](DEVELOPMENT_STATE.md) | Durable handoff record: decisions, merged milestones, and next actions |
| [VERIFICATION.md](VERIFICATION.md) | What has actually been verified, and what remains unverified |
| [RELEASING.md](RELEASING.md) | Version scheme, tagging, and the release/Pages workflows |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Branching rules, quality gates, and how to propose a change |

## Historical

[`archive/`](archive/) holds the original planning documents (`PROJECT.md`, `REQUIREMENTS.md`,
`STACK.md`, `PLAN.md`). They are kept for provenance and are **not current** — they describe an
earlier scope and use pre-implementation terminology, including "V1" wording that no longer matches
the shipped prerelease. Read `DEVELOPMENT_STATE.md` for the real state.
