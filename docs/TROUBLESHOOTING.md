# Troubleshooting

Practical problem, cause, and fix entries derived from the current implementation. Each fix references a concrete file, path, or message found in the repository.

---

## Broker not running / connection refused

**Symptom**

CLI prints to stderr:

```text
Could not reach AgentNotify: ... 
Is the tray app running?
```

or for `health`:

```text
Could not reach AgentNotify at http://127.0.0.1:47821: ...
Is the app running? Check the tray icon.
```

Source: `src/AgentNotify.Cli/Program.cs`.

**Cause**

The broker host is not running, failed during initialization, or the CLI is contacting the wrong port. On Windows the tray process `AgentNotify.Tray.exe` owns Kestrel; on macOS/Linux the headless `agentnotifyd` process does.

**Fix**

1. On Windows, launch `AgentNotify.Tray.exe` from the Start menu or `%LOCALAPPDATA%\Programs\AgentNotify`. On macOS/Linux, start `agentnotifyd` or its systemd/launchd user service. A second Windows launch signals the existing tray process and exits (`src/AgentNotify.App/App.xaml.cs`).
2. Check `agentnotify health`. When a token is present it probes `GET /v1/health`; otherwise `GET /health` (`src/AgentNotify.Cli/Program.cs`).
3. Confirm the `port` in the broker's per-user `config.json` (default `47821`) matches the CLI's `--port` or `AGENTNOTIFY_PORT` setting.
4. Inspect the broker's local log for initialization failures. On Windows this is `%LOCALAPPDATA%\AgentNotify\logs\agentnotify-YYYYMMDD.log`. A failed startup terminates the incomplete host rather than leaving a partial listener (`docs/ARCHITECTURE.md`).

For an SSH-forwarded WebUI, keep the local and remote ports distinct. If `agentnotifyd` listens
on remote port 47821, forward `-L 127.0.0.1:47822:127.0.0.1:47821` and open
`http://127.0.0.1:47822/ui/` locally. A LAN or tailnet host address in the browser is rejected by
the WebUI host guard; see [WEB_UI.md](WEB_UI.md#troubleshooting).

---

## macOS: the broker exits immediately with no output

Symptom: `agentnotifyd` returns instantly, prints nothing, and `echo $?` shows `137`.
Under launchd, `launchctl list | grep agentnotify` shows a `-9` status and the log has
no new lines at all. `agentnotify health` then reports connection refused.

Cause: the macOS archives are cross-built on a Linux runner, so the adhoc code
signature they carry is not one the macOS kernel accepts. It sends `SIGKILL` before any
of the program runs, which is why nothing is logged anywhere — 137 is 128 + 9.

Fix, for both binaries:

```sh
codesign --force --sign - ~/.local/bin/agentnotify
codesign --force --sign - ~/.local/bin/agentnotifyd
```

`scripts/install.sh` does this automatically; you only need it after a manual install or
after copying the binaries yourself. Re-signing adhoc grants no trust the binary did not
already have — it makes an unsigned local binary runnable, nothing more. Verify with
`codesign -v ~/.local/bin/agentnotifyd`, which should print nothing and exit 0.

`spctl -a` will still say `rejected` afterwards. That is expected and unrelated: it
reports Gatekeeper's opinion of an unsigned binary, and it says the same of a build that
runs perfectly.

## 401 Unauthorized

**Symptom**

```text
Error 401 Unauthorized: unauthorized
```

`send` exits `2`; other commands exit `1`.

**Cause**

The bearer token is missing or wrong. The API rejects any `/v1/*` request without `Authorization: Bearer <token>` where the token matches `authToken` via SHA-256 fixed-time comparison (`src/AgentNotify.Api/Auth/TokenAuth.cs`, `src/AgentNotify.Api/ApiHost.cs`). Token discovery order for the CLI (`src/AgentNotify.Cli/Program.cs`, `src/AgentNotify.Core/Config/ConfigStore.cs`):

1. `--token` flag for this invocation.
2. `AGENTNOTIFY_TOKEN` environment variable (when `applyEnvOverrides: true`).
3. `authToken` in `%LOCALAPPDATA%\AgentNotify\config.json`.

The config file is created lazily: `ConfigStore.EnsureAuthToken` generates 32 random bytes on first run and persists them (`src/AgentNotify.Core/Config/ConfigStore.cs`). Until the tray has run once no token exists. The CLI warning in that case is `No token found. Has AgentNotify run at least once? Look at: {ConfigPath}` and `No auth token found at {ConfigPath}. Has AgentNotify run at least once? Set AGENTNOTIFY_TOKEN or pass --token.` (`src/AgentNotify.Cli/Program.cs`, `:459`).

**Fix**

1. Run the tray app at least once so the token is generated, then confirm:

 ```powershell
 agentnotify.exe token
 ```

 The command reads the file without environment overrides (`src/AgentNotify.Cli/Program.cs`) and prints the raw token.

2. Ensure the same token is used by the caller. Remove a stale `AGENTNOTIFY_TOKEN` from the environment or pass the correct value with `--token`.
3. When debugging with `curl`, prefer:

 ```bash
 TOKEN="$(agentnotify.exe token)"
 curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:47821/v1/health
 ```

 Do not print the token in logs or commit `config.json`.

---

## Agents in WSL: no usage, quota, or skill rows

**Symptom**

Claude Code, Codex, or OpenCode run inside WSL, but the web interface shows no usage for them, no
`WSL · <distribution>` quota card, or no WSL rows on the Agents page.

**Cause**

The Windows broker reads WSL only while a distribution is running; reading a stopped one would boot
it. Discovery is cached for 30 seconds. A quota card appears only when `~/.codex` or `~/.claude`
exists for the distribution's default user (the `[user] default=` in `/etc/wsl.conf`, or else the
registered default user), and usage is read from the agents' default locations
(`CODEX_HOME` or `CLAUDE_CONFIG_DIR` set inside WSL is not visible to Windows).

**Fix**

1. Open a WSL shell, wait half a minute, and refresh the page.
2. For a non-default profile directory, add it on Live quota as
   `\\wsl.localhost\<distribution>\home\<you>\<dir>`.
3. If the Codex card says the app server could not be queried or timed out, open a new WSL shell and
   run `command -v codex`. AgentNotify starts Codex through your login, interactive shell, so it must
   be found there.

---

## `agentnotify` is not found after installation

**Symptom**

PowerShell or WSL reports `command not found` or `agentnotify: command not found`.

**Cause**

The installer adds `%LOCALAPPDATA%\Programs\AgentNotify` to the current user's `PATH` and creates Start menu shortcuts (`docs/INSTALLATION.md`). Existing shells still have the old `PATH`. WSL imports the Windows user `PATH` only when a new shell starts.

**Fix**

1. Open a new PowerShell, Command Prompt, or WSL shell after installation.
2. Verify:

 ```powershell
 Get-Command agentnotify.exe | Format-List
 wsl -- which agentnotify.exe # inside WSL, use the .exe name
 ```

3. The WSL wrapper is the Windows binary itself: call `agentnotify.exe` from WSL (`docs/AGENT_INTEGRATION.md:91`). No conversion of `config.json` paths is needed; the CLI reads `%LOCALAPPDATA%\AgentNotify\config.json` via Windows APIs.

---

## Rate limiting (429)

**Symptom**

`POST /v1/notifications` or `POST /v1/events` returns:

```json
{ "error": "rate limit exceeded" }
```

with `Retry-After: 1` and HTTP `429`.

CLI `send` prints `Error 429 TooManyRequests: rate limit exceeded` and exits `1`.

**Cause**

The API guards `POST` under `/v1/notifications` and the ARC `/v1/events` endpoint with one per-token
fixed-window counter. The limit is `rateLimitPerSecond` in `config.json`, default `30` requests per
second. When the count within the current one-second window reaches the limit, the middleware returns
`429` with `Retry-After: 1`. The limiter is not a security boundary; it roughly bounds abusive
traffic.

**Fix**

- Back off for at least one second before retrying; honor the `Retry-After` header.
- Batch or debounce agent sends rather than looping tightly. Use `key` deduplication to update a single active notification instead of creating many.
- To change the limit, edit `%LOCALAPPDATA%\AgentNotify\config.json` `rateLimitPerSecond` and restart the tray. Values `<= 0` are reset to `30`.

---

## Request body too large (413)

**Symptom**

The broker rejects the request before routing. Depending on the client this surfaces as a Kestrel `413 Payload Too Large` or a closed connection. Direct HTTP clients see no `{ "error": ... }` body because the limit is enforced at the Kestrel layer (`docs/API.md:134`).

**Cause**

`options.Limits.MaxRequestBodySize = config.MaxRequestBodyBytes` (`src/AgentNotify.Api/ApiHost.cs`). Default `65536` bytes (`AgentNotifyConfig.cs`). Metadata is separately bounded to `MaxMetadataBytes` (`8192` bytes serialized, `src/AgentNotify.Core/Services/NotificationValidator.cs`). Validation messages for fields include `title must be at most 200 characters`, `message must be at most 4000 characters`, and `metadata must be at most 8192 bytes`.

**Fix**

- Shorten `title` to at most 200 characters, `message` to at most 4000, and keep `metadata` at or below 8192 serialized bytes.
- To allow larger bodies, increase `%LOCALAPPDATA%\AgentNotify\config.json` `maxRequestBodyBytes` and restart the tray. Values `<= 0` are reset to `65536`.

---

## Notifications stored but no toast shown

**Symptom**

`agentnotify list` and the notification center show the notification as `active`, but no toast window appears. The log may contain `[Toast] paused, not showing {id} {title}` or `[Toast] max visible (5) reached, queued {id}`.

**Cause**

1. **Paused.** `AgentNotifyConfig.PauseNotifications` is `true`. `ToastStackManager.Show` returns early and logs that line (`src/AgentNotify.App/ToastStackManager.cs`). The tray menu "Pause notifications" and Settings → General control this flag.
2. **Visible limit reached.** `MaxVisibleToasts` (default `5`, 1–20) limits concurrent toasts. When reached, new notifications are queued in `_pending` and shown when a visible toast closes (`src/AgentNotify.App/ToastStackManager.cs`). The log records the queue event.

`DoNotDisturb` does not suppress toasts; it only affects sounds (see Sounds not playing).

**Fix**

- Uncheck **Pause notifications** in the tray menu or Settings → General → "Pause desktop toasts (notifications are still stored)" and save.
- Increase **Maximum visible toasts** in Settings → Toasts if stacking is desired, or dismiss active toasts.
- Confirm that the notification status is `active` (`GET /v1/notifications?unresolved=true`). `dismissed` and `resolved` toasts are closed on `Update` (`src/AgentNotify.App/ToastStackManager.cs`).

---

## Sounds not playing

**Symptom**

No audible feedback on a new notification. The log may contain `Configured sound file is missing: ...` or `Could not play sound ...`.

**Cause**

Sound is gated by `NotificationSoundPolicy.ShouldPlay` (`src/AgentNotify.Core/Services/NotificationSoundPolicy.cs`):

```text
if (!soundsEnabled || pauseNotifications) return false;
return !doNotDisturb || (priority == critical && playCriticalSoundsDuringDoNotDisturb);
```

So sound is silent when any of these is true: `soundsEnabled` is `false`, `pauseNotifications` is `true`, or `doNotDisturb` is `true` without the critical-override for a `critical` notification. Additional causes: the configured file does not exist in `%LOCALAPPDATA%\AgentNotify\sounds\`, the file was not a valid `.wav`/`.mp3` import, the import exceeded `10 MB`, the volume is `0`, or `MediaPlayer` failed to open the file.

The managed store resolves files with `ManagedSoundStore.Resolve` (`src/AgentNotify.Core/Services/ManagedSoundStore.cs`). Imported files are copied with a content-addressed safe name and must be `.wav` or `.mp3` (`src/AgentNotify.Core/Services/ManagedSoundStore.cs`). Built-in tones `chime.wav`, `ping.wav`, `alert.wav`, `knock.wav` (`src/AgentNotify.Core/Services/BuiltInTone.cs`) are seeded from embedded resources on startup (`src/AgentNotify.App/NotificationSoundService.cs`).

**Fix**

1. Enable **Enable notification sounds** in Settings → Sounds and set **Volume (0–100)** above `0`.
2. If `Do Not Disturb` is set, enable **Allow critical sounds during Do Not Disturb** for critical alerts, or clear `Do Not Disturb`.
3. Ensure `Pause desktop toasts` is not checked — it suppresses both toasts and sounds.
4. Re-select the global or per-type sound in Settings → Sounds (built-in picker or Choose… for WAV/MP3). The preview button calls `Preview`, which plays `Path.GetFileName(fileName)` through the same resolver (`src/AgentNotify.App/NotificationSoundService.cs`).
5. Check `%LOCALAPPDATA%\AgentNotify\logs\agentnotify-YYYYMMDD.log` for the missing/invalid media message; logging failures themselves never crash the broker (`src/AgentNotify.Core/Logging/FileLogger.cs`).

---

## Outbound channel enabled but nothing delivered

**Symptom**

A notification is created and appears in history, but no outbound message arrives. The outbox counts in diagnostics remain `Pending`/`Retry` or stay `0`.

**Cause**

Outbound delivery requires **both** a provider profile and a matching route to be enabled. `NotificationDeliveryCoordinator.EnqueueAsync` (`src/AgentNotify.Core/Delivery/NotificationDeliveryCoordinator.cs`) does no network I/O: it lists routes, builds the set of enabled provider IDs, then for each route where `enabledProviderIds.Contains(route.ProviderId) && DeliveryRouting.Matches(route, notification)` it enqueues an outbox item and signals the dispatcher. Disabled providers or disabled/mismatched routes produce `0` enqueued rows and the API response still succeeds.

`DeliveryRouting.Matches` (`src/AgentNotify.Core/Delivery/DeliveryRouting.cs`) requires:

- `route.Enabled` is `true` and `notification.Priority >= route.MinimumPriority`.
- When `route.TypeId` is set, `NotificationTypes.Normalize(route.TypeId)` equals `notification.Type` (case-insensitive).
- When `route.Project` or `route.Agent` is set, exact trimmed case-insensitive equality with `notification.Project` / `notification.Agent`.

Routes also carry `IncludeMessage`; when `false` the payload redacts `message` (only `title` leaves the machine). Provider secrets are decrypted at the adapter boundary; missing or unreadable secrets produce `provider_secrets_unreadable` and the outbox item moves to `DeadLetter` after bounded retries. The dispatcher retries up to `RetrySchedule.MaximumAttempts` (`6`) with jittered backoff and a per-attempt timeout (`DeliveryDispatcher.cs`, default `15` seconds), then dead-letters. Attempt diagnostics are sanitized; exception text is never written to logs.

**Fix**

1. In Settings → Channels enable the provider profile **and** the route. New profiles and routes begin disabled.
2. Set the route filters so the notification matches: clear `TypeId`/`Project`/`Agent` when a broad route is intended, and set `MinimumPriority` low enough.
3. Verify the route's **Include notification message off-device** setting when message content is required.
4. Use the provider's **Test** action — it sends a fixed JSON payload through the same adapter with idempotency and bounded size checks (`src/AgentNotify.Core/Delivery/DeliveryDispatcher.cs`).
5. Check provider credentials are re-entered correctly (blank password fields preserve the stored secret; the explicit removal checkbox deletes an optional credential).

---

## Where the local log files live

**Path**

`%LOCALAPPDATA%\AgentNotify\logs\` (`ConfigStore.LogsDir`, `src/AgentNotify.Core/Config/ConfigStore.cs`), surfaced in the tray menu as **Open log folder** (`src/AgentNotify.App/App.xaml.cs`).

**Format**

Daily files `agentnotify-YYYYMMDD.log` written by `FileLogger` (`src/AgentNotify.Core/Logging/FileLogger.cs`):

```text
YYYY-MM-DD HH:mm:ss.fff [INFO|WARN|ERROR] message
```

Rotates by local date, `AutoFlush: true`, thread-safe, and tolerant — write failures are swallowed so logging never crashes the app (`src/AgentNotify.Core/Logging/FileLogger.cs`). `ERROR` entries include exception `ToString()` when an exception is supplied.

Open the current day's file to diagnose port conflicts, token generation, toast overrides, delivery dispatcher recovery (`Recovered N interrupted outbound delivery item(s).`), and sound file resolution.
