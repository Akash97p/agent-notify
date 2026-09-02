# Bidirectional agent communication

Status: **research and implementation plan**, not a shipped compatibility claim.

Research checked: **2026-09-03**. Agent APIs and draft protocols can change; adapters must pin and
test the versions they support.

## Decision summary

AgentNotify should add bidirectional interaction as a new durable broker capability, while keeping
ARC and the local AgentNotify database as the product's source of truth.

The recommended design has two integration modes:

1. **Direct/native adapters** use a coding agent's supported hooks, plugin API, SDK, gateway, or RPC
   surface. This is the only practical way to preserve an already-running terminal or editor
   session.
2. **Managed Agent Client Protocol sessions** let an AgentNotify bridge start an ACP-capable coding
   agent as a child process, act as its client, receive permission and elicitation requests, and
   return the human response over the same JSON-RPC session.

Do not rebuild AgentNotify on Agent Communication Protocol/A2A, either project named Agent Event
Protocol, or MCP. Those standards solve useful adjacent problems, but none replaces AgentNotify's
durable human-attention lifecycle, local history, relay routing, and mobile response security.

The practical topology is:

```text
existing terminal/editor session                 AgentNotify-managed session
        |                                                    |
native hook / plugin / SDK                          Agent Client Protocol
        |                                                    |
        +--------------- AgentNotify host adapter -----------+
                                  |
                    local broker + SQLite source of truth
                         /                         \
          desktop decision UI                 durable relay outbox
                                                    |
                                           Relay + mobile UI
                                                    |
                                      sealed response envelope
                                                    |
                         interaction broker completes one waiter
                                                    |
                           host adapter returns the native answer
```

This is event-driven at the agent boundary, but it is also durable. The adapter owns a live waiter;
SQLite owns the request and response record; relay delivery is allowed to disconnect and resume.
The language model itself does not need to subscribe to a message bus.

## Why a skill or ordinary tool is insufficient

A skill can teach a model to call `agentnotify` before it waits, but a skill is prompt context. It
cannot observe a host-native permission dialog, keep a blocked runtime call open, or inject a
decision into that dialog.

An MCP or custom `ask_human` tool is useful when the model voluntarily calls it. It does not capture
permissions enforced by the coding-agent host around shell commands, file writes, MCP calls, or
other tools. MCP elicitation can gather structured input while an MCP request is active, but it does
not take ownership of another host's approval mechanism.

A real integration therefore needs one of these host-owned control surfaces:

- a synchronous permission hook;
- a plugin/extension API that can list and resolve pending requests;
- a bidirectional SDK or gateway session;
- an RPC protocol whose client answers UI requests; or
- Agent Client Protocol, with AgentNotify acting as the client.

Skills and MCP remain useful installation and fallback surfaces. They are not the control plane.

## The protocol layers

AgentNotify needs to keep three different concerns separate.

| Layer | Responsibility | Planned owner |
| --- | --- | --- |
| Host adapter | Translate a vendor's live permission/question request and return its answer | Cross-platform AgentNotify bridge |
| Interaction contract | Stable identity, choices, expiry, state, idempotency, and response outcome | ARC next-version work in `AgentNotify.Protocol` |
| Remote transport | Deliver sealed requests/responses, reconnect, backfill, and acknowledge transport | AgentNotify Relay and mobile app |

The relay transport should not contain Codex-, Claude-, or Cursor-specific payloads. The WPF app
should not embed every vendor SDK. Adapters translate at the edge into one bounded AgentNotify
interaction model.

## Planned interaction model

The following is the recommended model for design and migration work. Names are not normative until
the ARC schema and API branch is implemented.

### Request identity and state

| Field | Purpose |
| --- | --- |
| `interaction_id` | AgentNotify-assigned stable interaction identity |
| `request_event_id` | Immutable producer event/correlation identity |
| `agent_session_id` | Native session identity |
| `turn_id` | Native turn/generation identity when available |
| `native_request_id` | Opaque ID required to answer the host request |
| `kind` | `permission`, `single_choice`, `multi_choice`, `text`, or bounded `form` |
| `prompt` | Exact human-readable question or permission explanation |
| `choices` | Stable choice IDs, labels, and optional semantics |
| `allowed_decisions` | The exact choices the host currently accepts |
| `expires_at` | Absolute response deadline |
| `status` | `pending`, `answered`, `expired`, `cancelled`, or `superseded` |
| `request_digest` | Digest binding the displayed request to an eventual response |

Permission choices should preserve host semantics such as `allow_once`, `allow_session`,
`allow_always`, `deny_once`, or `deny_always`. An adapter must never advertise or synthesize a
scope the host did not offer.

### Response and outcome

A mobile tap is not yet proof that the coding agent accepted the decision. Track these states
separately:

```text
submitted by human -> accepted by AgentNotify -> accepted by host
                                      \-> rejected as stale/mismatched
```

Likewise, keep delivery state separate from interaction state:

```text
delivered != viewed != answered != accepted by host
```

The response record should contain the interaction ID, request digest, selected stable choice IDs or
bounded text/form data, responder device, response time, idempotency key, and host acceptance
outcome. Suggested future events include response submitted/accepted/rejected and request
expired/cancelled/superseded; exact ARC event names remain an implementation decision.

### Adapter capabilities

Every adapter should declare capabilities rather than relying on product-name assumptions:

- permission decisions and the scopes it can faithfully return;
- single choice, multiple choice, bounded text, or structured form input;
- request cancellation and timeout behavior;
- reconnect/backfill support;
- whether it owns a managed session or attaches to an existing one;
- whether answers survive a desktop or adapter restart; and
- whether the host can confirm that it accepted the answer.

## Broker and API direction

The first implementation should add portable domain and storage boundaries before any vendor
adapter:

- versioned SQLite migrations for interactions, choices, responses, and host acceptance;
- a single interaction broker implementing first-valid-response-wins;
- authenticated loopback create/list/respond/cancel routes;
- local desktop UI for permission and single-choice requests;
- durable publication to Relay after local persistence;
- an adapter connection that can wait asynchronously and be cancelled; and
- status projection into the existing notification/history view without making a notification row
  the only copy of the interaction data.

The existing notification lifecycle remains valuable for visibility, deduplication, and history.
Interaction state needs its own tables because an answer, expiry, and host acceptance are not merely
notification statuses.

## Relay and mobile return path

WebSocket, server-sent events, long polling, and MQTT are transports, not the semantic contract. A
reasonable first design is:

1. mobile submits a sealed response to Relay over authenticated HTTPS;
2. Relay stores the opaque response durably and acknowledges transport acceptance;
3. desktop receives a wake-up over an authenticated WebSocket/SSE channel or discovers the response
   through bounded polling/backfill;
4. desktop decrypts and validates it, persists it idempotently, and asks the local interaction
   broker to complete the waiter; and
5. the adapter maps the result into the host's native response and records accepted/rejected.

Always keep a polling/backfill path even if WebSocket is the normal low-latency channel. A dropped
socket must not lose a permission decision. MQTT may be an optional self-hosted binding later, but
making it the only response path would couple the product contract to one broker deployment.

## Security requirements

Remote permission responses are authorization messages. They require a stronger boundary than an
ordinary notification acknowledgement.

- Seal and authenticate response content end to end for the intended installation. Protect the
  installation private key with the platform secret store.
- Cryptographically bind a response to the installation, agent session, turn/native request,
  request digest, expiry, and a single-use nonce.
- Accept at most one valid response. Duplicate transport delivery must be idempotent.
- Reject expired, cancelled, superseded, wrong-session, wrong-installation, and digest-mismatched
  responses.
- Never represent a remote response as a shell command, URL action, or arbitrary callback payload.
  It is only a choice or bounded input defined by the pending request.
- Display the exact command, path, destination, tool arguments, and scope that the host supplied,
  subject only to explicit secret redaction. Do not approve an invisible broader action.
- Start with allow-once/deny and bounded selection. Persistent or session-wide grants should ship
  only when the native host defines and enforces the same scope.
- Default to deny or the ordinary desktop prompt on timeout/adapter failure according to explicit
  user policy. Never auto-allow because Relay or mobile is unavailable.
- Keep local desktop response available and make first-valid-response-wins deterministic when local
  and mobile answers race.
- Keep Relay transport acknowledgement distinct from desktop decryption, user view, submitted
  response, and host acceptance.
- Redact prompts, command arguments, paths, and answers from logs by default. Retain only the minimum
  bounded audit metadata selected by the user.

Required failure tests include duplicate response, replay, stale response, wrong installation,
wrong session/turn, changed request digest, offline Relay, desktop restart, adapter crash, host
rejection, timeout, and simultaneous local/mobile responses.

## Standards assessment

### ARC — keep as AgentNotify's semantic contract

ARC 0.1 already models durable attention request create/update/resolve state and intentionally
excludes structured answers. The next ARC work should add a versioned interaction/response profile
only after the local UI, persistence, adapter callback, Relay, and mobile path can be tested end to
end. ARC should stay transport-neutral.

### Agent Client Protocol — adopt as the common managed-session adapter

[Agent Client Protocol](https://agentclientprotocol.com/get-started/architecture) is the most direct
standard for this problem. It connects coding agents to clients over bidirectional JSON-RPC, usually
stdio; the client starts the agent process and can answer server-initiated requests. Stable protocol
version 1 defines [`session/request_permission`](https://agentclientprotocol.com/protocol/v1/tool-calls#requesting-permission)
with stable option IDs and allow/reject semantics. Newer capability-negotiated work also covers
elicitation.

AgentNotify should implement an ACP **client**, not redefine ACP. The bridge can normalize ACP
permission/elicitation calls into AgentNotify interactions and return the selected option on the
original JSON-RPC request.

Important limitation: ACP normally gives the client ownership of a subprocess. It is not a generic
way to attach silently to an arbitrary TUI or editor session. That is why native adapters remain
necessary.

### Agent Communication Protocol and A2A — adjacent agent-to-agent layer

The similarly named [Agent Communication Protocol](https://agentcommunicationprotocol.dev/introduction/welcome)
is a REST-oriented agent interoperability protocol and is now part of
[A2A](https://a2a-protocol.org/latest/specification/) under the Linux Foundation. A2A can represent
long-running tasks, `input-required` states, streaming, polling, and push notifications. Its primary
abstraction is a client agent communicating with a remote agent, not a local coding host handing a
specific permission waiter to a human.

An A2A projection may be useful later for fleet or multi-agent integrations. It should not replace
ARC, the host adapter, or the Relay response design.

### MCP elicitation — useful optional input surface

[MCP elicitation](https://modelcontextprotocol.io/specification/draft/client/elicitation) lets an MCP
server request form or URL input while processing a client request, with accept/decline/cancel
responses and a restricted JSON Schema form model. AgentNotify can expose or consume this after its
interaction model stabilizes. It cannot intercept a coding host's unrelated shell/file approval.

### Agent Approve Agent Event Protocol — borrow the event vocabulary

[Agent Approve's AEP draft](https://www.agentapprove.com/standards/agent-event-protocol) normalizes
coding-agent events and hook control responses. Its `action.requested`, `question.asked`,
`question.answered`, stable action correlation, mapping files, and allow/deny/ask/defer vocabulary
are directly useful design references.

It is a public 0.1 review draft. The published format does not by itself provide AgentNotify's local
storage model, Relay encryption, device identity, replay defense, or offline delivery boundary.
Borrow concepts and consider an import/export adapter; do not make it AgentNotify's internal source
of truth.

### agenteventprotocol organization — promising but pre-release

The separate [agenteventprotocol](https://github.com/agenteventprotocol) project defines activity,
attention, control, and replay across stdio/HTTP/SSE/WebSocket. Its reference stack demonstrates an
`attention.requested` to authenticated control response round trip, command acknowledgement,
deduplication, `(epoch, seq)` replay, and adapters for several coding agents.

This is the closest external prototype to the complete AgentNotify idea, but its own status is
pre-release v0.1 with parts still draft. Study its conformance fixtures, replay model, control
acknowledgements, and adapters. Avoid a core dependency until the standard and packages stabilize.

## Coding-agent feasibility

Ratings describe the quality of a truthful bidirectional integration, not the general quality of
the coding agent.

| Coding agent | Feasibility | Best first integration | Important constraint |
| --- | --- | --- | --- |
| Codex | Excellent | Native synchronous hooks for existing sessions; app-server for managed sessions | Background hooks cannot decide a live request; app-server must remain locally authenticated |
| Claude Code | Excellent | `PermissionRequest`/`PreToolUse` hooks or Agent SDK | Use the host's exact decision and question schemas; blocking handler owns the waiter |
| OpenCode | Excellent | Plugin + SDK permission list/get/reply; ACP where available | Pin/test current API; reconcile pending requests after reconnect |
| Kilo Code | Excellent | Plugin event/permission API or `kilo acp` | CLI and editor share concepts, but test each surface and version |
| Hermes | Excellent | Native approval transport plugin | Best security match: immutable request, ID/digest binding, allowed choices, bounded timeout |
| OpenClaw | Excellent | Gateway operator client with approvals scope | Treat canonical command/cwd/session plan as authoritative; backfill pending approvals |
| Gemini CLI | High | Managed `gemini --acp` | Notification hook observes permission prompts but cannot answer them |
| Muse Code | High, preview | Official SDK approval and user-input streams | Developer Preview, pre-1.0; pin schema/SDK and expect change |
| Pi coding agent/harness | High | Extension + RPC extension-UI protocol | Pi intentionally has no universal built-in permission policy; AgentNotify extension supplies it |
| GitHub Copilot CLI | Excellent | Native hooks for existing sessions; SDK/ACP for managed sessions | ACP is public preview; cloud sessions may treat ask as deny/no-user |
| Cursor | High | Managed CLI ACP; native hooks for allow/deny policy | Native `preToolUse` accepts `ask` in schema but does not enforce it today |
| Roo Code | Low / legacy | Source fork only | The official extension was shut down; do not make it a primary target |

### Codex

Official [Codex hooks](https://developers.openai.com/codex/hooks) include synchronous
`PermissionRequest` and lifecycle events. A direct AgentNotify hook can forward a request, block
within the host timeout, and return the native allow/deny/abstain decision. Async hooks are suitable
for notifications, not for approving the already-waiting call.

For a managed experience, [Codex app-server](https://developers.openai.com/codex/app-server) is a
bidirectional JSON-RPC interface with streamed events, server-initiated approvals and user-input
requests, turn steering, and interruption. It is the strongest Codex adapter surface. Use stdio or
authenticated loopback/Unix transport; the official documentation labels WebSocket transport
experimental and warns against unauthenticated remote exposure.

The community [`codex-acp`](https://github.com/agentclientprotocol/codex-acp) adapter is another
managed-session option, but the official app-server should be preferred when its contract covers
the required behavior.

### Claude Code

[Claude Code hooks](https://code.claude.com/docs/en/hooks) expose `PermissionRequest`, `PreToolUse`,
and question-related tool data with decision control. They run across terminal and supported editor
surfaces. The [Agent SDK user-input API](https://code.claude.com/docs/en/agent-sdk/user-input) offers
blocking permission/question callbacks and is the better managed-session surface.

A native adapter can preserve an ordinary Claude session. An SDK adapter can provide richer
multi-choice/free-text behavior and explicit defer/resume where supported.

### OpenCode

[OpenCode plugins](https://opencode.ai/v2/docs/build/plugins/) can subscribe to
`permission.asked`/`permission.replied`; the SDK can list, get, and reply to pending permission
requests with stable request IDs. Its built-in
[question tool](https://opencode.ai/docs/tools/#question) supports selectable choices and custom
answers. This is a strong direct adapter surface, and an ACP server can provide the managed path
when supported by the installed release.

Pending/reconnect behavior is especially important: a plugin event alone is not durable. On startup
or reconnect, list pending requests and reconcile them with AgentNotify SQLite before accepting an
answer.

### Kilo Code

[Kilo plugins](https://kilo.ai/docs/automate/extending/plugins) work in the CLI and VS Code
extension, expose permission events/control hooks, and can add tools. The CLI also provides
[`kilo acp`](https://kilo.ai/docs/code-with-ai/platforms/cli-reference#kilo-acp). This supports both
direct and managed modes with a largely shared adapter implementation.

### Hermes

[Hermes approval transports](https://hermes-agent.nousresearch.com/docs/user-guide/features/plugins#approval-transports)
are almost exactly the AgentNotify adapter abstraction. A plugin registers a presentation transport
for an existing approval; Hermes supplies an immutable redacted request, host timeout, allowed
choices, and opaque request ID/digest. Stale, changed, unbound, or unsupported-scope responses are
rejected, and transport failure denies unless the user explicitly selects built-in fallback.

Implement a thin Python plugin that sends the normalized interaction to the local broker and waits
for the broker result. Keep final authorization, hard blocks, and grant persistence in Hermes.

### OpenClaw

[OpenClaw exec approvals](https://github.com/openclaw/openclaw/blob/main/docs/tools/exec-approvals.md)
already use a gateway event/control loop: `exec.approval.requested`, list/backfill, and
`exec.approval.resolve`. A
[Gateway client](https://docs.openclaw.ai/gateway/clients) with `operator.approvals` can display and
resolve pending approvals and reconcile live events with the pending list by approval ID.

AgentNotify should be a Gateway operator client and preserve OpenClaw's canonical command, cwd,
session, and execution plan rather than reconstructing them from display text.

### Gemini CLI

[Gemini CLI ACP mode](https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/acp-mode.md)
runs `gemini --acp` as a JSON-RPC stdio server and makes the client responsible for session control.
This is the clean managed integration. Gemini's
[Notification hook](https://github.com/google-gemini/gemini-cli/blob/main/docs/hooks/reference.md#notification)
can observe `ToolPermission`, but its documentation explicitly says it cannot grant permissions or
block the alert. Do not claim full direct-session response support from that hook.

### Muse Code

The [Muse Code SDK](https://github.com/meta-models/muse-code-sdk) exposes programmatic control and a
schema-driven event stream. Current schemas include approval requested/list-pending/decide/update/
resolved and user-input requested/answer/cancel/clarify flows. Particularly useful patterns are the
requirement/request ID guard, separate command acknowledgement and resolved events, and reissuing
pending requirements after reconnect.

The SDK is explicitly a pre-1.0 Developer Preview with no stability promise. It is a strong design
reference and adapter candidate, but versions must be pinned and contract-tested.

### Pi coding agent / harness

This plan assumes “Pi harness” means the Pi coding agent. Pi deliberately leaves policy to
extensions. Its [extension API](https://pi.dev/docs/latest/extensions) can intercept `tool_call`
before execution and can block; `ctx.ui` provides confirmation, selection, and input. In
[RPC mode](https://pi.dev/docs/latest/rpc), those dialogs become blocking
`extension_ui_request`/`extension_ui_response` messages with matching IDs.

Build a small AgentNotify extension and run Pi in RPC mode for the managed path. Do not describe it
as a built-in Pi-wide permission system.

### GitHub Copilot CLI

[Copilot CLI hooks](https://docs.github.com/en/copilot/reference/hooks-reference) can control
`preToolUse` and expose permission/user-input lifecycle events. The
[Copilot SDK](https://github.com/github/copilot-sdk/blob/main/docs/features/streaming-events.md)
provides `permission.requested`, `user_input.requested`, and `elicitation.requested` events with
response methods. Its [ACP server](https://docs.github.com/en/copilot/reference/copilot-cli-reference/acp-server)
provides a common managed path, currently in public preview.

### Cursor

[Cursor hooks](https://prod.cursor.com/docs/hooks) can observe and allow/deny native tool execution,
but the current documentation says `ask` is accepted by the `preToolUse` schema and not enforced.
That makes hooks useful for policy and notification, not a complete remote question UI.

[Cursor CLI ACP](https://prod.cursor.com/docs/cli/acp) is the stronger bidirectional surface. It
uses stdio JSON-RPC, sends `session/request_permission` with allow-once/allow-always/reject-once,
and has blocking Cursor extension methods such as `cursor/ask_question`.

### Roo Code

The [Roo Code repository](https://github.com/RooCodeInc/Roo-Code) states that the official extension
was shut down on May 15 and points users to successors/forks. Preserve the research note, but do
not spend first-party adapter effort on an inactive distribution. A fork can reuse the generic
extension or protocol work later.

## Implementation sequence

Each phase should be a separate topic branch and must preserve a working local-only AgentNotify.

1. **Interaction domain and storage** — finalize the ARC-compatible model, schema migration,
   idempotency, expiry/cancellation, response acceptance, and loopback API.
2. **Local desktop interaction UI** — permission and single-choice cards, exact detail display,
   timeout, race handling, and accessible keyboard behavior.
3. **Generic Agent Client Protocol bridge** — a separate cross-platform bridge process/package that
   owns ACP subprocesses and maps permissions/elicitation to the local broker.
4. **First direct adapters** — Hermes (best reference design), Claude Code, Codex, and Copilot CLI;
   add OpenCode/Kilo together where their shared API permits.
5. **Relay reverse envelopes** — authenticated response submission, durable response queue,
   reconnect/backfill, E2E request binding, and desktop ingestion.
6. **Mobile response UI** — allow/deny and single choice first; show expiry and host-acceptance
   result; keep persistent grant scopes out of the first release.
7. **Additional adapters** — OpenClaw, Gemini ACP, Pi RPC, Cursor ACP, Muse SDK, then lower-priority
   or community-maintained hosts.
8. **Optional standards projections** — MCP elicitation, AEP import/export, and A2A exposure only
   after the native interaction contract is stable.

TypeScript is the likely best language for the standalone bridge because ACP has an official
TypeScript SDK and OpenCode, Kilo, Pi, OpenClaw, Muse, and Copilot offer TypeScript-friendly
surfaces. Hermes can remain a thin Python plugin. The portable .NET broker owns durable state and
policy; WPF owns Windows presentation only.

## Open design decisions

- Whether response events become ARC 0.2 core events or a separately versioned ARC interaction
  profile.
- Whether the bridge is distributed inside AgentNotify archives or as a separate npm/package
  artifact with a small launcher.
- The first local adapter IPC: authenticated loopback HTTP plus long poll, named pipe/Unix socket,
  or a broker-owned WebSocket. The semantic and persistence model should not depend on this choice.
- How long a response remains visible after the native host cancels or supersedes it.
- Which exact host details may leave the computer under the user's route redaction policy.
- Whether mobile can answer only requests from installations it has explicitly trusted for control,
  distinct from installations allowed to send notifications.

## Repository and licensing note

The owner reports that the local Relay and Android mobile repositories were tested successfully
end to end on 2026-09-03. This documentation branch did not repeat that device test.

- `agent-notify` is MIT-licensed.
- [`agent-notify-relay`](https://github.com/Akash97p/agent-notify-relay) is public and MIT-licensed.
- `agent-notify-relay-mobile` is currently publicly visible but carries a proprietary licence that
  grants no general permission to use, copy, modify, or distribute it.

Repository visibility and copyright licence are separate. Public visibility does not make the
mobile source open source. Conversely, copies already distributed under MIT retain the rights the
MIT licence grants; changing repository visibility later does not retract those existing grants.
This is a project record, not legal advice.

## Primary research sources

Standards:

- [Agent Client Protocol architecture](https://agentclientprotocol.com/get-started/architecture)
- [Agent Client Protocol permission requests](https://agentclientprotocol.com/protocol/v1/tool-calls#requesting-permission)
- [Agent Client Protocol repository and versioning](https://github.com/agentclientprotocol/agent-client-protocol)
- [Agent Communication Protocol](https://agentcommunicationprotocol.dev/introduction/welcome)
- [A2A specification](https://a2a-protocol.org/latest/specification/)
- [MCP elicitation](https://modelcontextprotocol.io/specification/draft/client/elicitation)
- [Agent Approve Agent Event Protocol](https://www.agentapprove.com/standards/agent-event-protocol)
- [agenteventprotocol organization](https://github.com/agenteventprotocol) and
  [reference stack](https://github.com/agenteventprotocol/reference)

Host integration sources are linked in each feasibility section above. Claims about Codex use
official OpenAI documentation; claims about other hosts use their official documentation or
official source repositories. Community adapters are labeled as such.
