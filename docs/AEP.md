# AEP Human Attention profile

Status: **experimental implementation profile**, version `0.1`.

AgentNotify consumes a focused subset of the public
[Agent Event Protocol (AEP) 0.1 draft](https://github.com/agentapprove/agent-event-protocol).
The upstream draft already existed when AgentNotify began documenting an event protocol, and it
already defines the two events this project needs most: `notification.sent` and `question.asked`.
AgentNotify therefore adopts that vocabulary instead of publishing a second incompatible protocol
with the same name.

This document specifies the **AgentNotify Human Attention profile**: the fields AgentNotify accepts,
the deterministic mapping into its notification lifecycle, and the `x-agentnotify` extension. It is
not a claim of full AEP consumer conformance. The upstream draft remains the authority for the base
envelope; this profile remains the authority for AgentNotify's ingestion behavior.

## Goals

- Give coding agents one vendor-neutral event envelope for human attention.
- Preserve agent, session, project, correlation, and unresolved state in AgentNotify.
- Make retried event delivery idempotent without requiring a producer-specific adapter.
- Keep transport, authentication, storage, routing, and presentation outside the base event schema.
- Leave room for ACP stream adapters, provider-native hooks, and future A2A bridges without changing
  the stored notification model.

## Scope

AgentNotify `0.0.3-alpha.1` accepts these AEP 0.1 event types:

| AEP event | Required content entry | AgentNotify default |
| --- | --- | --- |
| `notification.sent` | `type: notification` | `info`, normal priority |
| `question.asked` | `type: question` | `input_required`, high priority |

Other AEP event families are rejected with `400` at this endpoint. They may be added later when a
clear human-attention mapping exists. Rejecting unsupported types is intentional: turning every tool
call, model delta, or session transition into a notification would create noise and defeat the
product's purpose.

The profile does not define hook control responses, approval callbacks, streaming, attachments,
remote transport, or agent-to-agent communication. Those remain upstream AEP concerns or future
AgentNotify capabilities.

## Endpoint

```text
POST http://127.0.0.1:47821/v1/events
Authorization: Bearer <local AgentNotify token>
Content-Type: application/json
```

The endpoint is loopback-only, bearer-authenticated, subject to the configured 64 KiB request limit,
and shares the broker's POST rate limit. A new event returns `201 NotificationDto`. A replay using
the profile-derived event key returns the original projection as `200 NotificationDto`, including
after that notification is resolved or dismissed. Replays do not create another route delivery.

## Required envelope

| Field | Requirement |
| --- | --- |
| `aep_version` | Exactly `"0.1"` |
| `id` | Non-empty producer event ID, at most 512 characters |
| `type` | `notification.sent` or `question.asked` |
| `time` | RFC 3339 timestamp |
| `agent.slug` | Non-empty producer identifier, at most 100 characters |
| `content` | Contains a non-empty entry matching the event type; text is at most 4,000 characters |

AgentNotify tolerates omitted optional AEP fields and unknown top-level fields. Unknown vendor
extensions under `extensions.x-*` are ignored. Producers should omit values they do not know rather
than sending `null`, following the upstream draft.

## AgentNotify extension

Optional projection hints live under `extensions.x-agentnotify`:

| Field | Type | Meaning |
| --- | --- | --- |
| `title` | string | Notification title, 1–200 characters |
| `notification_type` | string | Built-in or configured AgentNotify type ID |
| `priority` | string | `low`, `normal`, `high`, or `critical` |
| `key` | string | Logical deduplication key, at most 100 characters |
| `project` | string | Project display name, at most 200 characters |
| `pid` | integer | Non-negative local agent process ID |

The extension is optional. A producer can emit valid upstream AEP without knowing AgentNotify.
Unknown fields inside `x-agentnotify` are rejected by the published profile schema so mistakes do not
silently change routing behavior.

## Deterministic projection

AgentNotify maps an accepted event as follows:

| Notification field | Source |
| --- | --- |
| `agent` | `agent.slug` |
| `agentInstance` | `agent.instance_id`, then `session.id` |
| `project` | `x-agentnotify.project`, then the final segment of `workspace.project_path` or `workspace.cwd` |
| `cwd` | `workspace.cwd` |
| `message` | Matching `content[].text` |
| `title` | `x-agentnotify.title`, otherwise a type-specific title derived from the agent display name |
| `type` | `x-agentnotify.notification_type`, otherwise the event default above |
| `priority` | `x-agentnotify.priority`, otherwise the event default above |
| `pid` | `x-agentnotify.pid` |
| `key` | `x-agentnotify.key`, otherwise a stable SHA-256 key derived from `agent.slug` and event `id` |

The original AEP version, event ID, event type, timestamp, hook name, conversation ID, and turn ID are
stored as bounded notification metadata. The complete source envelope is not stored: tool inputs,
model output, vendor extensions, and other unrelated data may contain secrets and are outside this
attention profile.

The derived key treats the upstream event as immutable and makes its ID idempotent across the full
local history. Supplying `x-agentnotify.key` opts into AgentNotify's condition lifecycle instead: a
different event with the same key updates the active condition, and a later event can create a new
condition after the old one has been resolved.

## Example

```json
{
  "aep_version": "0.1",
  "id": "evt_permission_018f",
  "type": "question.asked",
  "time": "2026-08-26T01:15:00Z",
  "agent": {
    "slug": "codex",
    "display_name": "Codex",
    "instance_id": "session-42"
  },
  "session": {
    "conversation_id": "conversation-7",
    "turn_id": "turn-19"
  },
  "workspace": {
    "cwd": "/work/agent-notify",
    "project_path": "/work/agent-notify"
  },
  "content": [
    {
      "type": "question",
      "text": "May I run the release workflow?",
      "style": "plain_text"
    }
  ],
  "extensions": {
    "x-agentnotify": {
      "title": "Release approval required",
      "notification_type": "permission_required",
      "priority": "high",
      "key": "release-approval"
    }
  }
}
```

## Schema and .NET model

The source schema is
[`src/AgentNotify.Protocol/Schemas/aep-agentnotify-profile-0.1.schema.json`](../src/AgentNotify.Protocol/Schemas/aep-agentnotify-profile-0.1.schema.json).
GitHub Pages publishes the same file at
[aep-agentnotify-profile-0.1.schema.json](https://akash97p.github.io/agent-notify/schemas/aep-agentnotify-profile-0.1.schema.json).

`AgentNotify.Protocol` is the cross-platform .NET assembly containing the AEP envelope, profile
extension, notification DTOs, enums, and shared JSON rules. It has no WPF, ASP.NET, SQLite, or
provider dependencies.

## Security and privacy

- AEP does not replace AgentNotify authentication. Every `/v1/events` request requires the local
  bearer token, and the API remains bound to loopback.
- Never put bearer tokens, provider credentials, private keys, or full tool input/output in content
  or `x-agentnotify`.
- Agents should send the minimum human-readable question or result needed for action. Outbound
  routes apply their configured redaction after local persistence.
- `id` provides deduplication, not authentication, authorization, or replay expiry.
- Unknown event types are rejected; unknown vendor extensions are ignored and never copied wholesale
  into notification metadata.

## Versioning and conformance language

Both upstream AEP and this profile are pre-1.0 drafts. Breaking changes are possible. Producers must
send the exact `aep_version` they target, and AgentNotify rejects versions it does not implement.

Use this precise project claim:

> AgentNotify implements the experimental AEP 0.1 Human Attention profile for
> `notification.sent` and `question.asked` ingestion.

Do not claim that AgentNotify defines AEP, implements every AEP event, or implements AEP control-hook
responses. Track upstream draft changes before expanding the supported profile.

## Adapter roadmap

The envelope is transport-neutral. Current producers can use direct HTTP or the CLI. Future input
adapters may translate provider hooks, newline-delimited stdout, the Agent Client Protocol (ACP), or
A2A events into this profile. An adapter must preserve event identity, apply the same payload bounds,
and pass through the same authenticated ingestion and persistence path; it must not bypass validation
or write directly to SQLite.
