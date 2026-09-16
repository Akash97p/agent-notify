# Attention Request Contract (ARC)

Status: **public draft**, version `0.2`.

ARC is an open, transport-neutral JSON contract for the moments when a software agent needs human
attention. It standardizes how a producer creates, updates, and resolves a bounded attention request
— and, when the producer is waiting on a person, how the human answer comes back — without defining
how any of that is transported, stored, routed, or displayed.

AgentNotify defines ARC and is its first reference implementation. The specification is independent
of the AgentNotify product model: producers do not need to know about desktop toasts, SQLite,
delivery providers, or the AgentNotify CLI.

## Goals

- Let any agent request human attention without a provider-specific payload.
- Let a request state the shape of the answer it is waiting for, and carry that answer back.
- Distinguish immutable delivery events from a stable unresolved condition.
- Preserve sender, session, project, working-directory, and correlation identity.
- Make transport retries and condition updates deterministic.
- Keep the core small enough for hooks, stdout adapters, HTTP clients, and message buses.
- Minimize sensitive data and leave presentation and routing policy to the consumer.

## What changed in 0.2

0.2 closes the loop. 0.1 could only say that a person was needed; it had no way to say *what is
being asked* or to carry back *what the person said*. Both now exist:

- `request.response` on a created or updated request declares that the producer is waiting, and for
  what: a permission, one of a bounded list of choices, or a short text.
- `response.submitted` carries one human answer back to the producer.
- `request.outcome` on resolution records why a condition closed.
- `context` gained `turn_id` and `native_request_id`, the correlation a host adapter needs to return
  an accepted answer into the call that is blocking on it.

0.2 replaces 0.1 rather than extending it. A consumer that implements 0.2 rejects `"0.1"`, as the
versioning rule below requires. The superseded
[`arc-0.1.schema.json`](../src/AgentNotify.Protocol/Schemas/arc-0.1.schema.json) stays published so
its URL keeps resolving; it is no longer normative and AgentNotify no longer accepts it.

## Non-goals in 0.2

ARC 0.2 does not define agent activity telemetry, model traces, tool inputs or outputs, attachments,
authentication, discovery, network transport, broker storage, or outbound delivery.

It also does not define **multi-select answers** — `single_choice` means exactly one, and a consumer
rejects an answer carrying more — or **free-form messages from a person to an agent**. An ARC answer
is authorized by a specific request's digest, nonce, and first-wins state; an unsolicited message has
none of those, so it needs its own contract rather than a loosened version of this one.

The non-normative research behind this direction is
[Bidirectional agent communication](BIDIRECTIONAL_AGENT_COMMUNICATION.md). It keeps ARC as the
transport-neutral semantic contract and uses Agent Client Protocol and native host APIs as adapters.

## Event lifecycle

ARC 0.2 defines four event types:

| Event type | Meaning | Key rule |
| --- | --- | --- |
| `request.created` | Open a new attention request | `request.key` is optional |
| `request.updated` | Replace the visible state of an active request | `request.key` is required |
| `response.submitted` | Deliver one human answer to an answerable request | `request.key` is required |
| `request.resolved` | Close a request because attention is no longer needed | `request.key` is required |

A producer-assigned `event_id` identifies one immutable event. A `request.key` identifies a logical
condition that may span several events. They must not be treated as the same concept.

When `request.created` omits a key, a consumer derives an identity from `sender.id` and `event_id`.
Replaying that event returns the original result, including after it has been resolved. When a key
is supplied, the producer opts into the condition lifecycle: another created event with the same key
updates an active condition or creates a new condition after the old one is terminal.

`request.updated` never creates a missing condition. `request.resolved` is idempotent when the latest
matching condition is already resolved.

## Envelope

Every ARC event is one JSON object:

| Field | Type | Required | Meaning |
| --- | --- | --- | --- |
| `arc_version` | string | yes | Exactly `"0.2"` |
| `event_id` | string | yes | Producer event identity, 1–512 characters |
| `event_type` | string | yes | One of the four lifecycle types |
| `occurred_at` | RFC 3339 timestamp | yes | When the source occurrence happened |
| `sender` | object | yes | Producing agent identity |
| `context` | object | no | Bounded execution context |
| `request` | object | yes | Attention condition and content |
| `response` | object | on `response.submitted` | One human answer; forbidden on every other type |
| `extensions` | object | no | Namespaced `x-<vendor>` values |

Unknown core fields are rejected. Unknown correctly namespaced vendor extensions are ignored. This
lets extensions evolve without allowing misspelled core fields to silently change behavior.

### Sender

| Field | Required | Limit |
| --- | --- | --- |
| `id` | yes | Stable producer identifier, 1–100 characters |
| `name` | no | Human-readable name, up to 160 characters |
| `version` | no | Producer version, up to 100 characters |
| `instance_id` | no | Current process/run identity, up to 100 characters |

### Context

| Field | Meaning | Limit |
| --- | --- | --- |
| `session_id` | Agent session/run identity | 100 characters |
| `correlation_id` | Producer correlation or turn identity | 200 characters |
| `project` | Human-readable project name | 200 characters |
| `cwd` | Working directory | 1,024 characters |
| `pid` | Local process ID | Non-negative integer |
| `turn_id` | Native turn/generation identity | 200 characters |
| `native_request_id` | Opaque host request id an adapter needs to answer the live call | 200 characters |

Context values are correlation hints, not authentication claims. Consumers must not authorize an
action solely because a producer supplied a project, process ID, or working directory.

### Request

| Field | Required | Meaning |
| --- | --- | --- |
| `key` | updates/resolution only | Stable condition identity, up to 100 characters |
| `kind` | created/updated | Portable attention reason |
| `title` | no | Optional display title, up to 200 characters |
| `message` | created/updated | Human-readable request, 1–4,000 characters |
| `priority` | no | `low`, `normal`, `high`, or `critical` |
| `response` | no | Answer specification; its presence makes the request answerable |
| `outcome` | resolution only | Why the condition closed |

Request kinds and AgentNotify defaults are:

| ARC kind | Meaning | AgentNotify type | Default priority |
| --- | --- | --- | --- |
| `information` | Useful state that deserves visibility | `info` | normal |
| `question` | The agent needs an answer | `input_required` | high |
| `permission` | The agent needs authorization | `permission_required` | high |
| `blocked` | Work cannot progress | `blocked` | high |
| `failure` | Work failed and needs review | `error` | high |
| `completion` | Requested work completed | `completed` | normal |

Consumers own presentation policy. They may group, defer, suppress, escalate, or route a request
according to local configuration. Producers should choose the semantic kind and priority rather than
trying to dictate colors, sounds, or delivery channels.

### Answer specification

`request.response` is what makes a request *answerable*. Without it the request is one-way: it tells
a person something and nobody is blocked. With it, the producer states that it is waiting, and states
exactly what a valid answer looks like.

| Field | Required | Meaning |
| --- | --- | --- |
| `kind` | yes | `permission`, `single_choice`, or `text` |
| `choices` | choice kinds | 2–12 stable `{id, label, detail?}` entries |
| `text_max_length` | no | Answer bound for `text`: 1–2,000, default 500 |
| `expires_at` | yes | Absolute deadline after which the producer stops waiting |

`permission` and `single_choice` require `choices` and forbid `text_max_length`; `text` is the
reverse. A choice `id` is 1–64 characters of letters, digits, underscore, or hyphen, unique within
the request, and is echoed back verbatim — it is opaque to the consumer, which never interprets it.
A producer must only offer choices it can actually act on.

Presenting a request is not accepting it. A consumer that cannot render a kind should refuse the
request rather than silently degrade it to something the producer did not ask for.

### Outcome

`request.outcome` may accompany a resolution to say why the condition closed:

| Outcome | Meaning |
| --- | --- |
| `answered` | A response was accepted |
| `expired` | The deadline passed with no answer |
| `cancelled` | The producer withdrew the request |
| `superseded` | A newer request replaced this one |
| `not_needed` | Attention stopped being necessary for some other reason |

It is advisory. A resolution without an outcome is still valid, and a consumer must not infer
authorization from one.

## Responses

A `response.submitted` event carries one human answer. Its `request` object contains only `key`, and
the answer itself is the top-level `response`:

| Field | Required | Meaning |
| --- | --- | --- |
| `response_id` | yes | Responder-generated idempotency key, 1–128 characters |
| `digest` | yes | The digest the consumer bound to the request as displayed, 64 lowercase hex |
| `nonce` | when issued | Single-use secret the consumer issued with the request |
| `choice_id` | exactly one of | The selected option, for the choice kinds |
| `text` | these two | The answer text, for the `text` kind |
| `source` | no | Where the answer came from, 1–64 characters |
| `device_id` | no | Responding device, when known |

The **digest** and the **nonce** are both assigned by the consumer, not the producer: the producer
does not know, when it creates a request, how that request will be rendered or when it might change.
A consumer computes a digest over the canonical request it displayed, mints a single-use nonce,
issues both with the request, and requires every answer to echo them. The digest binds an answer to
the exact question a person saw; the nonce binds it to a request that responder actually received.
Neither is a secret to be logged or forwarded beyond the request envelope that carried it.

A consumer applies these rules in order, and the **first valid response wins**:

1. No such condition → reject as not found.
2. Already answered → reject as a conflict, unless `response_id` matches the accepted answer, in
   which case replay the original acceptance.
3. Not open — expired, cancelled, or superseded → reject.
4. `digest` does not match the current request → reject. The question changed after it was asked, so
   the answer cannot apply to it.
5. The consumer issued a `nonce` and the answer does not echo it → reject. A consumer that issues
   a nonce requires it on every answer, compared in constant time.
6. Wrong shape for the kind — an unknown `choice_id`, `text` over the bound, both fields, neither
   field → reject.

An accepted answer closes the condition. Every later answer to it is refused, including one already
in flight from another device.

Producers must not treat an unanswered request as an answer. When `expires_at` passes, control
returns to the producer, which decides what to do with no input; ARC never turns silence into
consent or refusal.

## Example: create a permission request

```json
{
  "arc_version": "0.2",
  "event_id": "evt_permission_018f",
  "event_type": "request.created",
  "occurred_at": "2026-08-26T01:15:00Z",
  "sender": {
    "id": "codex",
    "name": "Codex",
    "instance_id": "session-42"
  },
  "context": {
    "session_id": "session-42",
    "correlation_id": "turn-19",
    "project": "agent-notify",
    "cwd": "/work/agent-notify"
  },
  "request": {
    "key": "release-approval",
    "kind": "permission",
    "title": "Release approval required",
    "message": "May I run the release workflow?",
    "priority": "high",
    "response": {
      "kind": "permission",
      "choices": [
        { "id": "allow_once", "label": "Allow once", "detail": "Runs the release workflow once" },
        { "id": "deny", "label": "Deny" }
      ],
      "expires_at": "2026-08-26T01:25:00Z"
    }
  }
}
```

## Example: update the condition

```json
{
  "arc_version": "0.2",
  "event_id": "evt_permission_update_0190",
  "event_type": "request.updated",
  "occurred_at": "2026-08-26T01:17:00Z",
  "sender": { "id": "codex", "name": "Codex" },
  "request": {
    "key": "release-approval",
    "kind": "permission",
    "message": "The build passed. May I publish the release?",
    "priority": "high"
  }
}
```

## Example: submit the answer

```json
{
  "arc_version": "0.2",
  "event_id": "evt_permission_answer_0192",
  "event_type": "response.submitted",
  "occurred_at": "2026-08-26T01:19:00Z",
  "sender": { "id": "agentnotify", "name": "AgentNotify" },
  "request": { "key": "release-approval" },
  "response": {
    "response_id": "resp_7c1e",
    "digest": "9f2c0b7d4a1e6358be0c5d9a7f3128ea4b6d05c817f29a3e5b4c7d81026fa93b",
    "nonce": "kR3xP0qz7Nf4",
    "choice_id": "allow_once",
    "source": "desktop"
  }
}
```

## Example: resolve the condition

```json
{
  "arc_version": "0.2",
  "event_id": "evt_permission_resolved_0191",
  "event_type": "request.resolved",
  "occurred_at": "2026-08-26T01:20:00Z",
  "sender": { "id": "codex", "name": "Codex" },
  "request": { "key": "release-approval", "outcome": "answered" }
}
```

## AgentNotify HTTP binding

AgentNotify accepts ARC at its existing local event endpoint:

```text
POST http://127.0.0.1:47821/v1/events
Authorization: Bearer <local AgentNotify token>
Content-Type: application/json
```

The endpoint remains loopback-only, bearer-authenticated, limited to the configured 64 KiB request
body, and protected by the broker's POST rate limit.

Every accepted event returns the same envelope:

```json
{ "notification": { }, "interaction": { } }
```

`notification` is the local record a person sees. `interaction` is the durable question the producer
waits on, and is `null` unless the event was answerable. A newly persisted request returns `201`;
an update, an answer, a resolution, or an idempotent replay returns `200`. Updating, answering, or
resolving a missing condition returns `404`, and a second answer to an already answered condition
returns `409`.

An answerable `request.created` opens a notification **and** an interaction under the same key. The
producer then waits with `GET /v1/interactions/{id}/wait`, and the answer may arrive from the local
web interface, the CLI, a desktop toast, or a paired phone — whichever is first. `response.submitted`
is the same acceptance path expressed as an ARC event; it also resolves the notification half,
because an answered condition is no longer waiting for attention. Resolving a condition withdraws any
question still open under its key, so a producer that stops waiting never leaves a live prompt on
somebody's phone.

The broker rejects an answerable event whose question is invalid *before* it stores anything, so a
rejected question cannot leave a visible notification behind with nothing waiting on it.

See [Interactions](INTERACTIONS.md) for the interaction model, its own loopback routes, and the CLI.

The HTTP binding is an AgentNotify implementation choice, not part of the ARC base contract. Other
consumers may receive the same event through newline-delimited stdout, a local socket, a queue, or a
different authenticated HTTP endpoint.

## AgentNotify extension

AgentNotify recognizes one optional presentation hint:

```json
{
  "extensions": {
    "x-agentnotify": {
      "notification_type": "custom_type_id"
    }
  }
}
```

`notification_type` selects a configured AgentNotify type ID. Unknown fields inside
`x-agentnotify` are rejected. Other valid `x-<vendor>` extensions are ignored and are not copied
wholesale into notification metadata.

## Idempotency and ordering

- Producers must not reuse an `event_id` for a different event.
- Consumers should treat the same sender/event pair as a retry, not a new occurrence.
- Producers should send condition events in occurrence order.
- ARC 0.2 does not require globally ordered delivery or a distributed lock.
- A resolved keyed condition may later be created again as a new local lifecycle.
- An update targets only an active condition and must not silently create a replacement.
- A `response_id` is the responder's idempotency key: retrying the same answer returns the original
  acceptance, and reusing it for a different answer is a producer error, not a second vote.
- An update that changes what is being asked supersedes the open question rather than inheriting its
  answer. That is what the digest check enforces.

## Security and privacy

- ARC does not authenticate a producer or a responder. Every transport must provide its own trust
  boundary.
- Do not include API tokens, provider credentials, private keys, full prompts, or tool output.
- Send the smallest human-readable message necessary for a decision.
- Treat `cwd`, project, process, and sender identity as potentially sensitive metadata.
- Consumers must bound payloads, validate every core field, and avoid executing request content.
- A process ID or working directory is never sufficient authority to activate or control a process.
- A digest and a nonce bind an answer to a request; they do not authenticate whoever sent it. A
  transport that can read an answer in the clear can alter it, and the contract does not pretend
  otherwise.
- Never log or forward a nonce beyond the request envelope that carried it, and keep prompts,
  choices, answers, and digests out of logs.

AgentNotify never extends its local bearer token to an internet-facing API. Optional off-device
routes apply their configured content policy only after the request is stored locally.

## Schema and implementation

The normative machine-readable schema is
[`src/AgentNotify.Protocol/Schemas/arc-0.2.schema.json`](../src/AgentNotify.Protocol/Schemas/arc-0.2.schema.json).
GitHub Pages publishes the same file at
[`schemas/arc-0.2.schema.json`](https://akash97p.github.io/agent-notify/schemas/arc-0.2.schema.json).

`AgentNotify.Protocol` contains the portable ARC models, schema, native API DTOs, enums, and shared
JSON rules. It has no WPF, ASP.NET, SQLite, or provider dependencies. Projection into AgentNotify's
local model remains in the API layer.

## Versioning and conformance

ARC versions the wire contract with `arc_version`. Version `0.2` is a public draft and may change
before 1.0. Consumers must reject unsupported versions instead of guessing their meaning — including
`"0.1"`, which 0.2 supersedes.

Use these implementation claims:

> AgentNotify defines ARC 0.2 and implements its request creation, update, response, and resolution
> lifecycle.

> This producer emits ARC 0.2 events.

A conforming ARC 0.2 producer emits schema-valid events and follows the identity and lifecycle rules
above. A conforming consumer validates the envelope, implements all four event types, enforces the
answer rules in order and accepts at most one answer per condition, preserves idempotency, and does
not treat context fields as authorization. A consumer that implements only the three request types
is not conforming; it is an ARC 0.1 consumer, and must say so.
