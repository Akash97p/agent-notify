# Relay + mobile interaction contract (v1)

This document is the build spec for the other half of the loop: the
**Relay server** endpoints and the **mobile app** answer UI that turn a
waiting broker interaction into a tap on the phone and back into the
coding host. Everything here is versioned by `contract_version: "1"`.
Bump it — on both sides together — before changing any field.

Status: **implemented end to end**. The broker publishes questions, polls
answers, and ingests them; Relay implements both response endpoints; the
mobile app renders question cards and submits answers; the desktop polls
Relay continuously while it runs (a CLI command remains for diagnostics).
The one deliberate gap: a mobile-originated, unsolicited message to an agent
is not this contract — see *Sending a message to an agent* below.

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

## Response endpoints (Relay server)

Base path and auth follow the existing Relay conventions: bearer tokens in
`Authorization: Bearer`, JSON bodies capped at 64 KiB, `Idempotency-Key`
honored where noted. The Relay also enforces the request media type and the
wire-byte cap before parsing JSON, and returns `Retry-After` on `429`.

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

`installation_id` is the containing envelope's outer `sender_id` — the desktop
installation that asked is the installation the answer must reach. It is not
in the sealed interaction object, and it is not `GET /v1/device` (which
returns `installation_id: null` for every normally paired phone). A question
whose envelope carries no `sender_id` has no destination and the card
disables.

Relay validation (shape + auth; digest/nonce/first-wins semantics belong to
the desktop):

- bearer device token valid and not revoked → else `401`;
- `contract_version == "1"` → else `400`;
- `response_id`, `interaction_id`, `request_digest`, `nonce` present → else `400`;
- exactly one of `choice_id` / `text` present → else `400`;
- `nonce` 1–128 chars, `choice_id` ≤ 64, `text` ≤ 2000 → else `400`. These are
  the broker's own hard caps, deliberately not looser: Relay is a dumb store,
  but one that accepts what the broker provably discards tells the phone
  "Relay recorded your answer" for an answer nothing can ever apply, and burns
  retention holding it. The per-interaction `text_max_length` is tighter still
  and stays the broker's to enforce — Relay never sees the request;
- `device_id` equals the authenticated device → else `403`;
- the device shares the installation's owner, or holds the legacy direct
  link → else `403`;
- media type is `application/json` → else `415`; wire bytes ≤ 64 KiB → else `413`;
- store keyed by `(installation_id, response_id)`, unique.

Responses:

| Status | Body | Meaning |
| --- | --- | --- |
| `201` | `{"status":"accepted","response_id":"…"}` | Stored. |
| `409` | `{"status":"duplicate","response_id":"…"}` | Same `response_id` and byte-identical answer fields seen before. Mobile treats it as success. |
| `409` | `{"error":{"code":"conflict",…}}` | Same `response_id` with a *different* body. Never reported as a duplicate. |
| `400` | `{"error":{…}}` | Shape/contract failure. |
| `401` | `{"error":{…}}` | Device revoked: the phone wipes and returns to pairing. |
| `403` | `{"error":{…}}` | Valid pairing, not authorized for this answer. Never wipes the pairing. |
| `415`/`413` | `{"error":{…}}` | Wrong media type / body over 64 KiB. |

Retention: a response is kept until 7 days pass, or the owning installation's
acknowledged cursor has been at rest past it for 24 h — whichever comes
first. An answer to an already-settled interaction is still stored (the
desktop decides, idempotently).

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

`next_cursor` is opaque to the desktop (pass it back verbatim). Sending a
previously returned `next_cursor` back as `since` acknowledges that page; the
cursor returned by a response is **not** acknowledged until the caller sends
it back. At-least-once delivery is expected and safe: the broker replays a
repeated `response_id` to the original outcome and rejects a second,
different answer with `409` (first valid response wins).

The desktop polls on a low-frequency continuous loop while the broker runs,
with bounded backoff on failure, and keeps its cursor in durable local state
so a restart resumes rather than re-reading. `agentnotify interactions
poll-responses` remains for diagnostics. No webhook/push from Relay to
desktop exists in v1 — the desktop always pulls.

## Mobile UI spec (minimum viable)

1. **Card per `interaction-request`**: project + agent line, full prompt,
   one button per choice (label + detail), or a bounded text field for
   `text`. No other actions on the card — an answer is only a choice or
   bounded text, never a command, URL, or callback.
2. **Expiry**: countdown from `expires_at`; disable the card at zero with
   "Expired — answer in the session instead". Never send after expiry (the
   broker would reject it), and never sound a system notification for a
   question that is already expired.
3. **Submit**: generate `response_id` as a random UUID once per tap; persist
   the complete answer body locally **before** the first POST, and on any
   ambiguous failure retry that exact stored body with the same
   `response_id` — never mint a new id and never change the answer. The
   containing envelope's `sender_id` is the `installation_id`.
4. **Outcome**: `201`/`409-duplicate` → "Relay recorded your answer", which
   is not the same as the desktop or host accepting it; any other `409` →
   "Already answered elsewhere". Only `401` wipes the pairing; `403` is a
   permanent authorization error that leaves it intact. Show which choice won
   only if a later result path says so (v1 has no phone-side result push).
5. **Unknown `contract_version`** → render nothing, log locally. Old phones
   keep showing the companion notification text, which is the whole point of
   sending both.
6. **Project conversations**: group the inbox by the stable local key
   `(envelope.sender_id, normalized decrypted project)`, with an explicit
   "No project" group. The project name and sender id come from decrypted
   payloads; Relay never sees them.

## Trust assumptions (read before implementing)

- v1 transport trust is **TLS + bearer tokens**. Relay sees request nonces
  and answer contents (it terminates TLS). This matches the notification
  path's posture: Relay is your own server and is trusted for liveness and
  ordering, **not** for authorization semantics.
- Authorization rests on three broker-checked bindings: the **digest**
  (request content), the **nonce** (request receipt), and **first-wins +
  idempotent `response_id`** (replay defense). A forged or replayed answer
  fails at the broker and is logged with a stable error code, never applied.
- Those bindings stop replay and stale answers; they do not make a malicious
  Relay harmless. Relay sees the nonce, digest, choice and text in plaintext
  and could alter the choice while preserving the digest and nonce, so v1
  trusts the self-hosted Relay for **answer integrity** as well as liveness
  and ordering. Do not claim otherwise; a sealed response v2 (installation
  key pair) is the fix.
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

Relay stores, returns `201`. The desktop's continuous poll fetches it, POSTs
to `…/interactions/a3f19c…/respond` with `source: relay`, the broker answers
`200`, and the waiting Codex hook prints `decision.behavior: deny`. A retried
submit with the same `response_id` returns `409 duplicate` and changes
nothing.

## Sending a message to an agent (not this contract)

The user's other request — write a free-form message from the phone to an
agent working on a project — cannot ride this contract. An answer is
authorized by a pending request's digest, nonce, and first-wins state; a
message has none of those, and pretending otherwise would let a phone message
masquerade as a permission decision.

The transport decision is made and does not need WebSockets or MQTT:

- **Relay -> phone** stays contentless FCM (`{envelope_id, version}`); the
  phone fetches and decrypts.
- **Phone -> Relay** is an authenticated HTTPS `POST` with the device bearer.
- **Relay -> desktop** is the desktop's own authenticated HTTPS `GET` poll.
  The desktop is behind NAT and holds an installation credential; it never
  accepts an inbound connection, so no websocket or broker is required at
  this volume.

A message therefore needs a separate, versioned installation inbox (for
example `POST /v1/inbound-events` device-authenticated, and
`GET /v1/inbound-events?installation_id=…&since=…` installation-authenticated)
with a database-ordered sequence, explicit acknowledgement, idempotent
`message_id`, project/thread metadata inside the sealed payload, and a
`can_message` grant distinct from notification receipt. On the desktop, an
accepted message must be persisted before acknowledgement and then routed to
a host adapter — and no adapter exists that can inject text into an arbitrary
running TUI session today, so "delivered to the agent" may legitimately mean
"queued for the agent" until ACP-managed sessions or a native adapter can
accept it. Claiming agent delivery because transport succeeded would be
false. This is follow-up work, tracked in the desktop repository's TODO.

## What is deliberately out of v1

- Phone-side result push (phone learns the outcome only by polling; not specified yet).
- Sealed (E2E) answers. Relay terminates TLS and sees the answer fields in
  plaintext, so v1 trusts the self-hosted Relay for answer integrity; the
  digest and nonce bind an answer to a request but do not stop a malicious
  Relay from altering the choice after it receives it. A future sealed
  response v2 needs an installation key pair.
- Multi-device conflict UI beyond "already answered elsewhere".
- Persistent/session grant scopes: only the exact choices the host offered.
- Unsolicited phone-to-agent messages (see the section above).
