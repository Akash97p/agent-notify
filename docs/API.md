# AgentNotify API

Base URL: `http://127.0.0.1:47821` (configurable via `config.json` `port` or `AGENTNOTIFY_PORT`).
All `/v1/*` routes require `Authorization: Bearer <token>`. The token is generated on first launch
and stored as `authToken` in the platform data directory's `config.json` (`%LOCALAPPDATA%\AgentNotify`
on Windows; `$XDG_DATA_HOME/AgentNotify` or `~/.local/share/AgentNotify` on macOS/Linux). It can be
overridden by `AGENTNOTIFY_TOKEN` or `--token` on the CLI.

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
| `version` | string | Product informational version, e.g. `"0.2.0-alpha.3"` |
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

### `POST /v1/events`

Accepts ARC 0.2 `request.created`, `request.updated`, `response.submitted`, and `request.resolved`
events. The sender, execution context, semantic request kind, title, message, priority, and stable
condition key project into the native notification lifecycle.
`extensions.x-agentnotify.notification_type` may select a configured local type ID; other correctly
namespaced vendor extensions are ignored. `arc_version` must be exactly `"0.2"`; 0.1 is rejected.

An unkeyed created event derives a stable key from `sender.id` and `event_id`, so a delivery retry
returns the original notification even after resolution. An explicit `request.key` opts into the
condition lifecycle. Updates require an existing active condition and never create a missing one;
resolutions are idempotent.

Every accepted event returns the same envelope:

```json
{ "notification": { }, "interaction": { } }
```

`notification` is the local record; `interaction` is the durable question the producer waits on, and
is `null` unless the event was answerable. A request carrying `request.response` opens both under one
key — see [Interactions](INTERACTIONS.md) for the model and its own routes. `response.submitted`
delivers one answer and resolves the notification half with it; a resolution withdraws any question
still open under the key.

A newly persisted request returns `201`. An update, answer, resolution, keyed in-place refresh, or
immutable replay returns `200`. A missing update/answer/resolution target returns `404`, a second
answer to an already answered condition returns `409`, and invalid or unsupported envelopes return
`400`. An answerable event whose question is invalid is rejected before anything is stored. The
endpoint uses the same validation, persistence, callbacks, routing, and durable outbox boundary as
native creation.

See [ARC.md](ARC.md) for the complete contract, lifecycle, schema, security rules, and examples.

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

### `POST /v1/interactions/request`

Open a durable waiting question or permission. A pending interaction with the same `key` is returned
instead of duplicated; if the request bound to that key changed, the previous one is superseded and a
new interaction is created.

**Request** (`CreateInteractionRequest`, snake_case):

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `key` | string | no | Logical deduplication key |
| `agent` | string | no | Host agent ID; default `unknown` |
| `agent_instance` | string | no | Per-run agent identity |
| `project` | string | no | Project/repository name |
| `session_id` | string | no | Opaque native session ID |
| `turn_id` | string | no | Opaque native turn/generation ID |
| `native_request_id` | string | no | Opaque host request ID needed to return the answer |
| `kind` | enum | no | `permission` (default), `single_choice`, or `text` |
| `prompt` | string | yes | 1–2000 characters |
| `choices` | array | for choice kinds | 2–12 `{ "id", "label", "detail"? }` entries |
| `text_max_length` | int | no | Text-answer limit, 1–2000; default 500 |
| `ttl_seconds` | int | no | Expiry window, 30–3600; default 600 |

**Response:** `201` with `InteractionDto` when created; `200` when a pending keyed request is reused;
`400` on validation failure.

### `GET /v1/interactions`

List interactions newest first. Optional query parameters are `pending` (bool), `status`, `agent`,
`project`, `session`, and `limit` (1–500, default 100). Returns `InteractionDto[]`.

### `GET /v1/interactions/{id}`

Return one `InteractionDto`, or `404` when it does not exist. Reads also sweep an overdue pending
interaction to `expired`.

### `GET /v1/interactions/{id}/wait`

Long-poll one interaction until it reaches a terminal state or the timeout elapses. `timeout` is
clamped to 1–300 seconds and defaults to 60. A timeout returns `200` with the current pending
`InteractionDto`; callers may wait again. Unknown IDs return `404`.

### `POST /v1/interactions/{id}/respond`

Submit the first valid answer.

**Request** (`RespondInteractionRequest`, snake_case):

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `response_id` | string | yes | Client idempotency ID, 1–128 characters |
| `request_digest` | string | yes | Must equal the current interaction digest |
| `nonce` | string | yes | Single-use nonce returned in the current `InteractionDto` |
| `choice_id` | string | choice kinds | Exactly one of `choice_id` or `text` |
| `text` | string | text kind | Bounded by `text_max_length` |
| `source` | string | no | `desktop`, `cli`, `relay`, or a host ID |
| `device_id` | string | no | Responding device identity when known |

Returns the answered `InteractionDto`. An identical `response_id` replays the accepted `200`; a
different second answer returns `409`. Stale digests/nonces, wrong answer shapes, terminal states, and
expired requests return `400`; unknown IDs return `404`.

### `POST /v1/interactions/{id}/cancel`

Cancel a pending interaction and wake any waiter. Returns the current `InteractionDto`; unknown IDs
return `404`. Cancelling an already-terminal interaction is idempotent.

### `POST /v1/interactions/{id}/publish`

Republish the interaction through every matching enabled AgentNotify Relay route whose payload policy
allows message content. The response is `{ "id": "...", "published": <count> }`. Returns `400` when
Relay publishing is not configured and `404` for an unknown interaction. Publishing is idempotent per
interaction and Relay provider.

See [INTERACTIONS.md](INTERACTIONS.md) for lifecycle, first-response-wins, digest/nonce, expiry, host
capture, and Relay synchronization rules.

---

## DTOs

### `NotificationDto`

```
id, key?, agent, agentInstance?, project?, type, priority, title, message,
cwd?, pid?, status, createdAt, updatedAt, resolvedAt?, metadata?
```

Timestamps are ISO 8601 (`DateTimeOffset`). `metadata` is `Record<string, JsonElement>?` (arbitrary JSON values, erased to `null` when absent).

Custom type definitions are local presentation policy stored in `config.json`; notification rows persist only the stable identifier. Missing, disabled, or deleted definitions safely fall back to generic info styling and a seven-second lifetime.

### `InteractionDto`

```
id, key?, agent, agent_instance?, project?, session_id?, turn_id?, native_request_id?,
kind, prompt, choices, text_max_length, status, request_digest, nonce,
created_at, updated_at, expires_at, answered_at?, response?
```

`status` is `pending`, `answered`, `expired`, `cancelled`, or `superseded`. `response`, when present,
contains `response_id`, `choice_id?`, `text?`, `source`, `device_id?`, and `created_at`. This DTO
contains the response-binding nonce and is loopback/bearer protected; do not forward it to unrelated
services or logs.

### `HealthResponse` — see `GET /v1/health`.

---

## Web interface

Everything under `/ui` belongs to the web interface and is **not** part of the stable API.
`/ui/api/*` takes no bearer token, requires `X-AgentNotify-UI: 1` and a loopback `Host` on every
state change, and may change between releases without notice.
Automate against `/v1`. The trust model is in [WEB_UI.md](WEB_UI.md).

---

## Auth

```
Authorization: Bearer <token>
```

The supplied and expected values are hashed with SHA-256 and compared with a fixed-time byte comparison. Missing/invalid token → `401 { "error": "unauthorized" }`. The bare `/health` probe is **not** authenticated.

---

## Rate limiting

Every `POST` under `/v1/notifications` or `/v1/interactions`, plus `POST /v1/events`, is guarded by one sliding
fixed-window counter per token: default **30 requests/second** (`rateLimitPerSecond`). `GET` and
`PATCH` are not rate limited. When exceeded: `429 { "error": "rate limit exceeded" }` with
`Retry-After: 1`.

---

## Errors

| Status | Shape | When |
|--------|-------|------|
| 400 | `{ "error": "<message>" }` | Validation failure, invalid JSON, illegal status transition |
| 401 | `{ "error": "unauthorized" }` | Missing/invalid bearer token |
| 404 | `{ "error": "notification not found" }` or `{ "error": "interaction not found" }` | Unknown `id` |
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

### ARC event

```bash
TOKEN="$(agentnotify.exe token)"
curl --fail-with-body -sS -X POST http://127.0.0.1:47821/v1/events \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{
    "arc_version":"0.2",
    "event_id":"evt_build_42",
    "event_type":"request.created",
    "occurred_at":"2026-08-26T01:15:00Z",
    "sender":{"id":"codex","name":"Codex"},
    "context":{"project":"agent-notify"},
    "request":{
      "kind":"completion",
      "title":"Build complete",
      "message":"All tests passed."
    }
  }'
unset TOKEN
```
