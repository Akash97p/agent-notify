# Configuration

AgentNotify is configured through a single per-user JSON file. The file is created on first launch with safe defaults and a generated bearer token. Unknown or malformed fields fall back to defaults; partial files are tolerated and rewritten with missing keys on startup.

Source: `src/AgentNotify.Core/Config/AgentNotifyConfig.cs`, `src/AgentNotify.Core/Config/ConfigStore.cs`, `src/AgentNotify.Core/Config/NotificationTypeDefinition.cs`.

---

## File location and format

Default directory and files (`src/AgentNotify.Core/Config/ConfigStore.cs`):

| Path | Purpose |
|------|---------|
| `%LOCALAPPDATA%\AgentNotify\config.json` | Typed configuration and bearer token |
| `%LOCALAPPDATA%\AgentNotify\agentnotify.db` | SQLite notification history |
| `%LOCALAPPDATA%\AgentNotify\logs\` | Daily log files |
| `%LOCALAPPDATA%\AgentNotify\sounds\` | Managed WAV/MP3 files |

`%LOCALAPPDATA%` is `Environment.SpecialFolder.LocalApplicationData`. All four paths are derived from the same config directory.

Format: JSON serialized with `AgentNotify.Contracts.Json.Options` (`System.Text.Json` with `JsonSerializerDefaults.Web`, `PropertyNameCaseInsensitive: true`, `JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)`). JSON property names are therefore camelCase (`port`, `authToken`, `toastLocation`) while enum strings on the wire are snake_case.

Loading behavior:

- Missing file returns defaults.
- Malformed JSON is caught as `JsonException`; defaults are kept and a subsequent `Save` rewrites a healthy file.
- After load `AgentNotifyConfig.ApplyDefaults()` normalizes and fills missing keys.
- Environment overrides `AGENTNOTIFY_PORT` and `AGENTNOTIFY_TOKEN` are applied when `ConfigStore(applyEnvOverrides: true)` (the CLI default). `HasTokenFile` and some app startup paths use `applyEnvOverrides: false`.

Saving is atomic: write to a temporary file then `File.Move(overwrite: true)`.

> Warning: `config.json` contains the local bearer token (`authToken`). This value authenticates any local process that can reach `127.0.0.1:47821`. Do not copy, commit, email, or otherwise share the file. Delete `%LOCALAPPDATA%\AgentNotify` only when intentionally removing the token, history, and logs.

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
| `defaultSoundFile` | `string?` | `null` | Global sound filename. Normalized to `Path.GetFileName` and accepted only when extension is `.wav` or `.mp3` (case-insensitive); otherwise `null`. Stored as a bare filename inside the managed sounds directory. Editable in Settings → Sounds (choose/preview/clear). |
| `typeSoundFiles` | `object` | `{}` | Per-type override map: type ID → filename. Keys are normalized with `NotificationTypes.Normalize`; values normalized as for `defaultSoundFile`. Invalid entries are dropped; duplicate normalized keys keep the last value. Case-insensitive. Editable in Settings → Sounds per type. |
| `playCriticalSoundsDuringDoNotDisturb` | `bool` | `false` | When `true`, critical-priority sounds play even when `doNotDisturb` is `true`. Editable in Settings → Sounds. See Sound policy. |
| `maxRequestBodyBytes` | `long` | `65536` (`64*1024`) | Kestrel `MaxRequestBodySize`. When `<= 0` reset to `65536`. Not editable in Settings. Bodies larger than this are rejected before routing. |
| `rateLimitPerSecond` | `int` | `30` | Simple fixed-window limit applied to every `POST` under `/v1/notifications` (per token, 1-second window); `GET` and `PATCH` are not limited. When `<= 0` reset to `30`. Not editable in Settings. Env does not override. |
| `maxMetadataBytes` | `int` | `8192` | Serialized metadata map size cap. When `<= 0` reset to `8192`. Not editable in Settings. Validation uses `JsonSerializer.SerializeToUtf8Bytes(metadata, Json.Options)`. |
| `toastDurations` | `object` | see below | Map of type ID → auto-dismiss seconds. `0` means sticky until dismissed/resolved. Backfilled from defaults and normalized. Editable in Settings → Toasts per built-in type (0–86400). |
| `customNotificationTypes` | `array` | `[]` | User-defined type definitions. See Custom types. Editable in Settings → Custom types. |

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

File resolution uses `ManagedSoundStore.Resolve` against `%LOCALAPPDATA%\AgentNotify\sounds\`. Missing files are logged and no sound plays; the notification itself is unaffected.

Files imported through Settings → Sounds are validated by `ManagedSoundStore.Import` (`src/AgentNotify.Core/Services/ManagedSoundStore.cs`): must be `.wav`/`.mp3`, `1 byte–10 MB`, copied with a content-addressed safe name `{safeBase}-{hash16}{ext}` where `safeBase` is sanitized to `[A-Za-z0-9_-]` (max 40). Built-in tones (`chime.wav`, `ping.wav`, `alert.wav`, `knock.wav`) are seeded idempotently from embedded resources.

---

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

The tray Settings window (`src/AgentNotify.App/SettingsWindow.xaml`, `.xaml.cs`) edits these config properties directly:

- General tab: `port` (1–65535), `historyRetentionDays` (0–3650), `pauseNotifications`, `doNotDisturb`.
- Toasts tab: `toastLocation`, `maxVisibleToasts` (1–20), eight `toastDurations` entries (0–86400 each).
- Custom types tab: full `customNotificationTypes` CRUD with the validation rules above.
- Sounds tab: `soundsEnabled`, `soundVolume` (0–100), `playCriticalSoundsDuringDoNotDisturb`, `defaultSoundFile`, `typeSoundFiles`, built-in tone picker (Chime, Ping, Alert, Knock) and WAV/MP3 import.
- Channels tab: provider profiles and delivery routes (persisted in SQLite, not in `config.json`).

These properties have no control in the Settings window and are edited by editing `config.json` or through the tray menu / environment:

- `authToken` — generated and shown only via `agentnotify token` or the file itself.
- `apiVersion` — read-only.
- `maxRequestBodyBytes`, `rateLimitPerSecond`, `maxMetadataBytes` — code defaults and file edits only.
- `launchAtStartup` — toggled from the tray menu (registry is the source of truth; reconciled to the file on startup).
