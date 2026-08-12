# Agent Notify Cross-Platform Future Plan

## Vision

Agent Notify becomes the **universal notification layer for AI coding agents**.

The goal is not merely desktop notifications. The goal is to provide a **vendor-neutral, cross-platform notification protocol** that any coding agent can use to notify a human through any channel.

Examples:

- Claude Code
- OpenAI Codex
- Cursor
- Zed
- OpenCode
- Future agent frameworks

A repository should be able to include a `SKILL.md` (or equivalent agent instruction file), and any supported agent should immediately gain the ability to notify the user.

---

# Why this matters

Coding agents are becoming **asynchronous workers**.

The human is often away from the terminal while the agent:

- finishes a task
- needs approval
- needs additional input
- encounters an error
- completes a long-running operation

Today, notification support is fragmented across vendors and editors. Agent Notify aims to provide a **single interface** regardless of the agent or notification channel.

---

# Core proposition

**One command, any agent, any channel.**

```bash
agent-notify send "Build finished"
```

Notification events should be standardized:

- completed
- failed
- needs_input
- needs_approval
- long_running
- progress

---

# Cross-platform strategy

## Current status

- Windows native implementation
- .NET / C#
- Native executable
- No Node.js runtime
- No Python runtime
- No virtual environments
- Minimal dependency surface

This is intentionally a strength.

## Target platforms

### Windows

- Native executable
- MSI / installer
- Winget distribution

### macOS

- Apple Silicon (`osx-arm64`)
- Intel (`osx-x64`)
- Homebrew distribution
- GitHub Releases binaries

### Linux

- x64
- ARM64
- Standalone binaries
- Optional package formats later (.deb, AppImage)

---

# Important realization

Modern **.NET 8+ is cross-platform**.

The project does **not** need a rewrite in Rust or Go.

Most of the codebase should remain shared across all operating systems.

Only the desktop notification backend is platform-specific.

Architecture:

```text
AgentNotify
├── Core
├── CLI
├── NotificationRouter
├── Channels
│   ├── Slack
│   ├── Discord
│   ├── ntfy
│   ├── Bark
│   └── Webhook
└── DesktopNotifier
    ├── WindowsNotifier
    ├── MacNotifier
    └── LinuxNotifier
```

---

# Release pipeline

GitHub Actions should publish native binaries for:

- win-x64
- linux-x64
- linux-arm64
- osx-x64
- osx-arm64

Using:

```bash
dotnet publish -r osx-arm64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=true
```

These binaries can be built **from a Windows machine**.

---

# Distribution

## GitHub Releases

Primary distribution method.

## Homebrew

Create a Homebrew tap repository that downloads binaries from GitHub Releases.

No Apple Developer account is required for Homebrew distribution.

## Winget

Publish Windows package through Winget.

## Linux install script

Simple curl installer:

```bash
curl -fsSL https://... | sh
```

---

# Apple Developer account

Not required initially.

Unsigned macOS binaries are acceptable for developer-focused open-source tools.

Apple Developer Program ($99/year) is only needed later for:

- code signing
- notarization
- Mac App Store
- smoother first-run experience

This can wait until the project has meaningful adoption.

---

# Positioning

Avoid positioning Agent Notify as:

> desktop notifications for Codex

Instead position it as:

> **The notification protocol for AI coding agents**

The value is:

- agent-agnostic
- channel-agnostic
- repo-local
- zero-runtime
- cross-platform

---

# Differentiation

Potential advantages over existing notification utilities:

- Works across multiple coding agents
- Unified notification semantics
- Multiple delivery channels
- Native binaries
- No Node/Python dependency chain
- `SKILL.md` distribution model
- Repository-level integration

---

# Near-term roadmap

## Phase 1

- Windows
- macOS binaries
- Linux binaries
- GitHub Releases automation

## Phase 2

- Homebrew tap
- Winget package
- Linux installer script
- Better release assets

## Phase 3

- Telegram
- Email
- Home Assistant
- Apple Shortcuts
- Custom webhooks
- Plugin architecture

## Phase 4

- Public notification event schema
- SDK for other agent frameworks
- Community integrations
- Agent Notify as ecosystem infrastructure

---

# Portfolio objective

The goal is to build a **high-quality open-source developer tool** that demonstrates:

- cross-platform engineering
- release automation
- native binary distribution
- clean architecture
- developer experience
- documentation
- ecosystem integration

Success is not only measured by startup potential.

A well-executed project with strong GitHub adoption, contributors, releases, and ecosystem integrations is already a significant portfolio asset.

---

# Guiding principle

**Do not rewrite prematurely.**

Ship quickly.

Use the existing .NET codebase.

Expand to macOS and Linux.

Optimize distribution and developer experience.

Let adoption determine whether a deeper architectural evolution is necessary.