# AgentNotify — implementation plan

This plan records the executed phases. `TODO.md` is the final feature/check state.

## 1. Inspect and preserve requirements

- [x] Read and scrutinize `PROJECT.md` in full.
- [x] Validate the Windows .NET 10 SDK at `/mnt/d/dev/dotnet/dotnet.exe`.
- [x] Inspect the supplied multi-resolution `an.ico`.
- [x] Audit the prior `/mnt/d/dev/AgentNotify` implementation as untrusted reusable input.
- [x] Create requirements, stack, plan, and TODO documents before new implementation.

## 2. Re-establish the broker foundation

- [x] Import the useful Contracts/Core/API/App/CLI/test project boundaries.
- [x] Build the solution with the Windows SDK from the WSL-hosted workspace.
- [x] Verify loopback Kestrel, local token, SQLite, validation, lifecycle, and logging.
- [x] Remove host-global API callbacks and isolate UI callback failures.
- [x] prevent WSL UNC content-root probing from hanging API tests.

## 3. Brutal correctness review

- [x] Fix auto-expiring toasts opening the dashboard.
- [x] Fix toast title/message grid overlap and button/body click bubbling.
- [x] Mark auto-expired notifications dismissed so Recent/attention state is truthful.
- [x] Restore persisted attention-required toasts after restart.
- [x] Queue overflow rather than discarding the oldest toast visually.
- [x] Place toasts on the foreground monitor and use DPI-aware coordinates.
- [x] Add `WS_EX_NOACTIVATE` and preserve `ShowActivated=false`.
- [x] Fix dashboard status actions so visible toasts close.
- [x] Fix dashboard attention/recent classification and exit behavior.
- [x] Fix second-instance event opening, startup races, and shutdown races.
- [x] Fix concurrent keyed deduplication.
- [x] Fix CLI `--unresolved`, `--version`, hyphenated types, invalid enum handling, connection errors, and timeouts.
- [x] Replace the generated icon with the supplied branded icon.

## 4. Agent distribution

- [x] Create `distribution/agentnotify` with the official skill initializer.
- [x] Write concise notification discipline, lifecycle, and CLI instructions.
- [x] Generate matching `agents/openai.yaml` metadata.
- [x] Validate the skill with `quick_validate.py`.
- [x] Embed the skill in the tray binary.
- [x] Add tray Copy and Save actions.

## 5. Installer and onboarding

- [x] Add publisher/author/product/version/license assembly metadata.
- [x] Add the WPF setup project and explicit MIT/no-warranty acknowledgement.
- [x] Publish self-contained x64 single-file tray and CLI binaries.
- [x] Avoid the Windows case-insensitive filename collision with `AgentNotify.Tray.exe`.
- [x] Add per-user install, PATH, startup, Start menu, desktop option, and uninstall registration.
- [x] Create the offline responsive getting-started page with Copy/Download `SKILL.md` actions.
- [x] Launch the tray app and guide at setup finish.
- [x] Produce one `artifacts/AgentNotifySetup.exe` distributable.

## 6. Tests and verification

- [x] Add regression tests for null payloads, hyphenated types, callback isolation, CLI bugs, and concurrent deduplication.
- [x] Build all seven projects with zero warnings/errors.
- [x] Pass all 87 automated tests.
- [x] Validate the packaged skill and installer payload presence.
- [ ] Complete a human visual pass of installer/tray/toasts on multiple DPI/monitor layouts.
- [ ] Authenticode-sign public release binaries with a Kabani Tech Private Limited certificate.

## 7. Documentation and open source release

- [x] Write detailed README, architecture, installation, API, agent integration, verification, and roadmap docs.
- [x] Add MIT `LICENSE`, `CONTRIBUTING.md`, and `SECURITY.md`.
- [x] Document email, WhatsApp, collaboration chat, SMS, push, webhook, and response-channel plans.
- [x] Remove inherited claims that were not independently verified.
- [x] Inspect final Git status and generated artifact metadata.
