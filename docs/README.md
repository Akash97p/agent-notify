# AgentNotify documentation

AgentNotify is a local human-attention broker for coding agents. This directory holds the project
documentation. The current published prerelease is `0.2.0-alpha.3`; the mature `1.0.0`
milestone is intentionally reserved and has not been reached.

## Platform support

The full **native graphical application is Windows-only**: the tray, notification center, custom
toasts, and Settings window are WPF. macOS and Linux run the same CLI, loopback API, SQLite history,
and outbound adapters through `agentnotifyd`; macOS additionally has a native quota-only menu-bar
client. The local web interface configures the broker and shows notifications, questions, usage,
costs, and quotas on all three platforms. [CROSS_PLATFORM.md](CROSS_PLATFORM.md) describes the
remaining full-macOS and Linux native-client work.

## Published documentation website

The documentation is published to GitHub Pages from `site/` by the repository workflow. The site
has a project landing page, an [Insights](https://akash97p.github.io/agent-notify/insights/) page for
the dashboard, a [Model router](https://akash97p.github.io/agent-notify/router/) page, and the guides
below rendered from the Markdown in this directory. There is no separate documentation landing page:
the topbar Documentation link opens
[Install with an agent](https://akash97p.github.io/agent-notify/docs/install-with-agent/), which is
the first thing a new user needs. Documentation `H1` headings come from each file's own title, so a
new guide needs an entry in `site/src/lib/docs.ts` to appear in navigation.

## Start here

| Document | What it covers |
| --- | --- |
| [INSTALL_WITH_AGENT.md](INSTALL_WITH_AGENT.md) | A step-by-step prompt that lets a terminal-capable agent install and verify AgentNotify |
| [INSTALLATION.md](INSTALLATION.md) | Installing the Windows build, what setup writes, and uninstalling |
| [INSTALLATION_UNIX.md](INSTALLATION_UNIX.md) | Installing the broker on macOS/Linux and the macOS quota menu bar |
| [WEB_UI.md](WEB_UI.md) | The browser interface for settings, questions, usage, published-rate cost estimates, named-account live quota, and Insights |
| [RELAY.md](RELAY.md) | Pair a broker and phone with the hosted Relay; send notifications and answer questions |
| [ROUTER.md](ROUTER.md) | The opt-in local provider router: endpoints, upstreams, routes, translation, failover, and its ledger |
| [CLI.md](CLI.md) | Every `agentnotify` command, flag, output shape, and exit code |
| [API.md](API.md) | The loopback `/v1` HTTP API: routes, request/response bodies, and errors |
| [ARC.md](ARC.md) | Attention Request Contract 0.2 lifecycle, schemas, and AgentNotify binding; 0.1 is retained as history |
| [CONFIGURATION.md](CONFIGURATION.md) | The on-disk config file, every setting, and custom notification types |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Symptoms, causes, and fixes for common problems |

## Connecting agents

| Document | What it covers |
| --- | --- |
| [AGENT_INTEGRATION.md](AGENT_INTEGRATION.md) | How an agent should send, key, and resolve notifications |
| [AGENT_SKILLS.md](AGENT_SKILLS.md) | Installing the distributable `SKILL.md` into agents that support skills |
| [HARNESS.md](HARNESS.md) | Auto-notify harnesses for OpenCode, Codex, and Claude Code (hooks/plugin) |
| [INTERACTIONS.md](INTERACTIONS.md) | Waiting questions/permissions: broker model, loopback API, CLI |
| [RELAY_INTERACTIONS.md](RELAY_INTERACTIONS.md) | Relay/mobile wire contract for answering from the phone |
| [BIDIRECTIONAL_AGENT_COMMUNICATION.md](BIDIRECTIONAL_AGENT_COMMUNICATION.md) | Research, architecture, security, and per-agent feasibility for returning human decisions |

## Design and internals

| Document | What it covers |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Project layout, process model, and the boundaries between components |
| [CHANNELS.md](CHANNELS.md) | Every implemented outbound delivery adapter and its security policy |
| [CROSS_PLATFORM.md](CROSS_PLATFORM.md) | The macOS and Linux plan, its phases, and current status |
| [../SECURITY.md](../SECURITY.md) | Trust boundaries, secret handling, provider risks, and vulnerability reporting |
| [../audit_2026-09-19.md](../audit_2026-09-19.md) | The pre-release security audit: assets, data flows, findings, and the controls that hold up |

## Planning and process

| Document | What it covers |
| --- | --- |
| [ROADMAP.md](ROADMAP.md) | Direction and explicitly non-committed items |
| [FEATURE_BACKLOG.md](FEATURE_BACKLOG.md) | The ordered backlog with per-item status |
| [VERIFICATION.md](VERIFICATION.md) | What has actually been verified, and what remains unverified |
| [BUG.md](BUG.md) | Defects found after a capability was called complete, and what caused them |
| [future-scope/](future-scope/README.md) | Shipped usage/quota foundations and remaining durable indexing, broader quota sources, and provider routing |
| [RELEASING.md](RELEASING.md) | Version scheme, tagging, and the release/Pages workflows |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Branching rules, quality gates, and how to propose a change |

## Historical

[`archive/`](archive/) holds the original planning documents (`PROJECT.md`, `REQUIREMENTS.md`,
`STACK.md`, `PLAN.md`). They are kept for provenance and are **not current** — they describe an
earlier scope and use pre-implementation terminology, including "V1" wording that no longer matches
the shipped prerelease. Read `ARCHITECTURE.md` and `VERIFICATION.md` for the real state.
