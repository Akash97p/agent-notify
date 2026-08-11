# Verification record

Date: 2026-08-12
Environment: Windows 11 host, WSL workspace, Windows .NET SDK 10.0.302 at `/mnt/d/dev/dotnet/dotnet.exe`, x64 publish target.

This record distinguishes automated/process verification from visual checks. No result from the inherited `/mnt/d/dev/AgentNotify` documentation was accepted without rerunning it.

## Verified

### Release build

Command:

```bash
./scripts/build.sh --no-restore
```

Result:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

All seven projects built: Contracts, Core, API, WPF App, CLI, Setup, and Tests.

### Automated tests

Command:

```bash
./scripts/test.sh --no-restore
```

Result:

```text
Passed: 176
Failed: 0
Skipped: 0
```

Coverage includes validation, lifecycle transitions, config recovery/round-trip, custom type normalization/definitions/default priority, managed sound import/sanitization/deduplication and sound profile resolution, SQLite notification and delivery migrations/CRUD/outbox/attempts, atomic concurrent outbox claiming, DPAPI/AES-GCM secret round-trip and redaction, provider input bounds, credential-preserving/merging/removal updates, validated route service updates, idempotent route materialization, dispatcher success/timeout/retry/dead-letter/recovery, sanitized diagnostics and bounded test-send, webhook templating/headers/HMAC/idempotency/status policy/private-address controls, SMTP message projection/authentication/TLS modes/recipient normalization/injection and address policy/status propagation, Telegram encrypted destination projection/plain-text limits/fixed endpoint JSON/status policy, routing/retry policy, authentication, health, create/list/get/patch/dismiss, malformed/null JSON, callback isolation including outbound queue failure, keyed deduplication including concurrent calls, CLI connection failure, bare `--unresolved`, hyphenated and custom notification type parsing, invalid identifier rejection, and version output.

### Delivery security foundation

Windows tests successfully round-trip a `dpapi-user:v1` envelope with current-user scope. Repository tests persist a provider secret, confirm the stored value is encrypted and the public profile omits both plaintext and ciphertext, decrypt only through the delivery-only service, preserve credentials on non-secret edits, reject malformed profile shapes and future schema versions, atomically claim a single item across concurrent workers, record schema migrations v1/v2, and exercise the route/outbox/attempt lifecycle. Network adapters are deliberately not active in this milestone.

### Durable dispatcher

Schema migration v2 adds a unique notification/route key so repeated coordination cannot duplicate delivery. Automated tests prove route filtering, atomic/idempotent enqueue, successful delivery, secret availability only at the adapter boundary, timeout-to-retry, retry exhaustion to dead-letter, interrupted-claim recovery, exact redacted diagnostics, bounded test-send, and isolation of outbox persistence failure from the already-committed local API response. Adapters perform no work without an explicitly enabled provider and matching route.

### Generic HTTPS webhook

Contract tests use an in-memory HTTP handler and verify JSON template expansion, encrypted endpoint/secret-header use, HMAC-SHA-256 calculation, timestamp and idempotency headers, status retry classification, and rejection before send for HTTP, URI credentials/fragments, missing secrets, newline injection, loopback, link-local metadata, multicast, private, benchmarking, and documentation addresses. The production handler disables redirects, cookies, ambient proxies, connection reuse, and response-body reads; its connect callback resolves and validates all destination IPs immediately before opening the socket. No external endpoint was contacted during automated verification.

### Channel management UI

The native Channels tab compiles with provider, route, and diagnostics sub-tabs. Service tests prove secret edits merge without plaintext UI readback, explicit optional-secret removal, route filter normalization, creation-time preservation, and missing-provider rejection. The UI starts new profiles/routes disabled, keeps stored password fields blank, requires explicit private-network and message-content consent, confirms cascade deletion, and calls the bounded dispatcher test path. Visual layout, keyboard flow, screen-reader behavior, and a real external webhook test remain human verification items.

### SMTP email

Contract tests with an injected sender verify strict STARTTLS and TLS-on-connect selection, encrypted username/password projection, recipient normalization/deduplication, invalid-address and CRLF-injection rejection, private/link-local destination rules, subject/body construction, route message redaction, and sanitized transport-result propagation. MailKit 4.17.0 compiles into the self-contained Windows target. No SMTP credentials were assumed and no external mail server was contacted; a real-server interoperability test remains explicitly unverified.

### Telegram Bot

Contract tests verify encrypted bot-token/chat projection, strict token/chat/topic validation, default content protection, silent/topic options, route message redaction, 4096-character surrogate-safe truncation, plain JSON without a markup mode, fixed official endpoint construction, token omission from the JSON body, link-preview suppression, malformed-response handling, and retry/permanent HTTP classification. The production handler pins connections to public DNS results for `api.telegram.org` and disables redirects, cookies, and ambient proxies. No Telegram token or destination was assumed and no external message was sent; real-bot interoperability remains explicitly unverified.

### Skill

`distribution/agentnotify` was initialized using the official skill initializer. `quick_validate.py` reports:

```text
Skill is valid!
```

The installed `SKILL.md` was also checked for its expected `name: agentnotify` metadata.

### Packaging

Command:

```bash
./scripts/package.sh
```

Result:

- `artifacts/AgentNotifySetup.exe`: approximately 186 MB, one self-contained file.
- Embedded tray payload: approximately 85 MB.
- Embedded CLI payload: approximately 38 MB.
- Installer payload/resource validation passed.

Latest locally packaged artifact SHA-256:

```text
59559a4a3c431d605091791e71f8f25649cc25d2c46cd7f7be4428013397bfc6  AgentNotifySetup.exe
```

Regenerate the checksum after any rebuild because it necessarily changes with the binary.

The portable `scripts/package.ps1` implementation and WSL wrapper generated a matching `artifacts/SHA256SUMS.txt`. The static Pages source built locally into `_site`; GitHub-hosted workflows cannot be execution-tested until the owner chooses to push the local repository.

### Custom notification type smoke

The packaged tray and CLI accepted `--type deployment-waiting`, normalized and persisted `deployment_waiting`, returned the same identifier through `get`, displayed a fallback toast, and resolved the row. The Settings definition editor and configured accent/label rendering are compiled and covered by configuration tests but still require a human visual pass.

### Sound verification boundary

Automated tests prove WAV/MP3 extension and size validation, safe content-addressed import, duplicate reuse, managed-path resolution, configuration sanitization, and per-type fallback. WPF media playback compiles and is isolated from the API path, but audible playback/preview remains a human check because no user-selected audio file was assumed or modified during automation.

Windows version-resource inspection confirmed for setup, tray, and CLI:

```text
Product: AgentNotify
Company: Kabani Tech Private Limited
File version: 1.0.0.0
```

Authenticode status is `NotSigned`, as documented.

### Published tray/API/CLI smoke

The actual packaged `AgentNotify.Tray.exe` and `agentnotify.exe` were copied to a temporary directory on the Windows `D:` filesystem and executed. Verified:

- tray/API process started;
- authenticated `health` returned version `1.0.0`, API `v1`, and the running PID;
- `input-required` CLI spelling was accepted and serialized as `input_required`;
- create returned an active notification ID;
- a second create with the same key returned the same ID and updated content;
- `list --unresolved --agent smoke-test` returned the row;
- unauthenticated `/v1/health` returned HTTP `401`;
- malformed JSON returned HTTP `400`;
- resolving the ID removed it from the unresolved query; and
- launching the tray binary again left exactly one `AgentNotify.Tray.exe` process.

The smoke notification was resolved, the process was stopped, and the temporary binaries were removed.

Direct Linux `curl` in this WSL configuration could not reach the Windows loopback listener; Windows `curl.exe` and the Windows CLI succeeded. That is a WSL networking environment detail, not an API bind failure.

### Installer window smoke

The packaged installer was launched from the Windows `D:` filesystem. Process inspection confirmed:

```text
Main window title: Install AgentNotify
Main window handle: nonzero
Responding: true
```

The process was then stopped without installing, and the temporary copy was removed.

### Silent install/uninstall smoke

The packaged installer was run with explicit license acceptance into a temporary `D:` directory:

```text
--silent --accept-license --install-dir <temporary>\AgentNotify --no-startup
```

Verified installed outputs:

- `AgentNotify.Tray.exe`
- `agentnotify.exe`
- generated `GettingStarted.html` with no unreplaced skill placeholder
- valid `SKILL.md`
- `LICENSE.txt`
- `THIRD_PARTY_NOTICES.txt`
- `uninstall.ps1`

The installed CLI returned `agentnotify 1.0.0`. The Windows uninstall registry entry reported AgentNotify, version 1.0.0, and publisher Kabani Tech Private Limited. Running the registered uninstall script removed the known files and uninstall registration. The temporary test directory was then removed.

## Verified by implementation and compilation, not visually inspected

- Tray menu includes Notification Center, Getting Started, Copy/Download skill, Pause, Start with Windows, logs, and Exit.
- The supplied multi-resolution `an.ico` is compiled into app/setup resources and executable icon metadata.
- Toast HWND receives `WS_EX_NOACTIVATE`; XAML also sets `ShowActivated=false`, `Focusable=false`, and `Topmost=true`.
- Toast manager queues overflow and uses foreground-monitor work area with DPI conversion.
- Dashboard status changes feed back into the toast manager.
- Active attention rows are queried and restored at startup.
- Normal setup finish launches the tray app and offline guide when the default checkboxes remain enabled.
- Native Settings window opens from the tray and validates port, retention, stack size, placement, lifetimes, pause/DND, and the initial sound toggle.

These were code-reviewed and built, but the automation did not capture the user’s desktop because doing so could expose unrelated private screen content.

## Remaining human checks

- Inspect toast/dashboard/installer visual layout at 100%, 150%, and 200% scaling.
- Verify toast position with taskbars on each edge and mixed-DPI multi-monitor arrangements.
- Type continuously in an editor while toasts arrive and confirm focus/caret never moves.
- Click every tray/menu/toast/dashboard action in a clean installed profile.
- Complete a normal interactive install, confirm the browser guide opens on Finish, sign out/in to test startup, then uninstall through Windows Settings.
- Verify screen-reader naming, keyboard navigation, contrast, and reduced-motion behavior.
- Repeat install/uninstall under a second clean Windows user.

## Release blocker for public distribution

The app is functional but unsigned. Obtain an Authenticode certificate for Kabani Tech Private Limited, sign the two payload binaries before embedding them, sign the final installer last, timestamp all signatures, and publish a fresh SHA-256 checksum.
