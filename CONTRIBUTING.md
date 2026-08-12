# Contributing to AgentNotify

Thanks for helping improve AgentNotify. Bug reports, documentation fixes, tests, design discussion, and focused pull requests are welcome.

## Before opening a change

1. Search existing issues and pull requests to avoid duplicating work.
2. For a substantial feature or protocol change, open an issue first and describe the user problem, proposed behavior, and security implications.
3. Keep the current trust boundary intact: the API must remain loopback-only and authenticated unless a separately reviewed transport is introduced.

## Development setup

AgentNotify is a Windows 11 WPF application built from WSL with the Windows .NET 10 SDK. This checkout uses the SDK at `D:\dev\dotnet\dotnet.exe`:

```bash
cd /home/akash/projects/agent-notify
./scripts/build.sh
./scripts/test.sh
```

Build the distributable installer with:

```bash
./scripts/package.sh
```

The output is `artifacts/AgentNotifySetup.exe`. Generated artifacts, user configuration, tokens, databases, and logs must never be committed.

## Branch workflow

`main` is the stable release line and `dev` is the integration branch. Do not commit directly to either branch for ordinary work. Create a focused branch from `dev` using `feature/`, `fix/`, `docs/`, `test/`, or `chore/`, verify it, and merge it back to `dev` with a non-fast-forward merge. See `AGENTS.md` for the exact local workflow and quality gates.

Commits should use a short imperative Conventional Commit subject, for example `feat: add SMTP test delivery`. Keep migrations, tests, security notes, and user documentation in the same branch as the feature they support.

## Pull request expectations

- Keep changes small enough to review and explain the user-visible behavior.
- Add or update tests for domain, persistence, CLI, or API changes.
- Do not add pixel-fragile UI tests; document manual WPF verification instead.
- Preserve nullable annotations and the existing project boundaries.
- Update `README.md`, API/agent docs, `TODO.md`, and verification notes when behavior changes.
- Run the full build, tests, skill validator, and packaging smoke checks.
- Do not log authentication tokens, notification metadata, or message bodies unnecessarily.
- Encrypt outbound provider credentials with the project secret-protection abstraction; never persist plaintext secrets.

## Style

Use modern, direct C#. Prefer clear types and small methods over new framework layers. Avoid service locators, hidden global state, unnecessary packages, and abstractions without a concrete second use.

## Reporting security issues

Do not publish exploitable security details in a public issue. Contact the maintainers through the repository owner’s private GitHub contact channel and include reproduction steps, impact, and the affected version.

By contributing, you agree that your contribution is licensed under the project’s MIT License.
