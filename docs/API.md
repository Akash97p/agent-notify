# AgentNotify API

Base URL: `http://127.0.0.1:47821` (configurable via `config.json` `port` or `AGENTNOTIFY_PORT`).
All `/v1/*` routes require `Authorization: Bearer <token>`. The token is generated on first launch and stored at `%LOCALAPPDATA%\AgentNotify\config.json` (`authToken`). It can be overridden by `AGENTNOTIFY_TOKEN` or `--token` on the CLI.

Priority/status enum strings and built-in type IDs are **snake_case** (`input_required`, `permission_required`, etc.). Type IDs may also be user-defined: 1–64 lowercase letters, numbers, or underscores, starting with a letter. CLI input accepts hyphens and normalizes them to underscores.

`Content-Type: application/json`. Max request body: 64 KiB (configurable `maxRequestBodyBytes`).

---

## Endpoints

### `GET /health`

Unauthenticated liveness probe.

```json
{ "status": "ok" }
```

### `GET /v1/health`

Authenticated. Returns broker health.

| Field | Type | Notes |
|-------|------|-------|
| `status` | string | `"ok"` |
| `version` | string | Assembly version, e.g. `"1.0.0"` |
| `pid` | int | Broker process id |
| `uptimeSeconds` | number | Seconds since start |
| `activeCount` | int | Count of `status=active` notifications |
| `apiVersion` | string | Always `"v1"` |
| `serverTimeUtc` | string (ISO 8601) | `DateTimeOffset.UtcNow` at handle time |

### `POST /v1/notifications`

Create or — when `key` matches an active notification — update in place (dedup).

**Request** (`CreateNotificationRequest`):

| Field | Type | Required | Notes |
|-------|------|----------|-------|
| `title` | string | yes | 1–200 chars |
| `message` | string | yes | 1–4000 chars |
| `type` | identifier | no | A built-in or user-defined type ID (default `info`) |
| `priority` | enum | no | `low`/`normal`/`high`/`critical`; omitted values use the custom definition default or `normal` |
| `agent` | string | no | Default `"unknown"`; 1–100 chars |
| `agentInstance` | string | no | 1–100 chars |
| `project` | string | no | 1–200 chars |
| `key` | string | no | 1–100 chars dedup key |
| `cwd` | string | no | 1–1024 chars |
| `pid` | int | no | Non-negative |
| `metadata` | object | no | Arbitrary JSON map; serialized size ≤ 8192 bytes |

Validation failures return `400` with `{ "error": "<message>" }`.

**Response** `201` → `NotificationDto`; on dedup the same `id` is returned with updated `updatedAt`.

### `GET /v1/notifications`

List notifications (newest first). Optional query params:

| Param | Type | Notes |
|-------|------|-------|
| `unresolved` | bool | When `true`, only `status=active` |
| `type` | identifier | Filter by exact normalized type ID |
| `status` | enum | Filter by exact status |
| `project` | string | Exact match, trimmed |
| `agent` | string | Exact match, trimmed |
| `limit` | int | 1–500 (default 100) |

**Response** `200` → `NotificationDto[]`.

### `GET /v1/notifications/{id}`

**Response**: `200` → `NotificationDto`; `404` when missing.

### `PATCH /v1/notifications/{id}`

Update status.

**Request** (`UpdateNotificationRequest`): `{ "status": "dismissed" | "resolved" | "active" }` — required. Transition rules (see `StatusTransitions`): `active → {dismissed,resolved}` always allowed; a dismissed/resolved notification can only be reopened (`→ active`).

**Response**: `200` → updated `NotificationDto`; `400` on invalid transition or missing `status`; `404` when missing.

### `POST /v1/notifications/{id}/dismiss`

Convenience: sets `status=dismissed`. Same responses as PATCH.

---

## DTOs

### `NotificationDto`

```
id, key?, agent, agentInstance?, project?, type, priority, title, message,
cwd?, pid?, status, createdAt, updatedAt, resolvedAt?, metadata?
```

Timestamps are ISO 8601 (`DateTimeOffset`). `metadata` is `Record<string, JsonElement>?` (arbitrary JSON values, erased to `null` when absent).

Custom type definitions are local presentation policy stored in `config.json`; notification rows persist only the stable identifier. Missing, disabled, or deleted definitions safely fall back to generic info styling and a seven-second lifetime.

### `HealthResponse` — see `GET /v1/health`.

---

## Auth

```
Authorization: Bearer <token>
```

The supplied and expected values are hashed with SHA-256 and compared with a fixed-time byte comparison. Missing/invalid token → `401 { "error": "unauthorized" }`. The bare `/health` probe is **not** authenticated.

---

## Rate limiting

`POST /v1/notifications` is guarded by a sliding fixed-window counter per token: default **30 requests/second** (`rateLimitPerSecond`). When exceeded: `429 { "error": "rate limit exceeded" }` with `Retry-After: 1`.

---

## Errors

| Status | Shape | When |
|--------|-------|------|
| 400 | `{ "error": "<message>" }` | Validation failure, invalid JSON, illegal status transition |
| 401 | `{ "error": "unauthorized" }` | Missing/invalid bearer token |
| 404 | `{ "error": "notification not found" }` | Unknown `id` |
| 429 | `{ "error": "rate limit exceeded" }` | POST rate limit |
| 413 | — | Request body exceeds `maxRequestBodyBytes` (Kestrel rejects before routing) |

Malformed payloads (invalid JSON) are handled gracefully — never crash the broker. Validation never logs the token.

---

## Examples

### CLI (recommended)

```bash
agentnotify.exe send --type completed --title "Build complete" --message "All tests passed."
```

### PowerShell

```powershell
$config = Get-Content "$env:LOCALAPPDATA\AgentNotify\config.json" | ConvertFrom-Json
$headers = @{ Authorization = "Bearer $($config.authToken)" }
$body = @{
  agent = "codex"
  project = "AgentNotify"
  type = "input_required"
  priority = "high"
  title = "Need input"
  message = "Choose option A or B."
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($config.port)/v1/notifications" `
  -Headers $headers -ContentType "application/json" -Body $body
```

### curl from WSL

Prefer `agentnotify.exe`; if direct HTTP debugging is explicitly required:

```bash
TOKEN="$(agentnotify.exe token)"
curl --fail-with-body -sS -X POST http://127.0.0.1:47821/v1/notifications \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"type":"blocked","priority":"high","title":"Blocked","message":"SDK missing"}'
unset TOKEN
```
