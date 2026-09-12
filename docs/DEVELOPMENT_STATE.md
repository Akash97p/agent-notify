# Development state

This is the durable handoff record for long-running AgentNotify development. Update it at every merged milestone and before pausing work.

## Current baseline

- GitHub repository: `git@github.com:Akash97p/agent-notify.git`; local development remains branch-based and no unrequested remote operations are permitted.
- Stable branch: `main`.
- Integration branch: `dev`.
- Imported working baseline: `4be4c1a` (`chore: import working AgentNotify baseline`).
- Recorded protocol/skill integration merge: `3ed89ee` (`merge: fix dev Pages deployment`). Use `git log dev`
  for the current documentation-only descendants.
- Baseline verification on 2026-08-12: Release build succeeded with 0 warnings and 0 errors; 597 tests passed.
- Latest local package verification: `AgentNotifySetup.exe` SHA-256 is `a4c5c68138a11113469b97c74b292d768020d74f10aaa8b84fe1ad93dc522ac4` (`v0.0.4-alpha.2`).
- Manual user verification: tray menu actions work; notification center receives events; custom Windows toasts were seen; skill copy/download works.
- Current product version: `0.1.0-alpha.2`, unsigned prerelease. Windows x64 installer plus portable macOS/Linux archives. The first mature release is reserved for `1.0.0`.

## Decisions that must survive context compaction

1. Use a native WPF Settings window opened from the tray as the primary configuration UI. An optional browser dashboard may be added later, but secrets must not be exposed through it.
2. Keep SQLite as the source for notification history and add provider profiles, routing rules, durable outbox entries, and delivery attempts through explicit schema migrations.
3. Protect provider credentials before persistence with Windows DPAPI using current-user scope. Store only encrypted envelopes in SQLite and redact secret fields from APIs/logs.
4. Keep local desktop notification delivery authoritative. Outbound channels are opt-in secondary deliveries and cannot make notification creation fail.
5. Implement one bounded capability per topic branch, verify it, merge it locally into `dev`, then branch from the updated `dev`.
6. Prefer official APIs. Signal support, if added through `signal-cli`, is experimental and must be clearly labeled as an unofficial user-managed bridge.
7. No commits or edits on `main`; remote pushes and tags require an explicit user request. The owner explicitly authorized the `dev` push and `v0.0.1-alpha.1` tag for the first prerelease.
8. Future macOS and Linux clients are first-class roadmap goals. Avoid putting portable routing/domain logic in WPF classes.
9. Keep ARC and AgentNotify SQLite as the canonical human-attention and interaction record. External
   agent protocols are adapters or projections, not replacements for the local lifecycle.
10. Implement bidirectional coding-agent communication in two modes: direct native adapters for
    existing terminal/editor sessions, and an Agent Client Protocol client for AgentNotify-managed
    subprocess sessions.
11. A skill or ordinary MCP tool cannot intercept a host-native approval. The adapter must own a
    synchronous host hook, plugin/SDK/gateway/RPC request, or ACP server-initiated request and return
    the human answer through that same control surface.
12. Treat remote answers as authorization messages: bind them to the exact installation,
    session/turn, native request and request digest; expire them; reject replay/stale responses; and
    keep Relay acceptance distinct from human response and native-host acceptance.

## Active sequence

1. Completed on `docs/foundation-and-onboarding`: README image/build guide, Getting Started title fix and GitHub link, contributor workflow, durable backlog, cross-agent skill guide.
2. Completed on `chore/github-release-pages`: portable PowerShell packaging, CI/tagged-release workflows, checksum generation, and a locally buildable GitHub Pages documentation site. At that milestone no remote was created; the repository was later published after explicit owner authorization.
3. Completed on `feature/settings-window`: native tray-opened Settings window with validated general, toast placement/lifetime, pause/DND, and initial sound controls. Build/tests passed; human visual inspection remains pending.
4. Completed on `feature/custom-notification-types`: backward-compatible string IDs, custom definition editor, accent/label/default priority/lifetime/enabled settings, legacy normalization, safe fallback, CLI/API/SQLite runtime smoke, and 96 passing tests. Human Settings visual inspection remains pending.
5. Completed on `feature/notification-sounds`: managed WAV/MP3 import, global/per-type selection, preview, volume, pause/DND policy, safe fallback, platform-neutral file store, and 101 passing tests. Human audio/UI verification with a real user-selected file remains pending.
6. Completed on `feature/delivery-foundation`: atomic SQLite v1 migration, provider/route/outbox/attempt repositories, atomic concurrent claiming, DPAPI current-user and test AES-GCM protectors, bounded/validated encrypted profile service, routing/payload policy, bounded retry schedule, and 112 passing tests.
7. Completed on `feature/delivery-dispatcher`: schema v2 outbox idempotency, failure-isolated API persistence hook, single-worker adapter contract, atomic claiming, restart recovery, timeout, bounded jittered retries, dead-letter state, sanitized attempts/logging, exact diagnostics, bounded test-send, and 120 passing tests.
8. Completed on `feature/channel-webhook`: encrypted endpoint URL, HTTPS-only POST, redirect/cookie/proxy suppression, connect-time DNS/IP policy, explicit private-network consent, safe header/secret-header maps, idempotency key, optional HMAC-SHA-256 signature, bounded JSON templates, status retry classification, response-body avoidance, dispatcher registration, and 144 passing tests.
9. Completed on `feature/channel-management-ui`: native webhook provider CRUD, encrypted blank-means-preserve secret patching/removal, enable/private-network consent, test-send, validated route CRUD/filters/off-device message consent, destructive confirmations, exact redacted diagnostics, wider Settings layout, and 147 passing tests. Human visual/accessibility inspection remains pending.
10. Completed on `feature/channel-smtp`: MailKit 4.17 transport, encrypted mandatory authentication, strict STARTTLS/TLS-on-connect, TLS 1.2/1.3 and revocation policy, connect-by-validated-address with hostname certificate validation, private-network consent, one-to-ten recipient allowlist, plain-text route-redacted messages, stable Message-ID, sanitized SMTP retry classes, native Settings fields, third-party notices, and 159 passing tests. Real-server and human UI smoke remain pending.
11. Completed on `feature/channel-telegram`: official Bot API `sendMessage`, DPAPI-encrypted token and chat destination, optional topic ID/silent/content-protection settings, plain-text route-redacted projection, fixed-host HTTPS transport with redirect/cookie/proxy suppression and connect-time public DNS enforcement, link-preview suppression, bounded response parsing, provider-specific retry classes, native Settings fields, and 176 passing tests. Real-bot and human UI smoke remain pending.
12. Completed on `feature/channel-discord`: encrypted official webhook URL, exact Discord host/path/token validation, optional thread ID and display name, confirmed `wait=true` sends, all mentions suppressed, user Markdown escaped, route-redacted 2000-character projection, fixed-host HTTPS transport with connect-time public DNS enforcement, redirect/cookie/proxy/response-body suppression, native Settings fields, and 199 passing tests. Real-webhook and human UI smoke remain pending.
13. Completed on `feature/channel-slack`: DPAPI-encrypted Slack/GovSlack webhook URL, exact official host/path/token validation, optional thread timestamp, markup/link-name suppression, Slack control-sequence encoding, route-redacted 4000-character projection, bounded `ok` acknowledgement verification, fixed-host HTTPS transport with connect-time public DNS enforcement, redirect/cookie/proxy suppression, native Settings fields, and 227 passing tests. Real-webhook and human UI smoke remain pending.
14. Completed on `feature/channel-teams`: encrypted current Power Platform Workflows URL, exact global-cloud host/path/signature validation, rejection of retired connectors, route-redacted bounded Adaptive Card 1.2 projection, Markdown/mention-tag escaping, fixed-host HTTPS with connect-time public DNS enforcement, sanitized retry policy, native Settings fields, and 241 passing tests. Real-workflow, sovereign-cloud, and human UI smoke remain pending.
15. Completed on `feature/channel-zoho-cliq`: encrypted `zapikey` webhook URL, all nine official data-center hosts, channel-name/channel-ID/bot message paths, strict path/query validation, route-redacted Markdown-escaped 5000-character projection, fixed-host HTTPS with connect-time public DNS enforcement, sanitized retry policy, native Settings fields, and 266 passing tests. Real-webhook and human UI smoke remain pending.
16. Completed on `feature/channel-google-chat`: encrypted `key`/`token` webhook URL, exact official host/path/query validation, optional thread key with explicit fallback-or-fail policy, route-redacted and mention-neutralized text, serialized UTF-8 payload enforcement below the 32,000-byte API limit, cancellation-aware one-write-per-second throttling, fixed-host HTTPS with connect-time public DNS enforcement, sanitized retry policy, native Settings fields, and 289 passing tests. Real-webhook and human UI smoke remain pending.
17. Completed on `feature/channel-mattermost`: DPAPI-encrypted webhook URL, HTTPS terminal `/hooks/{token}` validation with subpath/custom-port support, explicit private-network consent without a certificate bypass, connect-time all-address DNS policy, optional silent delivery, route-redacted 16,383-character text, Markdown and Mattermost/Slack mention neutralization, bounded `ok` acknowledgement verification, sanitized retries, native Settings fields, and 311 passing tests. Real-server and human UI smoke remain pending.
18. Completed on `feature/channel-matrix`: HTTPS Client-Server v3 `m.room.message` PUT, DPAPI-encrypted bearer token and room ID, stable SHA-256-derived transaction ID, self-hosted subpath/custom-port and explicit private-network policy, empty `m.mentions` plus legacy mention neutralization, serialized 48 KiB JSON bound, bounded event-ID acknowledgement, sanitized retries, native Settings fields, and 330 passing tests. End-to-end encrypted rooms, real-server smoke, and human UI smoke remain pending.
19. Completed on `feature/channel-ntfy`: hosted/self-hosted HTTPS JSON publishing, DPAPI-encrypted topic and optional `tk_` access token, topic exclusion from URLs, explicit unauthenticated-topic and private-network consent, priority/tag projection, 4096-byte message enforcement, stable sequence-ID retry idempotency, bounded acknowledgement parsing, native Settings secret removal, and 353 passing tests. Real-server and human UI smoke remain pending.
20. Completed on `feature/channel-gotify`: self-hosted HTTPS `/message` JSON publishing, DPAPI-encrypted application token in `X-Gotify-Key`, base-path/custom-port and explicit private-network policy, route-redacted 16,384-character text, fixed priority mapping, forced `text/plain` extras without remote/action fields, bounded message-ID acknowledgement, native Settings fields, and 376 passing tests. Real-server and human UI smoke remain pending; the API has no documented idempotency key.
21. Completed on `feature/channel-pushover`: fixed official HTTPS Messages API, DPAPI-encrypted application/user keys and optional device, configurable built-in/custom sound name, low/normal/high priority mapping, explicit critical-to-emergency opt-in with validated retry/expiry and receipt acknowledgement, 1024/250 Unicode-scalar message/title bounds, provider-mandated permanent 4xx/quota handling, public-DNS connection enforcement, native Settings fields, and 405 passing tests. Real-account/human UI smoke and receipt-status polling remain pending; the API has no request idempotency key.
22. Completed on `feature/channel-pushbullet`: exact official HTTPS Pushes endpoint, DPAPI-encrypted full-account access token and optional target, all-devices/device/channel/email destination modes, explicit 500-per-month free-quota acknowledgement, plain `note` objects without files/links, stable SHA-256-derived `guid` retries, conservative 32 KiB serialized payload bound, bounded push-object acknowledgement, public-DNS connection enforcement, native Settings fields, and 434 passing tests. Real-account/human UI smoke remain pending; provider GUID idempotency is documented as only “mostly” idempotent.
23. Completed on `feature/channel-twilio-sms`: exact official HTTPS Messages endpoint, recommended API Key and local-testing Auth Token modes, DPAPI-encrypted Account/credential/recipient/sender values, E.164 single-recipient and Twilio-number/Messaging-Service sender constraints, explicit paid-send consent, configurable priority floor and 6–36,000-second queue validity, correct GSM-7 extension/UCS-2 one-segment truncation, smart encoding, content discard/address obfuscation, bounded queued/accepted acknowledgement, best-effort cost-safe no-replay handling for caught ambiguity/cancellation, public-DNS connection enforcement, native Settings fields, and 468 passing tests. Process-termination duplicates, real-account/human UI smoke, and a durable daily/account spend budget remain pending.
24. Completed on `feature/whatsapp-cloud`: exact official versioned Graph Messages endpoint (v25.0 default, strictly configurable), DPAPI-encrypted system-user access token/phone-number ID/single E.164 recipient, approved text templates with zero-to-five allowlisted ordered body variables, redaction-safe fixed arity, explicit recipient-opt-in/template-approval/paid-send acknowledgements, critical-by-default priority cost floor, 32 KiB request/response bounds, `wamid` acceptance validation, best-effort no-replay handling for caught ambiguity/cancellation with only definite 429 retries, public-DNS connection enforcement, native Settings fields, and 513 passing tests. Process-termination duplicates, real-business-account/human UI smoke, delivery-status webhooks, and a durable daily/account spend budget remain pending.
25. Completed on `feature/twilio-whatsapp`: exact Account-scoped Twilio Messages endpoint, recommended API Key and local-testing Auth Token modes, DPAPI-encrypted Account/credential/single E.164 recipient/Messaging Service/Content SIDs, approved `HX` Content Templates with zero-to-five allowlisted numbered variables, redaction-safe fixed arity, explicit opt-in/template/text-only/paid acknowledgements, critical-by-default priority floor and queue validity, content/address retention controls, bounded acknowledgement including WhatsApp `read`, best-effort no-replay ambiguity handling with only definite 429 retries, public-DNS enforcement, native Settings fields, and 552 passing tests. Process-termination duplicates, real-account/human UI smoke, delivery-status webhooks, and durable spend budgets remain pending.
26. Completed on `feature/channel-mqtt`: MQTTnet 5.2 MQTT 5 publishing, mandatory TLS 1.2/1.3 with platform hostname/chain/revocation checks, all-address DNS validation followed by pinned-IP connection with the original SNI target, explicit private-network consent, DPAPI-encrypted fixed non-wildcard topic and username/password or Current User mTLS certificate thumbprint, separately acknowledged anonymous TLS, bounded non-retained JSON with message expiry and stable delivery/notification user properties, explicit QoS 0/1/2 behavior and duplicate-risk acknowledgement, native Settings fields, and 597 passing tests. Real-broker/mTLS and human UI smoke remain pending.
27. Paused after MQTT: AWS SNS was researched but deliberately not implemented. No AWS SDK, credential-chain behavior, or partial adapter remains in the tree. Revisit only as a separately approved `feature/channel-aws-sns` branch with explicit static credentials, encrypted region/topic/keys, fixed destination, cost acknowledgement, and provider-specific retry classification.
28. Signal remains experimental because it has no supported official bot/business sending API. Additional channels remain in the priority order maintained in `docs/FEATURE_BACKLOG.md`.
29. Completed on `chore/prerelease-versioning`: aligned product informational version metadata to `0.0.1-alpha.1`, kept numeric Windows assembly/file metadata at `0.0.1.0`, updated API/CLI/installer/registry display values, added prerelease-aware release workflow behavior, and updated release documentation. Merged as `8186aed`, pushed to `origin/dev`, and tagged `v0.0.1-alpha.1`.
30. Published `v0.0.1-alpha.1` as a GitHub prerelease through Actions run `31566620009`. The release contains `AgentNotifySetup.exe`, `SHA256SUMS.txt`, and `SKILL.md`; the mature `v1.0.0` release remains intentionally reserved for a future stable milestone.

31. Completed on `chore/repo-layout`, `feature/site-favicon`, `fix/settings-readability`, and `feature/bundled-tones` (housekeeping and UX pass, no channel work):
    - Repository root reduced to README, LICENSE, CONTRIBUTING, SECURITY, CLAUDE, AGENTS, TODO, THIRD_PARTY_NOTICES and build files. The original planning documents moved to `docs/archive/` with a README marking them historical, the cross-platform plan became `docs/CROSS_PLATFORM.md`, and branding images moved to `assets/branding/`. Project icon paths, the site build script, and the Pages path trigger were updated; the `Resources\an.ico` link name was deliberately preserved so existing `pack://` URIs still resolve.
    - The supplied favicon set moved to `site/favicon/` so the existing build script publishes it. The webmanifest had empty name fields and absolute `/` icon paths that would have broken under the `/agent-notify/` project path; both were corrected and the theme colour matched to the site background. Open Graph and Twitter metadata were added, and the published site is linked from the README.
    - `src/AgentNotify.App/Theme.xaml` fixes settings readability. WPF's default control templates paint system-black text, which was unreadable on the dark settings background, and empty provider/route lists rendered as blank white boxes. The dictionary is merged into `SettingsWindow` only, so toast and notification-center visuals are intentionally unchanged. Empty-state hints were added to the Providers and Routes lists. The redundant close button was removed from the Notification Center header, along with its now-dead handler.
    - Four original CC0 tones (`chime`, `ping`, `alert`, `knock`) were synthesised for the project, embedded in the tray assembly, and seeded idempotently into the managed sound directory on first run. A built-in tone picker was added to the global and per-type sound settings behind a suppression flag that prevents programmatic selection from feeding back into the change handlers. User-supplied WAV/MP3 import is unchanged. The user's own four MP3s are deliberately NOT committed and are ignored via `notification-tone/`, pending redistribution-rights confirmation.
    - Gates: full Release build 0 warnings/0 errors, 601 passing tests, packaging rerun because embedded resources changed (installer SHA-256 `bd7c8ac93cea2369438ec857566dd6d5e5c3d3825228ac1f03c56dc6056c446e`), and the static site build. The distributable skill was not modified.
    - Verification limits are recorded honestly in `docs/VERIFICATION.md`: the theme dictionary and the embedded tones were verified programmatically (dictionary loads through the real pack URI with every template instantiating; each tone extracted through the app's own reflection path with a valid RIFF/WAVE header and byte-identical SHA-256), but **no visual WPF check was performed** and none is claimed.

32. Completed on `docs/accuracy-pass` (documentation only, plus one comment fix):
    - Removed stale "V1" framing from current documentation. `SECURITY.md` now has a "Trust boundary" heading, `CONTRIBUTING.md` says "the current trust boundary", and the `SoundsEnabled` XML comment no longer claims "V1 does not play sounds" (sounds have shipped). The `docs/archive/` documents keep their original V1 wording on purpose; `docs/README.md` states that they are historical.
    - Corrected the automated test count from 597 to 601 in the README badge, README build section, `TODO.md`, and the site stats card, after re-running `./scripts/test.sh`.
    - The GitHub Pages stats card read "8+ built-in and custom types", which was being misread as a provider count. It now reads "8 built-in notification types, plus custom"; the neighbouring card already stated the correct 18 implemented outbound adapters.
    - Fixed two places where documentation contradicted the code. `error` was documented as a sticky toast but `DefaultDurations` gives it 15 seconds; the README table now says 15 seconds and a new paragraph separates "attention type" (which `error` is) from "sticky toast" (which it is not). Rate limiting was documented as covering only `POST /v1/notifications`, but `ApiHost` guards every `POST` under that path; `docs/API.md` and `docs/CONFIGURATION.md` now say so and note that `GET`/`PATCH` are unlimited.
    - `docs/ARCHITECTURE.md` retitled "Future adapter boundary" to "Adding a new outbound adapter", since eighteen adapters exist; the section is now the rule set for adding another.
    - Added four documents: `docs/CLI.md` (every command, flag, output, and exit code), `docs/CONFIGURATION.md` (config file location, every setting with defaults, custom type schema, sound policy, token warning), `docs/TROUBLESHOOTING.md` (symptom/cause/fix entries), and `docs/README.md` as the documentation index. The first three were drafted against the source by the Muse worker and then reviewed, corrected, and stripped of volatile source line-number citations.
    - Gates: Release build 0 warnings/0 errors, 601 passing tests. Packaging was not rerun because no installer payload, embedded resource, or publish setting changed. No WPF visual check was performed and none is claimed.


33. Completed on `feature/cross-platform-core` (Phases 1 and 2 of `docs/CROSS_PLATFORM.md`):
    - `ChannelAdapterFactory` in Core is now the single source of the eighteen adapters; `App.xaml.cs` uses it and its eighteen private adapter fields collapsed into one list. Disposal is unchanged: the sixteen `IDisposable` adapters are still disposed, and SMTP/MQTT still are not because they do not implement it.
    - Secret protection is selected per platform. Windows keeps DPAPI and can never fall back. macOS uses a key in the login keychain via `/usr/bin/security`, Linux uses the Secret Service via `secret-tool`, and either falls back to a `0600` key file with a loud warning. A corrupted key file is a hard error, never a silent regeneration.
    - Unix local state is owner-only: `0700` data directory, `0600` on `config.json`, `agentnotify.db`, and `secret.key`.
    - New `AgentNotify.Desktop` project with `IDesktopNotifier` and `notify-send`, macOS (`terminal-notifier`/`osascript`), and console backends. All helper processes are launched with an argument list, never a shell; the AppleScript is a fixed program taking text through `argv`, and `notify-send` body text is XML-escaped.
    - New `AgentNotify.Host` console project (`agentnotifyd`) that runs the same broker headlessly: config, SQLite, delivery dispatcher, loopback API, desktop notifier, lock-file single instance, and bounded signal-driven shutdown.
    - Three defects were found by running the Linux binary in WSL, not by compiling: local state could be written into the working directory including the bearer token; `SIGTERM` was ignored because the signal registrations were finalized; and shutdown could hang forever. All three are fixed and the first has a regression test. Full detail is in `docs/VERIFICATION.md`.
    - Gates: Release build 0 warnings/0 errors, 619 passing tests, cross-compilation for all five target RIDs, and a real end-to-end run on Linux (health, send, keyed dedup, list, resolve, permissions, single instance, `SIGTERM`). Packaging was not rerun; no installer payload or embedded resource changed.
    - Explicitly unverified: the Linux and macOS *desktop* notifiers have never displayed a notification, the macOS Keychain and `secret-tool` key stores have never run, ARM64 binaries have never executed, and no WPF visual check was performed after the App refactor.


34. Completed on `feature/cross-platform-release` (Phase 3 and part of Phase 4):
    - `scripts/publish-cross.sh` publishes the `agentnotify` CLI and the `agentnotifyd` broker as self-contained single files for win-x64, linux-x64, linux-arm64, osx-x64, and osx-arm64, bundles the licence, third-party notices, and `SKILL.md` beside them, archives each runtime, and writes `SHA256SUMS.txt`.
    - `scripts/install.sh` is a POSIX installer that detects the platform, downloads the matching archive, verifies its SHA-256 against the published checksums, and installs into `~/.local/bin`. It refuses to install anything it cannot verify.
    - `.github/workflows/ci-portable.yml` builds and tests the portable projects on ubuntu-latest and macos-latest, then smoke-tests the broker: health, keyed deduplication through the API, `0600` local state, and clean `SIGTERM` shutdown. This is the only thing that can prove the macOS build works, since no Mac is available here.
    - The release workflow gained a `cross-platform-assets` job that attaches every archive to the tagged release after the Windows installer job succeeds.
    - `docs/INSTALLATION_UNIX.md` documents installation, systemd/launchd units, notification backends, data locations, credential protection, and platform-specific troubleshooting. The README now describes the project as cross-platform and states plainly what macOS and Linux still lack.
    - Verified: a real `linux-x64` archive was produced, extracted, and both binaries inside it ran. Not verified: no GitHub Actions run has ever executed, and `install.sh` has never run end to end because no release contains these archives yet.


35. Completed on `fix/portable-filename-sanitizing`: the first Linux/macOS CI run failed identically on both runners because `Path.GetFileName` is platform-dependent — on Unix a backslash is an ordinary file-name character, so a Windows-style configured sound path survived sanitizing intact and the "bare file name inside the managed directory" invariant did not hold. Added `SafeFileName.Last`, which strips both separators and any volume prefix with plain string operations, and routed `AgentNotifyConfig.NormalizeSoundFile`, `ManagedSoundStore.SeedBuiltIn`, and `ManagedSoundStore.Resolve` through it. 628 tests pass on Windows, and the fix was confirmed on real Linux by letting the Linux broker rewrite a hand-authored Windows-style `config.json`. This is the first defect found by CI rather than by local work.


36. Completed on `chore/release-0.0.2-alpha.1`: bumped the product version to `0.0.2-alpha.1`, added `CHANGELOG.md` as the hand-written source of release descriptions, and added `scripts/release-notes.sh` which extracts a version's section and appends install instructions, checksum guidance, documentation links, and a compare range. The release workflow now passes that file to `gh release create --notes-file` instead of `--generate-notes`, so a release explains what changed rather than only linking to a diff, and it fails when the changelog has no section for the tag. Two workflow defects were fixed before shipping: both jobs uploaded an asset named `SHA256SUMS.txt` so the portable one would have replaced the installer's (now `SHA256SUMS-portable.txt`), and the shallow default checkout had no earlier tags for the compare link (now `fetch-depth: 0`). Gates: build 0/0, 628 tests, packaging rerun (installer SHA-256 `74877688e5bd6e26c0146b8e68b750ebe695e21ae51b01e57dee8eec7a2d208c`).


37. Completed on `feature/about-section` and `chore/docs-and-release-0.0.3`: added an About tab to Settings and an "About AgentNotify" tray entry that opens it; added `docs/BUG.md`; removed machine-specific filesystem paths from all documentation, replacing them with `/path/to/agent-notify` and an environment variable for the external skill validator; and aligned every current-version reference and test count with the release. Version bumped to `0.0.3-alpha.1` (assembly/file metadata `0.0.3.0`). Gates: build 0/0, 644 tests, packaging rerun (installer SHA-256 `828db45c5b02ac0ce73b308d261433186042ac5e83ea9e694a0cb802d1dd9377`). The About tab and tray entry were confirmed working by the owner on Windows; the Telegram crash fix was likewise confirmed with a real bot, including a delivered notification.


38. Completed on the event-profile/skill-installer milestone and `fix/pages-dev-environment`:
    - A first experimental event-ingestion profile was implemented and published. It was later
      superseded by the independently designed Attention Request Contract recorded below.
    - The former `AgentNotify.Contracts` project is being promoted to `AgentNotify.Protocol`; it owns
      native API contracts plus the profile model/schema, while projection remains in the API layer.
    - `POST /v1/events` established the authenticated event projection, immutable retry behavior,
      bounded correlation metadata, and reuse of the validation, SQLite, callbacks, routing, and
      outbox path that ARC now replaces and extends.
    - The CLI skill installer targets the current documented personal/project locations for Codex
      (`.agents/skills`) and Claude Code (`.claude/skills`), embeds its offline payload, refuses to
      overwrite changed files without `--force`, and supports `--dry-run` and custom roots.
    - CI now runs on topic-branch pushes so native Windows/package and Linux/macOS binary gates can
      pass before the required local merge. Pages publishes verified documentation from `dev` as
      well as release-line `main`, allowing prerelease documentation updates without a release merge.
    - Local verification: the full solution cross-build completed with 0 warnings/0 errors using
      `EnableWindowsTargeting`; all 659 portable tests passed; the static site and JSON Schema built;
      and the skill-creator validator reported `Skill is valid!`.
    - Hosted verification on topic commit `d265ac0`: Windows run `32892500440` completed the native
      build, 659 tests, and installer/resource packaging; portable run `32892500455` passed on both
      Ubuntu and macOS, publishing and executing the self-contained CLI/broker and exercising the
      embedded Codex skill installer.
    - The first `dev` Pages run (`32892869704`) built and uploaded the site successfully, then GitHub
      rejected deployment because the existing `github-pages` environment allows only `main`.
      Keep that release-line protection unchanged; `dev` deployments use a distinct
      `github-pages-dev` environment, which `actions/deploy-pages` explicitly supports.
    - Pages run `32893126444` then deployed successfully. Direct HTTPS checks returned `200` and the
      expected content for the event profile, published JSON Schema, and agent-skill installation page.

39. Completed on `feature/arc-contract`:
    - The owner selected **ARC — Attention Request Contract** as the independent open contract for
      agent-to-human attention. ARC is not a renamed compatibility profile.
    - ARC 0.1 defines `request.created`, `request.updated`, and `request.resolved`, separates immutable
      event identity from a stable condition key, and keeps transport, authentication, persistence,
      routing, and presentation outside the base JSON contract.
    - AgentNotify is the first reference implementation. `/v1/events` retains its loopback bearer
      boundary and now maps the full ARC 0.1 lifecycle into local history. Missing updates do not
      create conditions; resolution and unkeyed event replays are idempotent.
    - Structured human responses remain outside ARC 0.1 until AgentNotify can implement the UI and
      callback boundary end to end.
    - Verification: 17 focused ARC API tests pass; the full solution cross-build completes with
      0 warnings and 0 errors; all 666 tests pass; the dependency-free site build and ARC JSON parse
      pass. Packaging was not rerun because no installer payload, embedded resource, publish setting,
      or release automation changed. No WPF visual behavior changed or was checked.

40. Completed on `feature/site-shadcn`:
    - Replaced the hand-written Pages site and Python Markdown renderer with a Next.js 16 static
      export. The source uses TypeScript, Tailwind CSS 4, checked-in shadcn/ui components, Radix
      primitives, and a deliberately monochrome neutral theme; no hosted UI runtime is required.
    - The Markdown files under `docs/` remain authoritative. The build renders 17 documentation
      pages with generated heading IDs and tables of contents, rewrites repository-relative links,
      publishes the ARC schema, and preserves the former `.html` URLs as compatibility aliases.
    - GitHub Actions installs Node 24 from `site/package-lock.json`, type-checks, builds all 21 Next
      routes, and uploads the static `_site` directory. Next telemetry is disabled in the shared
      build script, and the site itself still has no telemetry service.
    - Local verification: the clean Pages script passed; all 39 generated HTML entry points have
      valid internal links/assets; the published schema is byte-identical to its source; npm audit
      reports zero known vulnerabilities; and Chromium renders were inspected at 1440-pixel desktop
      and 390-pixel mobile widths for the landing and ARC pages. The full solution build also passed
      with 0 warnings/0 errors and all 666 tests passed. Packaging was not rerun because no installer
      payload, embedded application resource, publish setting, or release workflow changed.

41. Completed on `feature/monochrome-brand`:
    - Adopted the owner-supplied monochrome robot mark as the project identity. The optimized dark
      mark is the canonical README/site asset; original-resolution dark/light PNG and AVIF variants
      are retained under `assets/branding/` for future native and promotional use.
    - Regenerated the complete web favicon set and both multi-resolution ICO files. The Windows ICO
      contains 16, 24, 32, 48, 64, 128, and 256-pixel images and continues to flow through the tray,
      CLI, setup executable, packed WPF About view, and installer using the existing resource paths.
    - Local verification: the static site built all 21 routes and was visually inspected at 1440x900
      and 390x844 with the new mark; the full solution build passed with 0 warnings/0 errors and all
      666 tests passed. Hosted Windows run `32953223592` then passed the native build, tests, installer,
      and embedded-resource package; portable run `32953223540` passed on Ubuntu and macOS.

42. Completed on `feature/windows-monochrome-ui`:
    - Applied the website's monochrome design language to Settings, channel management, Notification
      Center, desktop toasts, and the standalone installer. Shared settings tokens now use near-black
      surfaces, zinc borders, white primary actions, and neutral text hierarchy; semantic notification
      type/status colors remain only where they communicate state.
    - Settings and Notification Center now use resizable custom Windows chrome with branded title bars,
      minimize/maximize/close controls, clearer page hierarchy, bordered content surfaces, and consistent
      action footers. Existing control names, event handlers, persistence paths, and security behavior are
      unchanged. Toasts use the same surfaces while retaining the narrow configured type accent.
    - The installer now shares the mark, palette, controls, licence card, progress treatment, and publisher
      footer. It remains a per-user, no-elevation installer with the same acceptance and payload behavior.
    - Local verification: the full solution cross-build passed with 0 warnings/0 errors and all 666 tests
      passed. Hosted Windows run `32955922259` passed native build, tests, and installer/resource packaging;
      portable run `32955922411` passed on Ubuntu and macOS. No Windows UI could be launched from WSL,
      so layout, DPI, keyboard, and screen-reader checks remain honestly unverified.


43. Completed on `feature/channel-relay` (AgentNotify Relay, per `RELAY_ADAPTER_SPEC.md` in `agent-notify-relay`):

     - `RelayChannelAdapter` posts an experimental opaque envelope to a Custom self-hosted relay (`https://` or `http://localhost` for dev) via `POST {relay_url}/v1/envelopes`. The adapter reuses the hardened `SocketsHttpHandler` pattern (redirect/cookie/proxy/decompression disabled, 10 s connect timeout, pinned-IP DNS validation via `WebhookChannelAdapter.IsAddressAllowed`) and never logs the URL, installation token, or envelope body.
     - Provider configuration is plain `{"deployment":"custom","relay_url":"https://relay.example.com","sender_name":"My ThinkPad","allowPrivateNetwork":false}`; the `installation_token` (`inst_…`) is DPAPI-encrypted as `installation_token`. Deployment `relay_go` is validated but rejected until the hosted base URL and billing are ready — the Settings UI shows “Relay Go — hosted, paid (coming soon)” disabled, with “Custom — self-hosted” as the selectable default.
     - Envelope shape is `envelope_version:"1"`, `client_event_id:<notificationId>`, `expires_at:<now+24h>`, `recipients:[{device_id,key_id,ciphertext}]`, optional `sender_name`. `ciphertext` is currently `base64url(nonce 24 || ephemPub 32 || plaintext)` (≥72 B) — an experimental opaque wire, not yet claimed as E2E, deliberately bounded to 64 KiB and honest per `docs/SECURITY.md` “Visible metadata”. Per-device fan-out queries `GET {relay_url}/v1/devices` with the same bearer token. A sender with no active device now returns permanent `no_devices_paired` locally without posting; an unknown pinned device returns `relay_device_not_found`.
     - Delivery semantics: `201`/`200` duplicate → success, `408`/`425`/`429`/`5xx`/network → retry, `3xx` → permanent redirect, other `4xx` → permanent failure with stable `relay_*` / `configuration_invalid` codes. Response bodies are bounded to 64 KiB and accepted only with `envelope_id`/`status:accepted|duplicate`. `Idempotency-Key:<outboxId>` and `Authorization: Bearer <installation_token>` are set, `Accept: application/json` and `User-Agent: AgentNotify/1.0` added, and the dispatcher’s outer 15 s timeout plus six-attempt jittered backoff apply.
     - `ChannelAdapterFactory` now lists nineteen adapters in dispatch order; both `AgentNotify.App` and `AgentNotify.Host` consume this single source. `ChannelSettingsPanel.xaml` adds **AgentNotify Relay** to `ProviderKindBox` and a `RelayFields` panel (deployment ComboBox with Relay Go disabled, base-URL and sender-name TextBoxes, `installation_token` PasswordBox with clear checkbox, and the shared `Allow private/loopback destinations` consent). `SaveRelayProviderAsync` mirrors the `SaveWebhookProviderAsync` blank-means-preserve pattern and `LoadRelayConfiguration` restores the fields; the dispatcher’s test-send path is unchanged.
     - Tests: `RelayChannelTests` covers successful envelope with `Idempotency-Key`/`Authorization` and no token in URL, sender-name projection, device-fetch fan-out, unsafe-server rejection (http, userinfo, query, fragment, link-local, private without consent, and `http://localhost` without consent), explicit private subpath/port and `http://localhost` with consent, invalid `inst_…` token rejection, status classification (`408`/`425`/`429`/`5xx` retry vs `401`/`403`/`3xx` permanent), malformed-success retry, `duplicate` acceptance, and `relay_go` rejection. `CrossPlatformTests.AdapterFactory_CreatesEveryImplementedAdapter` now expects 19.
     - Gates: Release build 0 warnings/0 errors, 693 passing tests. Packaging not rerun (no installer payload/embedded resource change). No WPF visual check performed and none claimed; no live relay pairing or real-device decryption was exercised (experimental transport, per `docs/ENVELOPE.md` review checklist).

44. Completed on `feature/relay-connect` (Relay sender device authorization):
     - `RelayPairingClient` in Core implements discovery, sender pairing, bearer-header polling,
       `slow_down`, bounded countdown/expiry, cancellation, five-failure transient-network tolerance,
       terminal rejection/expiry/consumption, and post-approval installation verification. Responses
       are capped at 64 KiB, calls and response reads are time-bounded, and exceptions are sanitized.
     - Pairing and envelope delivery share `RelayHttpTransport`: redirect/cookie/proxy/decompression
       suppression, validated-request marking, connect-time all-address policy, pinned-IP sockets, and
       explicit private-network consent are no longer duplicated. Both verification URLs are required
       to match the configured Relay scheme, host, and port before the desktop can open a browser.
     - The Windows Channels panel replaces default manual credential entry with Connect/Cancel,
       accessible live status, readable short code, browser launch plus no-browser fallback, countdown,
       verified connection identity, Reconnect state, and cancellation on provider-kind/profile/window
       changes. The credential remains memory-only until Save and is never rendered; manual entry and
       removal remain in the collapsed Advanced expander.
     - `agentnotify relay pair` exposes the same flow for Windows/macOS/Linux headless hosts, with
       optional JSON-lines state output and explicit `--allow-private`; `relay status` verifies protected
       saved credentials. Pairing creates disabled providers by default and updates a profile that has
       the same normalized Relay URL.
     - Automated coverage adds 13 pairing tests for request metadata, bearer placement, pending/approved
       flow, terminal states, `slow_down`, transient failure limits, cross-origin rejection, URL policy,
       exception redaction, cancellation, discovery, and verification. Gates: Release build 0 warnings/
       0 errors; all 706 tests passed; packaging completed with installer SHA-256
       `b9ff26b2b800ce58b331a27c57482361c75f134dc88b155e78936de47f1f0b9e`; the distributable skill
       validator passed. Packaging still emits the pre-existing `SettingsWindow.xaml.cs` IL3000 warning
       about `Assembly.Location` under single-file publish.
     - Not verified: no Relay server was available for a live browser approval, credential persistence,
       log scan, test envelope, or real-device flow; no WPF surface was rendered. These manual checks remain
     required and are not inferred from compilation or fake-handler tests.

45. Fixed the post-pairing no-device path on `fix/relay-no-devices` after a live localhost Relay
    exposed the missed §6a requirement in `temp/RELAY_CONNECT_IMPLEMENTATION.md`. The adapter no
    longer synthesizes `relay-placeholder-device`: an empty active-device list returns permanent
    `no_devices_paired`, an unrecognized pinned device returns permanent `relay_device_not_found`,
    and neither state issues `POST /v1/envelopes`. The Channels panel translates both codes into
    actionable text and checks for an empty device list immediately after Connect. The paired
    `installation_id` is parsed from provider configuration and used as the envelope's sender
    identity input instead of the old hard-coded value. Targeted Relay coverage passed 44 tests;
    full build/test/package results are recorded in `docs/VERIFICATION.md`.

46. Completed on `docs/bidirectional-agent-communication`:
    - Added the durable research and implementation plan for returning permissions, choices, text,
      and structured input from desktop/mobile to waiting coding agents. It defines the two-mode
      adapter architecture, normalized interaction state, Relay reverse path, authorization
      boundary, implementation sequence, and open decisions.
    - Evaluated Agent Client Protocol, Agent Communication Protocol/A2A, MCP elicitation, Agent
      Approve AEP, and the separate agenteventprotocol project. Recorded feasibility and preferred
      integration surfaces for Codex, Claude Code, OpenCode, Kilo Code, Hermes, OpenClaw, Gemini CLI,
      Muse Code, Pi, Copilot CLI, Cursor, and Roo Code.
    - Linked the plan from ARC, architecture, agent integration, roadmap, backlog, TODO, docs index,
      and the generated site. Corrected the stale claim that the mobile receiver did not exist and
      recorded the owner's 2026-09-03 live Relay/mobile result without claiming it was repeated in
      this branch. Recorded the Relay MIT/mobile proprietary licence split.
    - Gates: Node 22 site type-check/static export passed with all 23 pages; the full Release build
      passed with 0 warnings and 0 errors; all 720 tests passed; `git diff --check` passed. Packaging
      and distributable-skill validation were not required because their inputs were unchanged.

## Bidirectional communication research (2026-09-03)

- The complete recommendation, protocol assessment, security design, staged implementation plan,
  and feasibility notes for Codex, Claude Code, OpenCode, Kilo Code, Hermes, OpenClaw, Gemini CLI,
  Muse Code, Pi, Copilot CLI, Cursor, and Roo Code are recorded in
  `docs/BIDIRECTIONAL_AGENT_COMMUNICATION.md`.
- Agent Client Protocol is the preferred common managed-session integration because it is a
  bidirectional coding-agent/client JSON-RPC protocol with permission requests. The similarly named
  Agent Communication Protocol is now part of A2A and targets agent-to-agent interoperability.
- Direct adapters remain required because ACP normally owns a subprocess and cannot attach to every
  already-running TUI/editor session. Strong native surfaces exist across the priority agents:
  synchronous hooks, permission plugins/SDKs, approval transports, gateways, and RPC UI requests.
- Agent Approve AEP and the separate agenteventprotocol project contain useful event/control,
  mapping, replay, and acknowledgement patterns. Both are pre-1.0 research inputs, not selected
  AgentNotify core dependencies.
- MCP elicitation is useful for structured input during an MCP call but is not a general replacement
  for a coding host's shell/file/tool permission system.
- The owner reports that `agent-notify-relay` and the Android
  `agent-notify-relay-mobile` flow were tested working end to end on 2026-09-03. This documentation
  branch did not independently repeat the device test.
- Repository licensing is deliberately separate: AgentNotify Relay is MIT; the publicly visible
  mobile repository has a proprietary licence. Public visibility alone does not make the mobile
  source open source.

47. Completed on `feature/install-tab`:
    - The tray menu gained **Install agent skill…**, which opens Settings on a new **Install** tab.
      Each known agent gets a row showing its destination folder, whether the skill is already there
      and whether it matches this build, and one button that installs, updates or reinstalls.
      **Another agent** has no default location on purpose and installs to a folder chosen in a
      picker.
    - The agent catalogue and the installer moved from `AgentNotify.Cli` into
      `AgentNotify.Core/Skills`, because the tray app and `agentnotify install-skill` now write the
      same files to the same folders and two lists would drift. The CLI keeps only its embedded
      payload (`SkillPayload`). `AgentSkillCatalog` is the single place an agent's path is claimed.
    - OpenCode was added as a third known target (`~/.config/opencode/skill`). Every entry is a claim
      about another product's layout: a wrong one writes a file that agent never reads, which looks
      exactly like success. Correct the catalogue rather than adding a parallel list.
    - The installer still refuses to replace a changed skill without `--force`; the GUI asks before
      forcing, naming what is lost. `SkillInstaller.Inspect` treats a partial install as outdated
      rather than up to date.
    - `SKILL.md` gained a section on writing for someone away from their keyboard, because a
      notification may be forwarded to a phone and "Which one?" is useless on a lock screen. It also
      states that the relay cannot read what it forwards and that a notification is still not a
      private channel.
    - `GettingStarted.html` was rewritten against Theme.xaml's palette instead of the old purple
      gradient, and its content corrected: it had been telling people to install the Codex skill to
      `~/.codex/skills`, which the CLI has never used.
    - Gates: Release build 0 warnings/0 errors; 738 tests passed (one earlier run hit a Kestrel port
      bind flake in the API fixture and passed on repeat); packaging produced
      `AgentNotifySetup.exe` SHA-256 `928673f7136fdf834803c577ceeec0a773f91864ac24cbed555885df6ca95ff1`;
      the skill-creator validator reported `Skill is valid!`.

48. Recorded on `docs/mac-hardware-verification` (docs only, no code): first real-Mac
    hardware run, 2026-09-04, owner Intel i5-10400H on macOS 26.6.2 with the extracted
    `osx-x64` archive (`0.0.4-alpha.1`). **The published release archive, not a build from
    `dev`** — nothing merged after that tag was present, so the Relay channel in particular
    has still never run on macOS. Quarantine had to be cleared with `xattr -dr` (no `sudo`
    needed); binaries are adhoc-signed so Gatekeeper still flags fresh downloads. Broker
    reports Keychain secrets and `osascript` notifications; health, keyed dedup,
    `get`/`list`/`resolve`, single-instance refusal, loopback-only listen, `0700`/`0600`
    state, and clean `SIGTERM` all confirmed. The owner visually confirmed an `osascript`
    banner and default-path token discovery. Full detail is in `docs/VERIFICATION.md`.
    Still unverified: `osx-arm64` execution, the `terminal-notifier` path, the launchd unit,
    `install.sh` end to end, the Relay channel on macOS, and any signed/notarized install.

49. Completed on `fix/macos-unix-installer` after running the documented installer on the owner
    Intel Mac:
    - Fixed two unbraced shell variables followed by a Unicode ellipsis; macOS `/bin/sh` otherwise
      parsed the ellipsis as part of the parameter name under `set -u`.
    - The installer now verifies portable archives against `SHA256SUMS-portable.txt`, with a
      `SHA256SUMS.txt` fallback for the original release layout. It no longer mistakes the current
      Windows-installer-only checksum file for the portable checksum list.
    - With no version override, the installer resolves the newest published release through GitHub's
      public releases API. GitHub's `/releases/latest` download path excludes prereleases and returned
      404 while AgentNotify had only prerelease releases.
    - The corrected unpinned installer selected `v0.0.4-alpha.2`, downloaded and verified the
      `osx-x64` archive, and installed both binaries to `~/.local/bin`. A real per-user launchd agent
      then kept the broker running on loopback, and `agentnotify health` returned `ok` for
      `0.0.4-alpha.2`.
    - The bundled offline installer placed exact skill copies in the supported personal locations
      for Codex, Claude Code, and OpenCode. Global `AGENTS.md`/`CLAUDE.md` files were not needed.
    - The initially supplied Relay hostname `an.relay.dev.kabnitech.com` returned DNS `NXDOMAIN`.
      After the owner corrected it to `an.relay.dev.kabanitech.com`, public DNS and HTTPS discovery
      succeeded, the live browser pairing was approved, and the one-time credential was verified and
      saved through the macOS protected secret store without being rendered. `relay status` reports
      the **Hosted relay** provider connected as `Akashs-MacBook-Pro`. The owner then explicitly
      requested an enabled catch-all route: **All notifications to mobile** has minimum priority
      `Low`, no type/project/agent filters, and includes the notification message off-device. A
      Low-priority test was delivered on its first attempt, the Relay returned `201`, and the owner
      confirmed that **Mac Relay test** appeared on the connected mobile app. Broker logs contained
      no `inst_` or `pol_` credential prefixes.
  - Local `/bin/sh` syntax and mocked-release regression checks passed. The repository's full
    scripts could not start on macOS because they intentionally require the Windows .NET 10 SDK
    path used from WSL. Hosted Windows run `34482095178` passed restore, full Release build, tests,
    and installer/resource packaging. Portable run `34482095194` passed on Ubuntu and macOS,
    including the new installer regression check and native CLI/broker smoke test on both hosts.

50. Completed on `feature/harness-opencode-codex-claude` (notify-only auto-notify harnesses):
    - OpenCode: dependency-free `distribution/harness/opencode/agentnotify.js` V1 event plugin
      (`session.idle`/`session.status` → completed, `session.error` → error,
      `permission.asked`/`permission.updated` → permission_required,
      `question` tool → input_required). No imports, named + default export, child-session
      suppression unless `AGENTNOTIFY_INCLUDE_SUBAGENTS=1`, 8 s bounded best-effort sends.
    - Codex/Claude Code: stdlib-only `distribution/harness/shared/agentnotify_hook.py` bridge
      (stdin JSON, argv agent/event, always exits 0, never returns a host decision) plus
      `hooks.example.json` / `settings.example.json` templates.
    - `agentnotify install-harness <opencode|codex|claude>` (alias `install harness`) with
      `--scope user|project`, `--path`, `--force`, `--dry-run`; `HarnessCatalog` owns all three
      layouts; `HarnessInstaller` copies the plugin/script with edit protection and merges
      `hooks.json`/`settings.json` preserving unrelated entries without duplicating on reinstall.
    - Payloads embedded in the CLI (`HarnessPayload`); 25 new tests (installer, catalog, CLI);
      gates on macOS with user-local .NET 10.0.401: full-solution Release build 0 warnings/
      0 errors (`-p:EnableWindowsTargeting=true`), 763 tests passed, `node --check` on the
      plugin, `py_compile` on the hook script, JSON parse on both examples.
    - Explicitly unverified: no real OpenCode/Codex/Claude Code session has loaded these
      harnesses; no desktop notification from a harness has been seen. Owner manual checklist
      is in `docs/HARNESS.md`; results belong in `docs/VERIFICATION.md`. Packaging was not
      rerun locally (Windows-only script); the next hosted Windows run must confirm
      installer/resource packaging with the two new embedded CLI files.

51. Completed on `feature/interaction-broker` (A02 Phase 1: durable interaction model):
    - New `AgentNotify.Protocol` contracts (`InteractionKind/Status/Choice`, create/respond
      requests, DTOs) with explicit `snake_case` wire names, matching the ARC convention.
    - New portable core: `Domain/Interaction`, `SqliteInteractionRepository` (`interactions`
      table in the shared broker database), and `InteractionService` implementing keyed
      idempotency, prompt-change supersede, first-valid-response-wins, response-id replay,
      digest/nonce binding, TTL expiry sweep, cancellation, retention pruning, and in-process
      long-poll waiters.
    - New loopback API (`request`, list, get, `wait`, `respond`, `cancel`) behind the existing
      bearer boundary and create rate limit; 404/400/409 semantics documented in
      `docs/INTERACTIONS.md`. Wired into both the Windows tray broker and `agentnotifyd`.
    - New CLI group `agentnotify interactions` (request/list/get/wait/respond/cancel) with
      `help` text and `docs/CLI.md` reference.
    - Gates: full-solution Release build 0 warnings/0 errors; 787 tests passed (763 + 24 new
      service/API/CLI tests, full suite run twice); `git diff --check` clean. No WPF visual
      behavior changed or checked; packaging deferred to the hosted Windows run.

52. Completed on `feature/harness-gemini-copilot-cursor-muse` (four more notify-only harnesses):
    - Shared `agentnotify_hook.py` now bridges six hosts (added `gemini`, `copilot`, `cursor`,
      `muse` labels and `permission-request`, `after-agent`, `agent-stop`, `error-occurred`
      events). Sources: official Gemini hooks reference, Copilot hooks reference, Cursor hooks
      docs, Muse beta reports (Claude-compatible wire schema).
    - Gemini: `Notification`/`AfterAgent`/`SessionEnd` merged into `~/.gemini` or
      `.gemini/settings.json` (all advisory — a perfect notify-only fit). Documented the
      announced Antigravity CLI succession as a catalog-correction trigger.
    - Copilot: owned `hooks/agentnotify.json` (`notification` fire-and-forget, `agentStop`,
      `sessionEnd`, `errorOccurred`) under `~/.copilot` or `.github`, with bash+powershell
      commands; custom hooks live in sibling files by design.
    - Cursor: `stop`/`sessionEnd` merged into versioned `hooks.json` (`~/.cursor` or `.cursor`);
      documented that user hooks skip cloud agents (use `--scope project` there).
    - Muse: `PermissionRequest`/`Stop` merged into `~/.config/muse/settings.json` with the
      mandatory `schema_version: 1` seeded on fresh files and preserved otherwise; project
      scope writes `.muse/hooks.json` as explicitly unconfirmed (installer + docs say how to
      verify via startup warnings).
    - `install-harness` accepts all seven ids plus `claude-code`/`muse-code`/`gemini-cli`/
      `copilot-cli`/`github-copilot` aliases; 17 new installer/catalog/CLI tests.
    - Gates: build 0/0; 804 tests passed; `py_compile` + example-JSON parses; `git diff --check`
      clean. Real-host display smoke still pending for all harnesses.

53. Completed on `feature/harness-kilo-openclaw-hermes-pi` (last four harnesses):
    - Kilo Code: the OpenCode V1 plugin retargeted at install time (`kilo` agent id, Kilo
      titles, kilo project fallback) with drift assertions — a changed source file fails the
      build of the payload instead of shipping a half-renamed plugin. Verified no leftover
      identity and `node --check` clean.
    - OpenClaw: stdlib-only `agentnotify_openclaw.py watch` daemon polling
      `openclaw approvals pending --json`, opening one broker interaction per approval,
      notifying, waiting, and resolving via `openclaw approvals resolve`. E2E-verified
      against fake binaries (request → notify → wait → resolve). Unsettled approvals stay
      pending; the gateway-operator client (`operator.approvals`) remains the planned upgrade.
    - Hermes: `agentnotify` approval-transport plugin (`plugin.yaml` + `__init__.py`) using
      only confirmed APIs (`register_approval_transport`, `register_hook`, `request.respond`,
      `plugins.enabled`, `security.approval.transport`). Transport waits for the broker answer
      and returns it; errors raise so Hermes denies closed. Installer prints the two
      `config.yaml` consent steps (YAML is never machine-edited).
    - Pi: dependency-free `agentnotify.ts` using only confirmed APIs (`agent_settled`,
      `ui_prompt_start/end`, `ctx.ui.notify`, `ctx.cwd`): settled → completed, blocking
      dialogs → permission notice + broker capture auto-cancelled on close. Typechecked with
      the repo's TypeScript against a stubbed `ExtensionAPI`.
    - `install-harness` covers all eleven hosts; 13 new installer/catalog/CLI tests.
    - Gates: build 0/0; 817 tests passed; `py_compile` on both Python bridges; `git diff
      --check` clean. Real-host smoke still pending across the board.

54. Completed on `feature/harness-ask-mode` (host decision return for Codex + Claude):
    - `agentnotify_hook.py ask-permission`: registers one keyed permission interaction,
      notifies, waits in slices (CLI caps one wait at 300 s), and prints the verified
      `PermissionRequest` decision JSON (`decision.behavior: allow/deny`) for Codex and
      Claude Code. Schemas verified against the official hook references fetched this
      session (Codex `developers.openai.com/codex/hooks`, Claude `code.claude.com/docs/en/hooks`).
    - E2E-verified against a fake CLI (request → notify → wait → deny JSON) and the
      fail-open path (no broker → silent exit 0, host shows its local prompt).
    - `install-harness <codex|claude> --ask` swaps the notify hook for the blocking ask
      hook (600 s Codex / 300 s Claude timeouts, `--timeout` passed through); reinstalling
      without `--ask` restores notify-only. Mode migrations drop the other mode's entries
      so hooks never double-fire; signature matching is now anchored on the script name so
      trailing flags cannot shift it. Other hosts reject `--ask` with a clear error.
    - Documented the tradeoff honestly: while the hook waits, the host's own prompt is
      suppressed — ask mode is for answering from the phone/another machine, notify-only
      stays the default for terminal work.
    - Gates: build 0/0; 821 tests passed (4 new installer/CLI tests); `git diff --check`
      clean. Live host round trip still pending (owner checklist).

55. Completed on `feature/relay-interaction-sync` (relay reverse path, broker side):
    - New `InteractionRelayPublisher`: every new interaction is enqueued per matching
      enabled Relay route with `IncludeMessage` (same body consent as notifications;
      bodyless routes never carry questions). Type/project/agent/priority gates mirror
      route matching with kind→type mapping (permission→permission_required). Outbox ids
      are deterministic per interaction+provider, so republish is idempotent. Payload is
      the sealed `interaction-request` JSON (`contract_version: "1"`, id, kind, prompt,
      choices, digest, nonce, expiry) riding the unchanged envelope flow.
    - Auto-publish via new `ApiCallbacks.InteractionCreated` (failure-isolated like
      `PersistOutbound`), wired in both the Windows tray broker and `agentnotifyd`;
      manual `POST /v1/interactions/{id}/publish` for testing the phone card.
    - New `InteractionResponseSync` + `RelayCursorStore`: `interactions poll-responses`
      pulls stored mobile answers per Relay provider (hardened transport reused for the
      poller), revalidates at the broker (digest, nonce, kind, expiry, first-wins), and
      advances durable per-provider cursors only on fully processed polls. Wrong-
      installation answers are dropped before touching the broker.
    - New `docs/RELAY_INTERACTIONS.md`: the relay/mobile build spec — request payload
      table, the two Relay endpoints to implement (submit/poll with exact JSON + status
      codes), mobile UI minimums, worked example, trust assumptions (TLS+bearer v1, Relay
      sees nonces/answers, E2E answer sealing tracked as follow-up), and explicit v1
      non-goals. This is what the owner's future relay/mobile work builds against.
    - RelayHttpTransport.CreateClient/MarkValidated made public so the CLI poller carries
      the same DNS-pinning policy as delivery.
    - Gates: build 0/0; 835 tests passed (14 new publisher/sync/cursor/API/CLI tests);
      site typecheck + static build with both new pages; `git diff --check` clean.
      Unverified: live Relay round trip (no relay implements the endpoints yet), mobile UI.

## Current documentation/status snapshot

- Implemented outbound adapters: 19 — generic HTTPS webhook, SMTP, Telegram, Discord, Slack, Teams Workflows, Zoho Cliq, Google Chat, Mattermost, Matrix, ntfy, Gotify, Pushover, Pushbullet, Twilio SMS, Meta WhatsApp Cloud, Twilio WhatsApp, MQTT 5, and AgentNotify Relay (self-hosted/Relay Go, experimental opaque transport).
- All outbound adapters are opt-in, disabled until a provider and matching route are enabled, and covered by encrypted secret storage, bounded payloads, provider-specific status policy, and durable outbox dispatch.
- Automated coverage is 835 passing tests. No provider credentials, real paid account, real broker, or external destination is included in the repository or verification run.
- Remaining product work is intentionally concentrated on rules/quiet hours/escalation, agent responses and heartbeat, delivery-status/spend controls, accessibility and multi-DPI human checks, signed releases, ARM64, and future macOS/Linux clients.
- The Android Relay mobile receiver now exists and the owner reports a successful live flow; native
  desktop clients for macOS/Linux remain planned.
- The macOS portable build has now run on real hardware. The first `0.0.4-alpha.1` run proved the
  broker and `osascript` banner; the 2026-09-10 `0.0.4-alpha.2` run additionally proved the corrected
  Unix installer, per-user launchd startup, personal agent-skill installation, and live Relay
  discovery, pairing, routing, encryption, server acceptance, and mobile display. Apple Silicon and
  `terminal-notifier` remain unobserved.
- Work continues on `dev` after cross-platform Phases 1-3 and the protocol/skill-installation milestone.
  Next distribution work is the Homebrew tap and Winget manifest, followed by native clients.

## Next resume action

Owner verification of the harnesses per `docs/HARNESS.md` (ask mode for Codex
first), then Relay server + mobile implementation against
`docs/RELAY_INTERACTIONS.md`. Remaining broker work: local desktop response UI,
dispatcher-integrated response polling, generic ACP bridge, MCP elicitation.

Two items are waiting on the repository owner rather than on code:

- Perform the human WPF checks listed at the end of `docs/VERIFICATION.md` for the settings theme and the built-in tones. Nothing visual has been confirmed.
- Decide the redistribution rights for the four personal MP3s in the ignored `notification-tone/` folder. If they are clear, add them under `assets/tones/`, extend `BuiltInTones.All`, and record their provenance in `THIRD_PARTY_NOTICES.md`.

## Release v0.1.0-alpha.1 (2026-09-12)

Bidirectional interactions (broker + CLI/API/tray answers, ask mode for Codex and Claude
Code, 13 harnesses, Relay answer sync on the desktop side) released as `v0.1.0-alpha.1`,
tagged on `main` after promoting `dev`. No .NET toolchain on this machine, so no local
build/test/package run was possible here; the hosted release workflow builds, tests, and
packages independently and fails rather than publishing. Windows installer verification
of the hosted assets is with the owner.

## Pages promotion fix (2026-09-12)

The `main` Pages run for `v0.1.0-alpha.1` was cancelled, not build-failed: pushing `dev`
seconds later replaced it because both branches shared `concurrency.group: pages` with
`cancel-in-progress: true`. Pages now queues same-site branch deployments rather than cancelling
the release-line run. The build script also resolves and enters the physical repository path before
starting Node, preventing case-variant paths on macOS from loading duplicate framework modules.
The replacement `dev` workflow had already built and deployed the same Git tree successfully.
Next 16.3.x still has an upstream cold-prerender AsyncLocalStorage race locally; no framework patch,
downgrade, or unbounded retry was retained.

## Continuous answer polling and response hardening (2026-09-12)

`fix/interaction-answer-delivery` completed the desktop half of the phone-answer loop:

- A new `InteractionResponsePoller` runs on a worker task in both the Windows tray
  (`App.xaml.cs`) and headless `agentnotifyd` (`BrokerRuntime`). It polls every enabled
  Relay profile every 5 s with bounded jittered backoff (cap 2 min), 15 s per request,
  and a cursor that only advances after the whole fetched batch was processed. Profiles
  without a saved `installation_id` are skipped rather than polled with an empty target.
  Shutdown is bounded and cancels in-flight work.
- `InteractionResponseSync` now validates each stored answer (contract version, ids,
  64-hex digest, nonce, exact target installation, exactly one bounded choice/text)
  before the local broker sees it; local broker 400/404/409 are terminal, transport
  errors and 5xx/429/401 are retryable and block cursor advancement. Relay response
  bodies are capped at 64 KiB and cursors at 2048 chars.
- `InteractionService.RespondAsync` requires a matching nonce whenever the stored
  interaction has one, and checks digest/nonce before replaying a settled outcome, so a
  response id can no longer be replayed without proving it belongs to the request.
- The `RelayPollTarget.FromProfileAsync` factory reads the same encrypted relay profile
  used for delivery; the CLI `interactions poll-responses` skips profiles without an
  installation id with a clear message instead of silently polling.
- Verification on macOS with `$HOME/.dotnet/dotnet` 10.0.401 and
  `-p:EnableWindowsTargeting=true`: full solution Release build 0 warnings / 0 errors;
  838 tests passed (835 prior plus 3 new). No WPF surface was launched; no live Relay
  round trip was run on this machine. Windows installer and real-device checks remain
  with the owner/hosted CI.

### Follow-up: Relay installation identity is now required (`fix/relay-installation-identity`)

The Relay hardening rejects any envelope whose `sender_id` is not the authenticated
installation, so the desktop adapter's old `"local-installation"` fallback became an
unaddressable envelope. The adapter now fails preparation permanently with
`relay_installation_identity_missing`, the Channels panel explains that the profile
must be reconnected, and the RelayChannelTests fixtures carry an installation id.
Gates: Release cross-build 0 warnings / 0 errors, 839 tests passed.

## Interaction wait lifecycle and ask fallback (2026-09-12)

`fix/interaction-wait-lifecycle` closed three defects found while reviewing the
bidirectional loop end to end. The Relay/mobile contract and the cursor, envelope and
`sender_id` -> `installation_id` chain were re-read against the implementations and are
sound; these were the gaps.

- `InteractionService.WaitAsync` reported `pending` for a question whose `expires_at`
  had already passed. Nothing signals a waiter when a deadline merely lapses, and the
  timeout path read the row straight from the repository without sweeping, unlike every
  other read path. It now settles the row through the gate (`SettleIfDueAsync`) before
  answering — gated, because an unguarded read-modify-write there could overwrite an
  answer `RespondAsync` committed in the same instant. The ask hook was spending a whole
  extra 120 s wait slice on questions that were already dead.
- `_waiters` retained one empty `List<TaskCompletionSource>` per interaction ever waited
  on. Buckets are now rented and returned (`RentWaiterList`/`ReturnWaiterList`): the
  bucket is dropped when its last waiter leaves, and the rent loop re-checks identity
  under the bucket lock so a waiter can never join an orphan that was removed between
  `GetOrAdd` and the lock. `WaiterBucketCount` is the internal test seam.
- The `ask-permission` hook left the interaction pending on every fallback path
  (broker timeout, malformed wait result, unexpected choice). The host then prompted
  locally and decided, while the phone kept a live card whose tap applied to nothing —
  and the phone would still be told "Relay recorded your answer". The hook now cancels
  the interaction in a `finally` on any path it does not use the answer from, and
  resolves the companion `permission_required` notification either way (it was created
  fire-and-forget and never resolved, so every ask left a stale "waiting for approval"
  entry). `run_cli_quiet` is the new best-effort CLI helper for both.

Verification on macOS with `$HOME/.dotnet/dotnet` 10.0.401 and
`-p:EnableWindowsTargeting=true`: full solution Release build 0 warnings / 0 errors;
842 tests passed (839 prior plus 3 new). Each new test was confirmed to fail against the
pre-fix code before being kept. `python3 -m py_compile` passes on the hook. No WPF
surface was launched and no live Relay round trip was run on this machine; the hook's
cancel/resolve path was not executed against a running broker.
