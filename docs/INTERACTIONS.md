# Interactions (waiting questions and permissions)

A notification tells the human something happened. An **interaction** asks the
human something and waits for the answer: a permission (`allow once` /
`deny`), one of a bounded list of choices, or a short text. The broker owns
exactly one durable record per question and accepts at most one answer —
first valid response wins.

This document covers the broker model, the loopback API, and the CLI. Host
harness capture (hooks that open interactions and return the answer) is in
[HARNESS.md](HARNESS.md); the Relay/mobile wire contract is in
[RELAY_INTERACTIONS.md](RELAY_INTERACTIONS.md).

[ARC 0.2](ARC.md) is the transport-neutral contract for the same thing. An ARC
request carrying a `response` specification opens one of these interactions
alongside its notification, and an ARC `response.submitted` event is this
answer path expressed as an event. The rules below are the normative ones:
ARC describes them, the broker enforces them.

## Relay sync

Phone-bound questions ride the existing Relay envelope flow: on every new
interaction the broker enqueues one sealed `interaction-request` payload per
matching **enabled Relay route with `IncludeMessage`** (same consent as
notification bodies; bodyless routes never carry questions). Outbox ids are
deterministic per interaction+provider, so republishing is idempotent.
`POST /v1/interactions/{id}/publish` re-sends manually (handy for testing
the phone card without a live host).

Answers come back the other way: mobile POSTs to the Relay server, and the
desktop pulls them. While the broker runs, a background poller checks every
enabled Relay profile every few seconds with bounded backoff (Windows tray and
headless `agentnotifyd` both host it); answers normally reach a waiting host
without anyone running anything. `agentnotify interactions poll-responses
[--provider ID] [--json]` remains for diagnostics and one-off polls.

Every fetched answer is revalidated by the broker (digest, nonce, kind,
expiry, first-wins), so a replay, a stale answer, or an answer to a changed
question is refused. Relay sees v1 answers in plaintext — it is trusted for
integrity but not for authorization — so sealing answers to an installation
key remains tracked follow-up. Poll cursors persist per provider in
`relay_response_cursors.json` next to the broker config, and a cursor only
advances after the whole fetched batch was processed: a transient broker or
Relay failure retries the same answers rather than dropping them.

## Host capture

A host adapter turns a live permission/question into an interaction and the
accepted answer back into the host's native decision:

1. The synchronous hook fires (Codex/Claude `PermissionRequest`) and blocks.
2. The bundled hook bridge (`agentnotify_hook.py <agent> ask-permission`)
   opens one interaction (keyed per project/session), sends the visible
   notification, and waits on the broker.
3. The human answers from the local WebUI, `interactions respond`, or the phone via Relay.
4. The bridge prints the verified host decision JSON (`decision.behavior:
   allow/deny`) and exits `0`. The host applies it like its own prompt answer.
5. On any failure the bridge prints nothing and exits `0`: the host shows its
   ordinary local prompt. Ask mode returns control to the human in front of the
   machine — it never auto-allows, never auto-denies.

Enable per host with `install-harness <codex|claude> --ask`. Hermes
(approval transport), OpenClaw (watch daemon), and Pi (RPC mode) have their
own equivalent surfaces; see [HARNESS.md](HARNESS.md).

## Model

| Field | Purpose |
| --- | --- |
| `id` | Broker-assigned identity (`N` hex) |
| `key` | Optional logical key: a repeated key reuses the pending interaction, or supersedes it when the question changed |
| `agent`, `agent_instance`, `project` | Who is asking, same meaning as notifications |
| `session_id`, `turn_id`, `native_request_id` | Opaque host correlation; `native_request_id` is what the harness needs to answer the live call |
| `kind` | `permission`, `single_choice`, or `text` |
| `prompt` | Exact human-readable question (1–2000 chars) |
| `choices` | 2–12 stable `{id, label, detail?}` entries for choice kinds; the host must only offer what it can return |
| `text_max_length` | Answer bound for `text` (default 500, max 2000) |
| `status` | `pending`, `answered`, `expired`, `cancelled`, `superseded` (terminal states never change) |
| `request_digest` | SHA-256 hex over the canonical request; every answer must echo it |
| `nonce` | Single-use secret every answer must echo; bearer-protected on loopback and sealed in Relay requests |
| `expires_at` | Absolute deadline (TTL 30–3600 s, default 600 s) |

A response carries a client-generated `response_id` (idempotency key), the
digest and nonce, one `choice_id` **or** `text` (never both, never neither), a `source`
(`desktop`, `cli`, `relay`, or host id), and an optional `device_id`.

Answer rules, in order:

1. Unknown id → `404`. Answered already → `409` (unless the `response_id`
   matches the accepted one, which replays the original `200`).
2. Not pending (`expired`, `cancelled`, `superseded`) → `400`.
3. Digest mismatch → `400`: the question changed since it was asked, so the
   answer cannot apply.
4. Missing or mismatched nonce → `400`.
5. Wrong shape for the kind (unknown `choice_id`, over-long `text`) → `400`.

Expiry is swept on every read/write; a `wait` that outlives the deadline
returns the `expired` state.

## Loopback API

All routes live under `/v1` and require the local bearer token, like the
rest of the API. `POST` routes share the create rate limit.

```http
POST /v1/interactions/request
GET  /v1/interactions?pending=true&status=&agent=&project=&session=&limit=
GET  /v1/interactions/{id}
GET  /v1/interactions/{id}/wait?timeout=60
POST /v1/interactions/{id}/respond
POST /v1/interactions/{id}/cancel
POST /v1/interactions/{id}/publish
```

`request` returns `201` on creation and `200` when a repeated key reuses the
pending interaction. `wait` blocks up to `timeout` seconds (1–300, default
60) and always returns the current state as JSON — `pending` on timeout, so
callers poll again. `publish` manually re-enqueues the question for matching enabled Relay routes and
returns the number published; it returns `400` when Relay publishing is unavailable. Field names are
`snake_case` throughout.

## CLI

```bash
agentnotify interactions request --kind permission --prompt "Deploy to prod?" \
  --choice allow-once:"Allow once" --choice deny:"Deny" \
  --agent codex --project shop --session sess-1

agentnotify interactions list --pending
agentnotify interactions get <id>
agentnotify interactions wait <id> --timeout 120
agentnotify interactions respond <id> --response-id r1 --digest <digest> --nonce <nonce> --choice deny
agentnotify interactions cancel <id>
```

`--choice` is repeatable as `ID:LABEL`; add `--choice-detail ID:DETAIL` for
scope/command previews. `respond` needs the digest and nonce from `get` and exactly one
of `--choice` / `--text`. See `agentnotify help interactions`.

## Storage and lifecycle

Interactions persist in the broker SQLite database (`interactions` table,
same file as notifications and delivery state). Terminal interactions older
than the history retention window are pruned with the same job. In-process
waiters complete the instant an answer, cancellation, or expiry lands — the
harness never polls in a loop.

## Security notes

- The loopback bearer token is the authorization boundary for local calls.
- The digest binds an answer to the exact displayed request; a changed
  prompt (key reuse with new text) supersedes the old interaction instead of
  inheriting its answer.
- The nonce binds every answer to a request the caller actually read. It is returned only by the
  bearer-protected loopback API or inside the sealed Relay request; never log or forward it elsewhere.
- Logs record interaction ids, kinds, and outcomes — never prompts, choices,
  answers, digests, or nonces.
