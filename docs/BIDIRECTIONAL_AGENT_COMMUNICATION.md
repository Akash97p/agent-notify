# Bidirectional agent communication

Status: **durable broker interactions, WebUI answering, Codex/Claude ask hooks, and the Relay/mobile
answer path are implemented**. The Agent Client Protocol bridge, broader host-native answer
adapters, host-acceptance receipts, and sealed mobile responses remain design work. See
[INTERACTIONS.md](INTERACTIONS.md), [HARNESS.md](HARNESS.md), and
[RELAY_INTERACTIONS.md](RELAY_INTERACTIONS.md) for the shipped contracts.

Research checked: **2026-09-03**. Agent APIs and draft protocols can change; adapters must pin and
test the versions they support.

## What is implemented

AgentNotify now persists bounded questions and permissions in SQLite, exposes authenticated
loopback request/list/wait/respond/cancel routes and CLI commands, and accepts the first valid
answer. The local WebUI answers pending questions. Codex and Claude Code `--ask` hooks pause a
live permission request and return an allow/deny decision to that same session; on timeout or
failure they leave the host's ordinary prompt in control. Hermes and OpenClaw have separate
answer bridges. Eleven hosts have installable notification harnesses, but notification coverage
does not imply answer control for every host.

The Relay can carry an interaction to a paired phone and the broker polls for its answer while it
runs. This path has been exercised end to end, but response sealing to an installation key and an
explicit host-acceptance receipt are still missing. Relay sees v1 response content in plaintext;
it is trusted for transport integrity while the broker revalidates digest, nonce, expiry, and
first-wins rules. See [RELAY_INTERACTIONS.md](RELAY_INTERACTIONS.md) for the current wire contract.

## Remaining integration direction

Two integration modes shape further work:

1. **Direct/native adapters** use a coding agent's supported hooks, plugin API, SDK, gateway, or RPC
   surface. Codex/Claude ask hooks, Hermes, and OpenClaw already use this path; other hosts need
   verified decision schemas before they can return an answer to an existing session.
2. **Managed Agent Client Protocol sessions** would let an AgentNotify bridge start an ACP-capable
   coding agent as a child process, act as its client, receive permission and elicitation requests,
   and return the human response over the same JSON-RPC session. This bridge is not implemented.

Do not rebuild AgentNotify on Agent Communication Protocol/A2A, either project named Agent Event
Protocol, or MCP. Those standards solve useful adjacent problems, but none replaces AgentNotify's
durable human-attention lifecycle, local history, relay routing, and mobile response security.

The practical topology is:

```text
existing terminal/editor session                 future managed session
        |                                                    |
native hook / plugin / SDK                       Agent Client Protocol bridge
        |                                                    |
        +--------------- AgentNotify host adapter -----------+
                                  |
                    local broker + SQLite source of truth
                         /                         \
          local WebUI / CLI                    durable relay outbox
                                                    |
                                           Relay + mobile UI
                                                    |
                             authenticated answer (visible to Relay)
                                                    |
                         interaction broker completes one waiter
                                                    |
                           host adapter returns the native answer
```

The shipped direct path is event-driven at the agent boundary and durable in SQLite. The adapter
owns a live waiter; SQLite owns the request and response record; Relay can disconnect and resume.
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

AgentNotify keeps three different concerns separate.

| Layer | Responsibility | Current state |
| --- | --- | --- |
| Host adapter | Translate a vendor's live permission/question request and return its answer | Codex/Claude ask hooks, Hermes, and OpenClaw answer paths; ACP bridge planned |
| Interaction contract | Stable identity, choices, expiry, state, idempotency, and response outcome | Broker model and loopback API shipped; ARC response profile and host-acceptance receipt planned |
| Remote transport | Deliver requests/responses, reconnect, backfill, and acknowledge transport | Relay/mobile round trip shipped; response sealing to the installation planned |

The relay transport should not contain Codex-, Claude-, or Cursor-specific payloads. The WPF app
should not embed every vendor SDK. Adapters translate at the edge into one bounded AgentNotify
interaction model.

## Interaction model and extensions

The broker's implemented fields and API are normative in [INTERACTIONS.md](INTERACTIONS.md).
The table below also includes extension ideas, which are not part of the current wire contract.

### Request identity and state

| Field | Purpose |
| --- | --- |
| `id` | AgentNotify-assigned stable interaction identity |
| `request_event_id` | Future immutable producer event/correlation identity |
| `session_id` | Native session identity |
| `turn_id` | Native turn/generation identity when available |
| `native_request_id` | Opaque ID required to answer the host request |
| `kind` | `permission`, `single_choice`, or `text` today; `multi_choice` and bounded `form` are future work |
| `prompt` | Exact human-readable question or permission explanation |
| `choices` | Stable choice IDs, labels, and optional semantics |
| `allowed_decisions` | Future separate scope-aware field; today the offered `choices` define valid answers |
| `expires_at` | Absolute response deadline |
| `status` | `pending`, `answered`, `expired`, `cancelled`, or `superseded` |
| `request_digest` | Digest binding the displayed request to an eventual response |

Permission choices should preserve host semantics such as `allow_once`, `allow_session`,
`allow_always`, `deny_once`, or `deny_always`. An adapter must never advertise or synthesize a
scope the host did not offer.

### Response and outcome

A mobile tap is not proof that the coding agent accepted the decision. The broker records its own
answer acceptance; an explicit native-host acceptance receipt remains future work:

```text
submitted by human -> accepted by AgentNotify -> accepted by host
                                      \-> rejected as stale/mismatched
```

Likewise, keep delivery state separate from interaction state:

```text
delivered != viewed != answered != accepted by host
```

The current response record contains the interaction ID, request digest, selected choice or
bounded text, response time, and idempotency key. A host-acceptance outcome and ARC response
events remain design decisions.

### Adapter capabilities

Every adapter should declare capabilities rather than relying on product-name assumptions:

- permission decisions and the scopes it can faithfully return;
- single choice, multiple choice, bounded text, or structured form input;
- request cancellation and timeout behavior;
- reconnect/backfill support;
- whether it owns a managed session or attaches to an existing one;
- whether answers survive a desktop or adapter restart; and
- whether the host can confirm that it accepted the answer.

## Broker and API state

SQLite interaction records, keyed idempotency, first-valid-response-wins, expiry, cancellation,
supersession, authenticated loopback routes, CLI wait/respond commands, local WebUI answering,
and durable Relay publication are implemented. The interaction remains distinct from the
notification that makes it visible. Native Windows toast/center answering and a durable
host-acceptance receipt remain separate work; the WebUI and CLI already provide local answers.

## Relay and mobile return path

The implemented v1 path sends a sealed request envelope to Relay, lets the phone submit an
authenticated answer, and has the running broker poll Relay with bounded backoff and a persisted
cursor. The broker revalidates the answer and completes the waiting adapter. The Relay response
body is currently plaintext to Relay; end-to-end response sealing is a follow-up. A future push
wake-up may reduce latency, but polling/backfill must remain so a dropped connection cannot lose
an answer. Relay acceptance, broker acceptance, and host acceptance are distinct states.

## Security requirements and remaining gaps

Remote permission responses are authorization messages. They require a stronger boundary than an
ordinary notification acknowledgement.

- Seal and authenticate response content end to end for the intended installation. This is not
  implemented for v1 responses; Relay currently sees plaintext answers.
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
- Keep local WebUI/CLI response available and make first-valid-response-wins deterministic when
  local and mobile answers race. Native Windows toast/center answering remains future work.
- Keep Relay transport acknowledgement distinct from desktop decryption, user view, submitted
  response, and host acceptance.
- Redact prompts, command arguments, paths, and answers from logs by default. Retain only the minimum
  bounded audit metadata selected by the user.

Required failure tests include duplicate response, replay, stale response, wrong installation,
wrong session/turn, changed request digest, offline Relay, desktop restart, adapter crash, host
rejection, timeout, and simultaneous local/mobile responses.

## Standards assessment

### ARC — keep as AgentNotify's semantic contract

ARC 0.1 models durable attention request create/update/resolve state and intentionally excludes
structured answers. The broker's separate interaction contract is already live; a future ARC
interaction/response profile should follow its stabilized semantics, including host-acceptance
receipts when available. ARC should stay transport-neutral.

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
| Codex | Excellent | `PermissionRequest` ask hook shipped; app-server for managed sessions planned | Background hooks cannot decide a live request; app-server must remain locally authenticated |
| Claude Code | Excellent | `PermissionRequest` ask hook shipped; Agent SDK path planned | Use the host's exact decision and question schemas; blocking handler owns the waiter |
| OpenCode | Excellent | Plugin + SDK permission list/get/reply; ACP where available | Pin/test current API; reconcile pending requests after reconnect |
| Kilo Code | Excellent | Plugin event/permission API or `kilo acp` | CLI and editor share concepts, but test each surface and version |
| Hermes | Excellent | Native approval transport shipped | Best security match: immutable request, ID/digest binding, allowed choices, bounded timeout |
| OpenClaw | Excellent | Gateway approval watcher shipped | Treat canonical command/cwd/session plan as authoritative; backfill pending approvals |
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

## Implementation status and next steps

- **Shipped:** interaction domain/storage and loopback API; WebUI and CLI answers; Codex/Claude
  ask hooks; Hermes/OpenClaw answer paths; Relay reverse polling and mobile answer UI. See the
  linked implementation docs for limits and verification.
- **Next:** native Windows toast/center answer controls, a broker-to-host acceptance receipt,
  and end-to-end sealed mobile responses. These are distinct from the already working WebUI and
  Relay answer path.
- **Planned:** a managed Agent Client Protocol bridge; verified answer adapters for more hosts;
  scope-aware grants and richer question kinds; optional MCP/AEP/A2A projections.

TypeScript is the likely best language for the standalone bridge because ACP has an official
TypeScript SDK and OpenCode, Kilo, Pi, OpenClaw, Muse, and Copilot offer TypeScript-friendly
surfaces. Hermes can remain a thin Python plugin. The portable .NET broker owns durable state and
policy; WPF owns Windows presentation only.

## Open design decisions

- Whether response events become ARC 0.2 core events or a separately versioned ARC interaction
  profile.
- Whether the bridge is distributed inside AgentNotify archives or as a separate npm/package
  artifact with a small launcher.
- Whether the shipped authenticated loopback HTTP/wait path needs a second local adapter transport
  for managed sessions. The semantic and persistence model should not depend on that choice.
- How long a response remains visible after the native host cancels or supersedes it.
- Which exact host details may leave the computer under the user's route redaction policy.
- Whether mobile can answer only requests from installations it has explicitly trusted for control,
  distinct from installations allowed to send notifications.

## Licensing note

The owner reports that the Relay and Android mobile builds were tested successfully end to end on
2026-09-03. This documentation branch did not repeat that device test.

- `agent-notify` — this project — is MIT-licensed and open source.
- AgentNotify Relay and its mobile client are **not** open source. They are closed-source components
  operated as a hosted service, and carry no permission to use, copy, modify, or distribute their
  source.

Copies of this project already distributed under MIT retain the rights the MIT licence grants; the
Relay's licensing is separate and does not affect them. This is a project record, not legal advice.

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
