# Attention Request Contract (ARC)

Status: **public draft**, version `0.1`.

ARC is an open, transport-neutral JSON contract for the moments when a software agent needs human
attention. It standardizes how a producer creates, updates, and resolves a bounded attention request
without defining how that event is transported, stored, routed, or displayed.

AgentNotify defines ARC and is its first reference implementation. The specification is independent
of the AgentNotify product model: producers do not need to know about desktop toasts, SQLite,
delivery providers, or the AgentNotify CLI.

## Goals

- Let any agent request human attention without a provider-specific payload.
- Distinguish immutable delivery events from a stable unresolved condition.
- Preserve sender, session, project, working-directory, and correlation identity.
- Make transport retries and condition updates deterministic.
- Keep the core small enough for hooks, stdout adapters, HTTP clients, and message buses.
- Minimize sensitive data and leave presentation and routing policy to the consumer.

## Non-goals in 0.1

ARC 0.1 does not define agent activity telemetry, model traces, tool inputs or outputs, attachments,
authentication, discovery, network transport, broker storage, or outbound delivery. It also does not
yet define structured human responses. AgentNotify will add response events only after its UI and
agent callback boundary can implement and verify them end to end.

The non-normative research and implementation plan for that next step is
[Bidirectional agent communication](BIDIRECTIONAL_AGENT_COMMUNICATION.md). The plan keeps ARC as the
transport-neutral semantic contract, uses Agent Client Protocol and native host APIs as adapters,
and requires separate submitted, accepted, expired, cancelled, and host-acceptance state. Nothing in
that plan changes ARC 0.1 conformance.

## Event lifecycle

ARC 0.1 defines three event types:

| Event type | Meaning | Key rule |
| --- | --- | --- |
| `request.created` | Open a new attention request | `request.key` is optional |
| `request.updated` | Replace the visible state of an active request | `request.key` is required |
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
| `arc_version` | string | yes | Exactly `"0.1"` |
| `event_id` | string | yes | Producer event identity, 1–512 characters |
| `event_type` | string | yes | One of the three lifecycle types |
| `occurred_at` | RFC 3339 timestamp | yes | When the source occurrence happened |
| `sender` | object | yes | Producing agent identity |
| `context` | object | no | Bounded execution context |
| `request` | object | yes | Attention condition and content |
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

## Example: create a permission request

```json
{
  "arc_version": "0.1",
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
    "priority": "high"
  }
}
```

## Example: update the condition

```json
{
  "arc_version": "0.1",
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

## Example: resolve the condition

```json
{
  "arc_version": "0.1",
  "event_id": "evt_permission_resolved_0191",
  "event_type": "request.resolved",
  "occurred_at": "2026-08-26T01:20:00Z",
  "sender": { "id": "codex", "name": "Codex" },
  "request": { "key": "release-approval" }
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
body, and protected by the broker's POST rate limit. A newly persisted request returns `201
NotificationDto`; an update, resolution, or idempotent replay returns `200 NotificationDto`. Updating
or resolving a missing condition returns `404`.

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
- ARC 0.1 does not require globally ordered delivery or a distributed lock.
- A resolved keyed condition may later be created again as a new local lifecycle.
- An update targets only an active condition and must not silently create a replacement.

## Security and privacy

- ARC does not authenticate a producer. Every transport must provide its own trust boundary.
- Do not include API tokens, provider credentials, private keys, full prompts, or tool output.
- Send the smallest human-readable message necessary for a decision.
- Treat `cwd`, project, process, and sender identity as potentially sensitive metadata.
- Consumers must bound payloads, validate every core field, and avoid executing request content.
- A process ID or working directory is never sufficient authority to activate or control a process.

AgentNotify never extends its local bearer token to an internet-facing API. Optional off-device
routes apply their configured content policy only after the request is stored locally.

## Schema and implementation

The normative machine-readable schema is
[`src/AgentNotify.Protocol/Schemas/arc-0.1.schema.json`](../src/AgentNotify.Protocol/Schemas/arc-0.1.schema.json).
GitHub Pages publishes the same file at
[`schemas/arc-0.1.schema.json`](https://akash97p.github.io/agent-notify/schemas/arc-0.1.schema.json).

`AgentNotify.Protocol` contains the portable ARC models, schema, native API DTOs, enums, and shared
JSON rules. It has no WPF, ASP.NET, SQLite, or provider dependencies. Projection into AgentNotify's
local model remains in the API layer.

## Versioning and conformance

ARC versions the wire contract with `arc_version`. Version `0.1` is a public draft and may change
before 1.0. Consumers must reject unsupported versions instead of guessing their meaning.

Use these implementation claims:

> AgentNotify defines ARC 0.1 and implements its request creation, update, and resolution lifecycle.

> This producer emits ARC 0.1 events.

A conforming ARC 0.1 producer emits schema-valid events and follows the identity and lifecycle rules
above. A conforming consumer validates the envelope, implements all three event types, preserves
idempotency, and does not treat context fields as authorization.
