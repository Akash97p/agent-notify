# Configuration

AgentNotify is configured through a single per-user JSON file. The file is created on first launch with safe defaults and a generated bearer token. Unknown or malformed fields fall back to defaults; partial files are tolerated and rewritten with missing keys on startup.

Source: `src/AgentNotify.Core/Config/AgentNotifyConfig.cs`, `src/AgentNotify.Core/Config/ConfigStore.cs`, `src/AgentNotify.Core/Config/NotificationTypeDefinition.cs`.

---

## File location and format

Default data directory (`src/AgentNotify.Core/Config/ConfigStore.cs`):

| Platform | Directory |
| --- | --- |
| Windows | `%LOCALAPPDATA%\AgentNotify` |
| macOS/Linux | `$XDG_DATA_HOME/AgentNotify`, or `~/.local/share/AgentNotify` when `XDG_DATA_HOME` is unset |

Every platform derives the same files from that directory:

| Relative path | Purpose |
| --- | --- |
| `config.json` | Typed configuration and bearer token |
| `agentnotify.db` | SQLite notification, interaction, delivery, API-account, and router state |
| `logs/` | Daily log files |
| `sounds/` | Managed WAV/MP3 files (used by the Windows desktop application) |
| `secret.key` | Unix-only fallback encryption key when no supported keyring is available |

`agentnotifyd --config-dir <path>` selects a different absolute data directory for a headless broker.
On Unix, the directory and sensitive files are created with owner-only permissions. See
[INSTALLATION_UNIX.md](INSTALLATION_UNIX.md) and [../SECURITY.md](../SECURITY.md) for keyring and
fallback-key details.

Format: JSON serialized with `AgentNotify.Protocol.Json.Options` (`System.Text.Json` with `JsonSerializerDefaults.Web`, `PropertyNameCaseInsensitive: true`, `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)`). JSON property names are therefore camelCase (`port`, `authToken`, `toastLocation`) while enum strings on the wire are snake_case.

Loading behavior:

- Missing file returns defaults.
- Malformed JSON is caught as `JsonException`; defaults are kept and a subsequent `Save` rewrites a healthy file.
- After load `AgentNotifyConfig.ApplyDefaults()` normalizes and fills missing keys.
- Environment overrides `AGENTNOTIFY_PORT` and `AGENTNOTIFY_TOKEN` are applied when `ConfigStore(applyEnvOverrides: true)` (the CLI default). `HasTokenFile` and some app startup paths use `applyEnvOverrides: false`.

Saving is atomic: write to a temporary file then `File.Move(overwrite: true)`.

Warning: `config.json` contains the local bearer token (`authToken`). This value authenticates any
local process that can reach `127.0.0.1:47821`. Do not copy, commit, email, or otherwise share the
file. Delete the platform data directory only when intentionally removing the token, configuration,
history, provider state, and logs.

---

## Settings reference

All settings are properties of `AgentNotifyConfig`. The table lists the JSON name (as written), the .NET type, the default, and the effect. Validation and clamping described here are performed by `ApplyDefaults`.

| JSON property | Type | Default | Description |
|---------------|------|---------|-------------|
| `port` | `int` | `47821` | Loopback Kestrel port. When `<= 0` or `> 65535` reset to `47821`. Env `AGENTNOTIFY_PORT` overrides. Editable in Settings → General (1–65535). Changing it requires restarting AgentNotify. |
| `apiVersion` | `string` | `"v1"` | Read-only API version returned in health responses. |
| `authToken` | `string` | `""` then generated | Bearer token for all `/v1/*` routes. Generated on first run as 32 random bytes rendered as base64url (`RandomNumberGenerator.GetBytes(32)`). Env `AGENTNOTIFY_TOKEN` overrides at runtime but is not persisted. `ConfigStore.EnsureAuthToken` creates and saves it when empty. |
| `toastLocation` | `string` | `"BottomRight"` | Toast corner: `BottomRight` or `TopRight` (case-insensitive check). Empty/whitespace reset to `BottomRight`. Editable in Settings → Toasts. |
| `maxVisibleToasts` | `int` | `5` | Maximum simultaneously visible toasts. When `<= 0` reset to `5`. Editable in Settings → Toasts (1–20). Overflow notifications are queued and shown as others close. |
| `historyRetentionDays` | `int` | `30` | Retention for non-active notifications. When `< 0` reset to `30`. Pruning uses `max(1, value)` days and deletes `status != active && updated_at < cutoff`. Editable in Settings → General (0–3650). |
| `launchAtStartup` | `bool` | `false` | Launch at Windows logon. Mirrored to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AgentNotify` and toggled from the tray menu. Not directly edited in the Settings window. |
| `pauseNotifications` | `bool` | `false` | When `true`, notifications are stored but no toasts are shown (`ToastStackManager` returns early; sound policy also returns false). Editable in Settings → General and the tray menu "Pause notifications". |
| `doNotDisturb` | `bool` | `false` | Reserved scheduling stub. Currently only affects sound policy. Editable in Settings → General. |
| `soundsEnabled` | `bool` | `false` | Master switch for notification sounds. Editable in Settings → Sounds. |
| `soundVolume` | `double` | `0.8` | Playback volume `0.0–1.0`, clamped with `Math.Clamp`. Edited as `0–100` in Settings → Sounds (`value/100`). |
| `defaultSoundFile` | `string?` | `null` | Global sound filename. Normalized with `SafeFileName.Last` so both Windows and POSIX separators are handled, and accepted only when extension is `.wav` or `.mp3` (case-insensitive); otherwise `null`. Stored as a bare filename inside the managed sounds directory. Editable in Settings → Sounds (choose/preview/clear). |
| `typeSoundFiles` | `object` | `{}` | Per-type override map: type ID → filename. Keys are normalized with `NotificationTypes.Normalize`; values normalized as for `defaultSoundFile`. Invalid entries are dropped; duplicate normalized keys keep the last value. Case-insensitive. Editable in Settings → Sounds per type. |
| `playCriticalSoundsDuringDoNotDisturb` | `bool` | `false` | When `true`, critical-priority sounds play even when `doNotDisturb` is `true`. Editable in Settings → Sounds. See Sound policy. |
| `maxRequestBodyBytes` | `long` | `65536` (`64*1024`) | Kestrel `MaxRequestBodySize`. When `<= 0` reset to `65536`. Not editable in Settings. Bodies larger than this are rejected before routing. |
| `rateLimitPerSecond` | `int` | `30` | Simple fixed-window limit applied to every `POST` under `/v1/notifications` and `/v1/interactions`, and to `POST /v1/events` (per token, 1-second window); `GET` and `PATCH` are not limited. When `<= 0` reset to `30`. Not editable in Settings. Env does not override. |
| `maxMetadataBytes` | `int` | `8192` | Serialized metadata map size cap. When `<= 0` reset to `8192`. Not editable in Settings. Validation uses `JsonSerializer.SerializeToUtf8Bytes(metadata, Json.Options)`. |
| `toastDurations` | `object` | see below | Map of type ID → auto-dismiss seconds. `0` means sticky until dismissed/resolved. Backfilled from defaults and normalized. Editable in Settings → Toasts per built-in type (0–86400). |
| `customNotificationTypes` | `array` | `[]` | User-defined type definitions. See Custom types. Editable in Settings → Custom types. |
| `quotaAccounts` | `array` | `[]` | Additional named Codex/Claude Code profile directories shown in Live quota. Each entry has an AgentNotify-generated `id`, `provider`, `label`, and absolute `directory` under the user's home folder; no credentials are stored. Their session logs are also read by Usage. Add, edit, and remove through Live quota, up to 16 extras. |
| `defaultQuotaAccountLabels` | `object` | `{}` | Optional owner-chosen display labels for the built-in `codex` and `claude_code` profiles and discovered WSL and secondary profiles (`codex:wsl:<distribution>`, `codex:home:<name>`, `codex:wsl:<distribution>:<name>`, and the `claude_code` equivalents). Values are trimmed to 1–60 printable characters; unknown keys and invalid labels are removed. Editable through Live quota → Manage accounts. |
| `openCodeGoRenewalDay` | `int?` | `null` | Day of the month (1–31) the OpenCode Go plan renews. When set, the OpenCode Go monthly estimate counts the current billing cycle instead of the last 30 days. Other values are dropped. Editable through Live quota → OpenCode Go. |
| `routerEnabled` | `bool` | `false` | Whether the provider router accepts requests on `/router/v1`. While `false` every router request is refused with `404 router_disabled` and no provider is ever contacted. Editable through the web interface → Router. See [ROUTER.md](ROUTER.md). |
| `routerKey` | `string` | `""` | The key an agent authenticates to the router with, as `Authorization: Bearer` or `x-api-key`. Generated when the router is first enabled, replaceable from the Router page, and printed by `agentnotify router key`. It is not the `/v1` bearer token and grants no access to notifications or configuration. |
| `routerMaxRequestBodyBytes` | `long` | `33554432` (32 MiB) | Per-request body limit for `/router/v1` routes only, clamped to 1 MiB–128 MiB. Agent requests carry whole conversations, so the 64 KiB `maxRequestBodyBytes` limit does not apply to them. Not editable in the interface. |
| `routerLedgerRetentionDays` | `int` | `30` | How long router request and attempt rows are kept, clamped to 1–365. Older rows are pruned when the broker starts and once a day. Not editable in the interface. |
| `removedQuotaAccounts` | `array` | `[]` | IDs of built-in (`codex:default`, `claude_code:default`) or discovered accounts (WSL defaults and native/WSL secondary profiles) removed from Live quota. Other values and duplicates are dropped. Removed and restored through Live quota → Manage accounts. |
| `macMenuBar` | `object` | enabled, 5 minutes, all accounts | macOS quota status settings: `enabled`; `refreshMinutes` clamped to 5–60; and up to 32 Codex/Claude account IDs used for the headline. Empty `accountIds` means all monitored accounts. Edited on WebUI → Live quota; ignored by Windows/Linux hosts. |

### Toast duration defaults

`AgentNotifyConfig.DefaultDurations()`:

| Type ID | Seconds |
|---------|--------:|
| `completed` | `5` |
| `success` | `5` |
| `info` | `7` |
| `warning` | `12` |
| `error` | `15` |
| `input_required` | `0` |
| `permission_required` | `0` |
| `blocked` | `0` |

During `ApplyDefaults`:

- An empty or missing map is replaced with the defaults.
- Existing keys are normalized; if the normalized form differs and is absent, the value is copied.
- Every default key is added when absent (`TryAdd`).

Effective duration is resolved by `ToastDurationSeconds(type)`:

1. If a custom definition is enabled and `Id` equals the normalized type, its `durationSeconds`.
2. Else if `toastDurations` contains the normalized type, that value.
3. Else `7`.

### Sound policy

`NotificationSoundPolicy.ShouldPlay` (`src/AgentNotify.Core/Services/NotificationSoundPolicy.cs`):

```text
if (!soundsEnabled || pauseNotifications) return false;
return !doNotDisturb || (priority == critical && playCriticalSoundsDuringDoNotDisturb);
```

File resolution uses `ManagedSoundStore.Resolve` against the data directory's `sounds/` folder. Missing files are logged and no sound plays; the notification itself is unaffected.

Files imported through Settings → Sounds are validated by `ManagedSoundStore.Import` (`src/AgentNotify.Core/Services/ManagedSoundStore.cs`): must be `.wav`/`.mp3`, `1 byte–10 MB`, copied with a content-addressed safe name `{safeBase}-{hash16}{ext}` where `safeBase` is sanitized to `[A-Za-z0-9_-]` (max 40). Built-in tones (`chime.wav`, `ping.wav`, `alert.wav`, `knock.wav`) are seeded idempotently from embedded resources.

---

### Agent home override

`AGENTNOTIFY_AGENT_HOME`, when set in the environment of `agentnotifyd`, is the home directory the
router's agent connectors look under for `.codex/` and `.claude/`, instead of the broker user's own.
It exists so a second agent profile — or a verification run — can be connected without touching the
owner's real Codex and Claude Code configuration. It is read at broker start.

## Custom notification types

Custom types are presentation and behavior policy stored in `customNotificationTypes`. Notification rows persist only the stable identifier; deleting or disabling a definition never makes historical rows unreadable — they fall back to generic info styling and a `7`-second lifetime.

Schema (`src/AgentNotify.Core/Config/NotificationTypeDefinition.cs`):

| Field | JSON name | Type | Default | Constraints |
|-------|-----------|------|---------|-------------|
| Stable identifier | `id` | `string` | `"custom"` | Normalized with `NotificationTypes.Normalize` (trim, `-` → `_`, lower-case, `inputrequired`/`permissionrequired` aliases). Must match `^[a-z][a-z0-9_]{0,63}$`. Must not collide with the eight built-in IDs. Must be unique among customs. Invalid or duplicate definitions are dropped during `ApplyDefaults`. |
| Display name | `displayName` | `string` | `"Custom"` | When empty/whitespace set to `id.Replace('_',' ')`; otherwise trimmed. |
| Accent color | `accentColor` | `string` | `"#4A90D9"` | Must be `#RRGGBB` hex; validated and upper-cased, otherwise reset to `"#4A90D9"`. |
| Default priority | `defaultPriority` | enum | `"normal"` | `low`/`normal`/`high`/`critical` (snake_case JSON via `JsonStringEnumConverter`). Used by `NotificationService.CreateAsync` when the request omits `priority`. |
| Lifetime | `durationSeconds` | `int` | `7` | Auto-dismiss seconds; `0` is sticky. Clamped `0–86400`. |
| Enabled | `enabled` | `bool` | `true` | Only enabled definitions are considered for effective duration, default priority, and accent. |

Fallback behavior: a notification whose `type` has no enabled custom definition uses the built-in `toastDurations` entry or `7` seconds, normal priority, and generic accent.

Example fragment:

```json
{
 "port": 47821,
 "toastLocation": "BottomRight",
 "customNotificationTypes": [
 {
 "id": "deployment_waiting",
 "displayName": "Deployment waiting",
 "accentColor": "#7C3AED",
 "defaultPriority": "high",
 "durationSeconds": 0,
 "enabled": true
 }
 ]
}
```

---

## Settings window coverage

The [web interface](WEB_UI.md) edits the same properties as the tray Settings window, on every
platform, except `launchAtStartup`, which stays in the Windows tray menu. Live quota also edits
`macMenuBar`, which has no WPF Settings control because it belongs to the macOS host.

The tray Settings window (`src/AgentNotify.App/SettingsWindow.xaml`, `.xaml.cs`) edits these config properties directly:

- General tab: `port` (1–65535), `historyRetentionDays` (0–3650), `pauseNotifications`, `doNotDisturb`.
- Toasts tab: `toastLocation`, `maxVisibleToasts` (1–20), eight `toastDurations` entries (0–86400 each).
- Custom types tab: full `customNotificationTypes` CRUD with the validation rules above.
- Sounds tab: `soundsEnabled`, `soundVolume` (0–100), `playCriticalSoundsDuringDoNotDisturb`, `defaultSoundFile`, `typeSoundFiles`, built-in tone picker (Chime, Ping, Alert, Knock) and WAV/MP3 import.
- Channels tab: provider profiles and delivery routes (persisted in SQLite, not in `config.json`).

These properties have no control in the Settings window and are edited by editing `config.json` or through the tray menu / environment:

- `authToken` — generated and shown only via `agentnotify token` or the file itself.
- `apiVersion` — read-only.
- `maxRequestBodyBytes`, `rateLimitPerSecond`, `maxMetadataBytes`, `routerMaxRequestBodyBytes`,
  `routerLedgerRetentionDays` — code defaults and file edits only.
- `launchAtStartup` — toggled from the tray menu (registry is the source of truth; reconciled to the file on startup).
