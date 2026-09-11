# Relay + mobile interaction contract (v1)

This document is the build spec for the other half of the loop: the
**Relay server** endpoints and the **mobile app** answer UI that turn a
waiting broker interaction into a tap on the phone and back into the
coding host. Everything here is versioned by `contract_version: "1"`.
Bump it — on both sides together — before changing any field.

Status: **broker side shipped** (publish, poll, ingest, CLI). Relay
endpoints and mobile UI are **not implemented yet**; this file is their
specification.

## The loop in one picture

```text
host hook (ask mode / transport / watcher)
  |  POST /v1/interactions/request (loopback, bearer)
  v
broker stores interaction (pending, digest, nonce, expiry)
  |  auto-publish -> outbox -> Relay adapter seals per-device envelope
  v
Relay POST /v1/envelopes (existing flow, unchanged)
  |  phone decrypts envelope, sees payload_kind "interaction-request"
  v
phone renders question + choices + expiry countdown
  |  user taps Allow / Deny (or types)
  |  POST /v1/interaction-responses (device bearer)      <-- IMPLEMENT
  v
Relay stores the opaque answer
  |  desktop polls GET /v1/interaction-responses          <-- IMPLEMENT
  v
broker revalidates (digest, nonce, kind, expiry, first-wins) and settles
  |
host hook wait returns -> native decision printed into the live call
```

Two facts shape the whole contract:

1. **Requests ride the existing envelope flow.** No Relay change is needed
   for phone-bound questions: the sealed payload just carries a new
   `payload_kind`. Any phone that does not understand it still shows the
   companion notification the harness sends alongside (forward compatible).
2. **Responses need two new Relay endpoints.** Relay is a dumb,
   authenticated store here: it checks shape and bearer, keeps bytes, and
   serves them back. All authorization semantics (digest, nonce, expiry,
   first-wins) are re-checked by the desktop broker, which is the only party
   that holds the request truth.

## Request payload (broker -> Relay -> phone)

Sealed per device inside the standard v1 envelope
(`envelope_version: "1"`, same recipients/ciphertext model as
notifications). The decrypted plaintext is this JSON object:

```json
{
  "payload_kind": "interaction-request",
  "contract_version": "1",
  "interaction": {
    "id": "a3f19c…",
    "key": "shop-sess99-ask",
    "agent": "codex",
    "agent_instance": "codex-71dc",
    "project": "shop",
    "session_id": "sess-99",
    "kind": "permission",
    "prompt": "Codex approval: Bash rm -rf /tmp/build",
    "choices": [
      { "id": "allow", "label": "Allow once", "detail": null },
      { "id": "deny", "label": "Deny", "detail": null }
    ],
    "text_max_length": 500,
    "status": "pending",
    "request_digest": "9f2c…64 hex chars…",
    "nonce": "single-use secret, base64url",
    "created_at": "2026-09-12T10:00:00+00:00",
    "expires_at": "2026-09-12T10:10:00+00:00"
  }
}
```

| Field | Type | Required | Notes |
| --- | --- | --- | --- |
| `payload_kind` | string | yes | Always `"interaction-request"`. Anything else is a notification payload. |
| `contract_version` | string | yes | `"1"`. Refuse to render unknown versions. |
| `interaction.id` | string | yes | Broker identity; echoed back in the answer. |
| `interaction.key` | string/null | no | Dedup key; informational for mobile. |
| `interaction.agent`, `agent_instance`, `project`, `session_id` | string/null | no | Display context. Never trust for routing — the envelope already did that. |
| `interaction.kind` | string | yes | `"permission"`, `"single_choice"`, or `"text"`. |
| `interaction.prompt` | string | yes | Exact question text, 1–2000 chars. Render verbatim. |
| `interaction.choices` | array | yes for choice kinds | `{id, label, detail?}`. Render every choice; never invent one. |
| `interaction.text_max_length` | integer | yes | Enforce client-side for `text`; the broker re-checks. |
| `interaction.status` | string | yes | Always `"pending"` on the wire. |
| `interaction.request_digest` | string | yes | 64-char hex. Echo verbatim in the answer. |
| `interaction.nonce` | string | yes | Echo verbatim. Proves the phone saw this request. |
| `interaction.created_at`, `expires_at` | string | yes | RFC 3339. Hide/disable the card after `expires_at`. |

Which questions leave the machine: only interactions matching an **enabled
Relay route with `IncludeMessage`** (same consent as notification bodies),
filtered by the route's type/project/agent/priority gates. A bodyless route
never carries questions — choices, digests, and nonces are message content.

## Response endpoints (Relay server to implement)

Base path and auth follow the existing Relay conventions: bearer tokens in
`Authorization: Bearer`, JSON bodies capped at 64 KiB, `Idempotency-Key`
honored where noted.

### Submit an answer (mobile -> Relay)

```http
POST {relay}/v1/interaction-responses
Authorization: Bearer <device token>
Content-Type: application/json
```

```json
{
  "contract_version": "1",
  "response_id": "7b9e… (client UUID)",
  "interaction_id": "a3f19c…",
  "request_digest": "9f2c…",
  "nonce": "single-use secret from the request",
  "choice_id": "deny",
  "installation_id": "install_…",
  "device_id": "phone-1"
}
```

For `text` interactions send `"text"` instead of `"choice_id"` (never both).

Relay validation (shape + auth only; semantics belong to the desktop):

- bearer device token valid → else `401`;
- `contract_version == "1"` → else `400`;
- `response_id`, `interaction_id`, `request_digest`, `nonce` present → else `400`;
- exactly one of `choice_id` / `text` present → else `400`;
- store keyed by `response_id` (unique per installation).

Responses:

| Status | Body | Meaning |
| --- | --- | --- |
| `201` | `{"status":"accepted","response_id":"…"}` | Stored. |
| `409` | `{"status":"duplicate","response_id":"…"}` | Same `response_id` seen before. Return the original outcome; mobile treats it as success. |
| `400` | `{"error":"…"}` | Shape/contract failure. |
| `401`/`403` | `{"error":"…"}` | Bad device token. |

Retention: keep a response until every subscribed installation's cursor has
passed it plus 24 h grace, or 7 days, whichever comes first. An answer to an
already-settled interaction is still stored (the desktop decides, idempotently).

### Poll answers (desktop -> Relay)

```http
GET {relay}/v1/interaction-responses?installation_id=<id>&since=<cursor>
Authorization: Bearer <installation token>
Accept: application/json
```

| Status | Body | Meaning |
| --- | --- | --- |
| `200` | `{"responses":[…],"next_cursor":"…"}` | `responses` in store order. Empty array with the same cursor means "nothing new". First poll sends `since=` empty: return the last 24 h. |
| `401`/`403` | `{"error":"…"}` | Bad installation token. Desktop aborts the run and keeps its cursor. |
| `5xx` | — | Desktop retries later; cursor unchanged. |

`next_cursor` is opaque to the desktop (pass it back verbatim). Advancing it
acknowledges receipt. At-least-once delivery is expected and safe: the broker
replays a repeated `response_id` to the original outcome and rejects a second,
different answer with `409` (first valid response wins).

Desktop poll cadence is a local decision (`interactions poll-responses` on a
schedule today; dispatcher-integrated polling later). No webhook/push from
Relay to desktop exists in v1 — the desktop always pulls.

## Mobile UI spec (minimum viable)

1. **Card per `interaction-request`**: project + agent line, full prompt,
   one button per choice (label + detail), or a bounded text field for
   `text`. No other actions on the card — an answer is only a choice or
   bounded text, never a command, URL, or callback.
2. **Expiry**: countdown from `expires_at`; disable the card at zero with
   "Expired — answer in the session instead". Never send after expiry (the
   broker would reject it).
3. **Submit**: generate `response_id` as a random UUID once per tap;
   **retry the same `response_id`** on transport failure (this is what makes
   retries idempotent). Echo `interaction_id`, `request_digest`, `nonce`,
   and `installation_id` verbatim from the request.
4. **Outcome**: `201`/`409-duplicate` → "Answer recorded". Any other `409`
   shape or a later state → "Already answered elsewhere". Show which choice
   won only if a subsequent poll says so (v1 has no phone-side result push).
5. **Unknown `contract_version`** → render nothing, log locally. Old phones
   keep showing the companion notification text, which is the whole point of
   sending both.

## Trust assumptions (read before implementing)

- v1 transport trust is **TLS + bearer tokens**. Relay sees request nonces
  and answer contents (it terminates TLS). This matches the notification
  path's posture: Relay is your own server and is trusted for liveness and
  ordering, **not** for authorization semantics.
- Authorization rests on three broker-checked bindings, none of which Relay
  can mint: the **digest** (request content), the **nonce** (request
  receipt), and **first-wins + idempotent `response_id`** (replay defense).
  A forged or replayed answer fails at the broker and is logged with a
  stable error code, never applied.
- Not sealed end-to-end (yet): sealing answers to the installation key
  would require an installation keypair the broker does not have today.
  Tracked as follow-up work; the nonce/digest design already carries the
  fields a sealed v2 needs (`installation_id`, `device_id`, `response_id`).
- Never log prompts, choices, answers, digests, or nonces on Relay, desktop,
  or mobile beyond the decrypted render surface. Audit logs keep ids, kinds,
  timestamps, and outcomes only.

## Worked example

Broker publishes (`payload_kind: interaction-request`, sealed in the envelope
the phone already knows how to open). Phone renders Allow/Deny + countdown.
User taps Deny; phone POSTs:

```json
{
  "contract_version": "1",
  "response_id": "c0ffee01-…",
  "interaction_id": "a3f19c…",
  "request_digest": "9f2c…",
  "nonce": "k7…",
  "choice_id": "deny",
  "installation_id": "install_abc",
  "device_id": "pixel-8"
}
```

Relay stores, returns `201`. Desktop `poll-responses` fetches it, POSTs to
`…/interactions/a3f19c…/respond` with `source: relay`, broker answers `200`,
the waiting Codex hook prints `decision.behavior: deny`, and Codex shows the
denial reason to the model. A retried submit with the same `response_id`
returns `409 duplicate` and changes nothing.

## What is deliberately out of v1

- Phone-side result push (phone learns the outcome only by polling; not specified yet).
- Sealed (E2E) answers; per-device request encryption already exists, answer sealing is next.
- Dispatcher-integrated polling (CLI schedule today).
- Multi-device conflict UI beyond "already answered elsewhere".
- Persistent/session grant scopes: only the exact choices the host offered.
