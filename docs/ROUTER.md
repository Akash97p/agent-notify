# Provider router

The provider router is an opt-in local proxy inside the broker. A coding agent points its API base
URL at the broker; the router picks an upstream provider and model for each request, translates
between the OpenAI Responses, OpenAI Chat Completions, and Anthropic Messages wire formats, fails
over across an ordered list of targets, and records every request and physical attempt in a local
ledger. It is how Codex can run on DeepSeek, OpenRouter, Kimi, Z.ai, or a local Ollama model, and how
Claude Code can run on an OpenAI-compatible provider, without either agent knowing.

It is off until the owner turns it on. Nothing else in AgentNotify depends on it, and a broker with
the router off makes no request to any model provider.

## Endpoints

All routes live on the broker's existing loopback listener, under `/router/v1`:

| Method and path | Inbound wire | Typical client |
| --- | --- | --- |
| `POST /router/v1/responses` | OpenAI Responses | Codex (`wire_api = "responses"`) |
| `POST /router/v1/chat/completions` | OpenAI Chat Completions | OpenCode, Kilo, anything OpenAI-compatible |
| `POST /router/v1/messages` | Anthropic Messages | Claude Code (`ANTHROPIC_BASE_URL`) |
| `POST /router/v1/messages/count_tokens` | Anthropic | Claude Code; forwarded only to an Anthropic-wire upstream, otherwise `404` |
| `GET /router/v1/models` | OpenAI model list | lists every selectable `provider/model`, alias, and combo |

- **Off:** every `/router` request gets `404 {"error":{"type":"router_disabled",...}}`.
- **Authentication:** a separate router key, not the broker's `/v1` bearer token. It is accepted as
  `Authorization: Bearer <key>` or `x-api-key: <key>` and compared in constant time. The key only
  grants spending through the router; it cannot read notifications or configuration. It is generated
  when the router is first enabled and can be regenerated.
- **Loopback:** the same `Host` check as the web interface (loopback names only), so a DNS-rebinding
  page cannot reach it. Browsers are refused: a request carrying an `Origin` header gets `403`.
- **Body size:** the broker's 64 KiB API limit does not apply. Router routes raise the per-request
  limit to `RouterMaxRequestBodyBytes` (default 32 MiB) because agent requests carry whole
  conversations and images.

Errors are written in the inbound wire's own error shape (`{"error":{"message","type","code"}}` for
OpenAI wires, `{"type":"error","error":{"type","message"}}` for Anthropic) so the agent displays them.
The `code` is one of a fixed set — `router_disabled`, `unauthorized`, `forbidden`,
`host_not_loopback`, `payload_too_large`, `invalid_request`, `missing_model`, `unknown_model`,
`ambiguous_model`, `no_enabled_target`, `unsupported_previous_response_id`, `not_supported`,
`provider_key_unreadable`, `rate_limited`, `timeout`, `connection_error`, `client_error`,
`upstream_error`, `all_targets_unavailable` — and never a provider's own message.

## Configuration and storage

`config.json` holds only the switch, the router key, and limits:
`RouterEnabled` (default `false`), `RouterKey`, `RouterMaxRequestBodyBytes`, `RouterLedgerRetentionDays`
(default 30).

SQLite holds everything else, in tables created idempotently by `RouterRepository.InitializeAsync`:

- **`router_upstreams`** — `id`, `slug` (unique, `[a-z0-9][a-z0-9-]{0,31}`, the `provider` part of
  `provider/model`), `label`, `wire` (`openai_responses | openai_chat | anthropic_messages`),
  `base_url`, `encrypted_key` (nullable: local servers need none), `models` (JSON array of native model
  IDs the owner declared), `enabled`, timestamps. Keys are sealed with the broker's `ISecretProtector`
  before storage, decrypted only while building an upstream request, and never returned, logged, or
  put in the ledger — the same rules as API accounts.
- **`router_routes`** — `id`, `name` (unique), `kind` (`alias | combo`), `targets` (JSON ordered array
  of `provider/model` strings), `enabled`, timestamps. An alias has exactly one target; a combo has
  1–8 and fails over in order.
- **`router_settings`** — one row: `default_route` (a `provider/model`, alias, or combo name, nullable).
- **`router_requests`** — one row per logical request: `id`, `started_at`, `finished_at`,
  `inbound_wire`, `requested_model`, `route_kind` (`explicit | alias | combo | model_list | default`),
  `route_name`, final `upstream_slug`/`model`, `stream`, `status` (HTTP status returned to the client),
  `outcome` (`ok | upstream_error | client_error | canceled | failed_over_exhausted`), token counts
  (`input`, `cached_input`, `output`, `reasoning`, nullable), `usage_status`
  (`reported | unreported`), `error_code` (a short fixed code, never a provider message body).
- **`router_attempts`** — one row per physical upstream send: `request_id`, `ordinal`, `upstream_slug`,
  `model`, `upstream_wire`, `status` (HTTP status or null), `error_code`, `duration_ms`,
  `bytes_streamed` (whether any byte reached the client), `started_at`.

The ledger never stores prompts, responses, headers, keys, or provider error bodies. Rows older than
`RouterLedgerRetentionDays` are pruned when the broker starts and once a day.

### Upstream presets

`RouterPresetCatalog` offers presets that fill `wire` and `base_url`; the owner can still enter any
base URL. `base_url` is the prefix the wire path is appended to (`/responses`, `/chat/completions`,
`/messages`):

| Preset | Wire | Base URL |
| --- | --- | --- |
| OpenAI | `openai_responses` | `https://api.openai.com/v1` |
| Anthropic | `anthropic_messages` | `https://api.anthropic.com/v1` |
| OpenRouter | `openai_chat` | `https://openrouter.ai/api/v1` |
| DeepSeek | `openai_chat` | `https://api.deepseek.com/v1` |
| Moonshot (Kimi) | `openai_chat` | `https://api.moonshot.ai/v1` |
| Z.ai | `openai_chat` | `https://api.z.ai/api/paas/v4` |
| SiliconFlow | `openai_chat` | `https://api.siliconflow.com/v1` |
| Groq | `openai_chat` | `https://api.groq.com/openai/v1` |
| Ollama (this computer) | `openai_chat` | `http://127.0.0.1:11434/v1` |
| LM Studio (this computer) | `openai_chat` | `http://127.0.0.1:1234/v1` |

**Destination rule.** `base_url` must be absolute `https`, or `http` only when the host is a loopback
literal (`127.0.0.1`, `::1`, `localhost`). No user info, query, or fragment. The upstream client
follows no redirects, sends no cookies, and ignores system proxies, so a key only ever reaches the
host it was saved for.

## Route selection

`RouteResolver` is a pure function of one configuration snapshot (upstreams, routes, default) and the
requested `model` string. It returns an ordered list of concrete targets `{upstream, nativeModel}` plus
`route_kind`/`route_name`, or a fixed error. Precedence:

1. **Combo or alias by name.** `combo/<name>` or a bare `<name>` equal to an enabled route's name.
2. **Explicit `provider/model`.** The part before the first `/` names an enabled upstream slug; the
   rest is the native model ID verbatim (so `openrouter/anthropic/claude-sonnet-4.5` works).
3. **Declared model list.** A bare model ID declared by exactly one enabled upstream goes there. If
   two or more declare it, the request fails with `ambiguous_model` rather than picking one.
4. **Default route.** Otherwise the default route, when set, resolved by rules 1–2. A request that
   carries no model at all also lands here; without a default it is `missing_model` (`400`).
5. Otherwise `unknown_model` (`404`).

Disabled upstreams are skipped inside a combo, and a combo with no enabled target fails with
`no_enabled_target`.

## Protocol translation

Translation goes through one internal representation, so three decoders and three encoders give
every pair:

- **`RouterRequest`** — `model`, `system` text, ordered `messages` (role `user | assistant | tool`,
  parts: text, image (URL or base64 with media type), tool call `{id, name, argumentsJson}`, tool
  result `{callId, text, isError}`), `tools` (`{name, description, parametersSchema}`), `toolChoice`
  (`auto | none | required | {name}`), `maxOutputTokens`, `temperature`, `topP`, `stop`, `stream`,
  `reasoningEffort` (optional hint), and `parallelToolCalls`.
- **`RouterStreamEvent`** — `TextDelta`, `ReasoningDelta`, `ToolCallStart{index,id,name}`,
  `ToolCallArgumentsDelta{index,json}`, `ToolCallEnd{index}`, `Usage{input,cachedInput,output,reasoning}`,
  `Finish{reason: stop | length | tool_calls | content_filter | error}`.

Per wire: a request decoder (inbound JSON → `RouterRequest`), a request encoder (`RouterRequest` →
upstream JSON), a stream parser (upstream SSE → events), a stream writer (events → inbound SSE), and a
non-streaming aggregator/writer pair.

**Passthrough.** When the inbound wire equals the upstream wire, the body is forwarded as-is except
that `model` is replaced by the native model ID. Streamed bytes are relayed unchanged while a tap
parses usage for the ledger. This keeps fields the IR does not model (Responses `reasoning` items with
`encrypted_content`, Anthropic thinking signatures, cache control) intact on a same-wire hop.

**What translation drops or maps, deliberately:**

- Responses input items: `message` (roles `user`, `assistant`, `system`, `developer` — the last two
  join `system`), content `input_text`/`output_text`/`input_image`, `function_call`,
  `function_call_output`, `custom_tool_call` and `custom_tool_call_output` (mapped to a function tool
  call whose arguments are `{"input": "<text>"}`), and `reasoning` (dropped). `instructions` becomes
  `system`. Tools of type `function` map directly; type `custom` (Codex `apply_patch` freeform) maps to
  a function with one required string parameter `input`, and the response maps back to a
  `custom_tool_call` item with that string as `input`. Built-in tools (`web_search`, `local_shell`,
  `image_generation`, …) are dropped when translating, and noted in the ledger as `tools_dropped`.
  `store` is forced irrelevant (no state is kept); `previous_response_id` on a translated hop fails
  with `unsupported_previous_response_id`.
- Anthropic `system` (string or text blocks), `text`, `image` (base64 or URL), `tool_use`,
  `tool_result` (text or text blocks; `is_error`), `thinking`/`redacted_thinking` (dropped),
  `tools[].input_schema`, `tool_choice` (`auto`, `any` → required, `tool` → name, `none`),
  `max_tokens`, `stop_sequences`, `temperature`, `top_p`.
- Chat Completions `messages` (`system`/`developer`, `user` string or parts, `assistant` with
  `tool_calls`, `tool`), `tools`, `tool_choice`, `max_tokens`/`max_completion_tokens`, `stop`,
  `stream_options.include_usage` (always requested upstream so usage reaches the ledger),
  `reasoning_content` deltas (as `ReasoningDelta`, emitted to the client only where its wire has a
  place for it).
- Anthropic upstreams require `max_tokens`; when the inbound request has none, the encoder uses 8192.

**Stream framing written back to the client:**

- *Responses:* `response.created`, `response.in_progress`, then per output item
  `response.output_item.added`, `response.content_part.added`, `response.output_text.delta`…,
  `response.output_text.done`, `response.content_part.done`, `response.output_item.done`; a function
  call streams `response.function_call_arguments.delta`/`.done`; a custom tool call emits its
  `custom_tool_call` item on `output_item.done`; finally `response.completed` (or
  `response.incomplete` for `length`) with `usage` `{input_tokens, input_tokens_details.cached_tokens,
  output_tokens, output_tokens_details.reasoning_tokens, total_tokens}`. Each event carries
  `sequence_number`. IDs are `resp_…`, `msg_…`, `fc_…`, generated per request.
- *Anthropic:* `message_start` (with `usage.input_tokens` when known, else 0), `content_block_start`,
  `content_block_delta` (`text_delta` / `input_json_delta`), `content_block_stop`, `message_delta`
  (`stop_reason`: `end_turn | max_tokens | tool_use | stop_sequence`, `usage.output_tokens`),
  `message_stop`. `ping` is not required.
- *Chat Completions:* `chat.completion.chunk` objects with `delta.content`, `delta.tool_calls`,
  `finish_reason`, a final usage chunk when the client asked for it, then `data: [DONE]`.

## Failover

A combo's targets are tried in order for one logical request. A target is skipped while it is cooling
down. An attempt **fails over** to the next target only when no byte has been written to the client
and the failure is one of: connection error, timeout before response headers, HTTP `408`, `429`,
`500`, `502`, `503`, `504`, `529`. Any other `4xx` is the request's fault: it is returned to the client
without trying another target and without cooling the target down. Once the first byte reaches the
client the attempt is committed; a later upstream failure ends the stream with the wire's error event
(`response.failed`, Anthropic `error` event, or a Chat error chunk) and is recorded, never retried.

Cooldown is in memory, per `upstream slug + model`: `Retry-After` (seconds or HTTP date, capped at
10 minutes) when present, otherwise 30 seconds after a `429`/`529` and 15 seconds after a connection
error or `5xx`. When every target is cooling down or has failed, the client receives the last failure
(or `503 all_targets_unavailable`).

Timeouts: 30 seconds to connect, 300 seconds to the response headers, and 300 seconds of silence
between streamed chunks. A client disconnect cancels the upstream request and records `canceled`.

## Putting routed models in the agent's own picker

Typing a selector works, but the point is to pick a routed model from the menu the agent already has.
Each host exposes that differently, so AgentNotify writes each host's own mechanism. Connecting is a
button on the Router → Agents page, or `agentnotify router connect <agent>`.

**Codex** reads a model catalogue from a file named by its `model_catalog_json` setting. AgentNotify
generates that file — one entry per `provider/model`, per alias, and per `combo/<name>` — and adds a
`[model_providers.agentnotify]` block pointing at `/router/v1` with the router key in
`experimental_bearer_token`, so Codex authenticates with no environment variable set. Every routed
model then appears in `/model`. Codex also resolves several settings per model, and the same page
writes them: reasoning effort, the subagent model and its effort (`default_subagent_model`), the
review model, and which shell tool a routed model is offered.

That last one matters. Codex's `shell_type` chooses the tool the model must call; AgentNotify defaults
to `shell_command`, one ordinary function call that third-party models handle far more reliably than
the stateful `unified_exec` session tool. Codex's `local` shell type is deliberately not offered: it
is a built-in tool type rather than a function, and translation to another wire drops it, which would
leave the model unable to run anything.

Codex's catalogue entries must also carry instructions. Its own models get them from its backend, and
a routed model has no such entry, so AgentNotify supplies its own short, plain preamble rather than
copying anyone else's prompt.

**Claude Code** has both a curated picker and per-entry environment variables, so both are written.
Its `modelPicker` setting gains a row per routed selector, each declaring the known model it
`behavesAs` — without that Claude Code cannot tell a routed model's context window or capabilities and
says so on every start. Optionally those rows replace Anthropic's own lineup instead of following it.
Separately, `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, and the model each built-in entry resolves
(`ANTHROPIC_MODEL`, `ANTHROPIC_DEFAULT_OPUS_MODEL`, `…SONNET…`, `…HAIKU…`, and the background
`ANTHROPIC_SMALL_FAST_MODEL`) are set in the `env` block of `settings.json`.

### What writing those files is held to

- **A copy first.** Every change copies the file into AgentNotify's own directory first. The Agents
  page lists those copies with the reason each was taken, and restores any of them; a restore copies
  the current file too, so it can be stepped back.
- **Only AgentNotify's own lines.** Codex's `config.toml` is edited between marker comments, in two
  regions because TOML is positional — bare keys must precede the first table. Nothing else in the
  file is reordered or reformatted. A key the owner already set that the managed region is about to
  set is commented out rather than left in place, because TOML rejects a key assigned twice and Codex
  would refuse to start. Claude Code's `settings.json` is merged as JSON, touching only the keys
  listed above.
- **Their own settings come back.** Disconnecting restores the values the file held before, not merely
  the absence of AgentNotify's lines, and un-comments what was commented out.
- **Nothing for an agent that is not installed.** No directory is created to make a host appear
  connected.
- **The catalogue follows the router.** Changing upstreams, routes, or the key rewrites a connected
  agent's generated catalogue and embedded key, and drops a subagent or review model that no longer
  resolves — a picker offering models the router refuses is worse than no picker.

## Web interface and CLI

The **Model router** group in the navigation holds four pages:

| Page | What it does |
| --- | --- |
| Providers | The on/off switch, the base URLs, a one-time key reveal on regenerate, and the upstreams (presets, write-only keys, declared models, enable) |
| Routing | Aliases and ordered failover combos, and the default route |
| Agents | Connect an agent so its own picker lists these models, choose its subagent/review/effort settings, disconnect, and restore a saved copy of its configuration |
| Activity | The request ledger with per-attempt detail, and totals by model |

The Agents page also shows copyable snippets for configuring a host by hand, for anyone who would
rather AgentNotify did not touch their files.

Codex (`~/.codex/config.toml`):

```toml
model_provider = "agentnotify"
model = "combo/coding"

[model_providers.agentnotify]
name = "AgentNotify router"
base_url = "http://127.0.0.1:47821/router/v1"
env_key = "AGENTNOTIFY_ROUTER_KEY"
wire_api = "responses"
```

Claude Code:

```bash
export ANTHROPIC_BASE_URL=http://127.0.0.1:47821/router
export ANTHROPIC_AUTH_TOKEN="$(agentnotify router key)"
export ANTHROPIC_MODEL=combo/coding
```

`agentnotify router status` prints whether the router is on and its base URLs, and
`agentnotify router key` prints the router key from the local config (like `agentnotify token`); both
read the file directly. `agentnotify router agents` lists the agents and every selector they can be
pointed at, while `agentnotify router connect <agent> [--model <selector>]` and
`agentnotify router disconnect <agent>` ask the running broker to write or undo those files, since it
owns the key and the generated catalogue.

The web API under `/ui/api/router` follows the existing web-interface rules: loopback host check,
`X-AgentNotify-UI: 1` on every change, and write-only secrets.

## Relationship to Usage and Live quota

The router ledger is **proxy-observed usage**. It is kept separate from the log-derived Usage view
and from Live quota, and the two are never added together: a request that went through the router is
also written to the agent's own log. The Router page shows its own totals with that caveat.

## Deliberate limits

- A route that is the default route cannot be deleted until the default is changed, and an upstream
  cannot be deleted while a route or the default still names it.
- `count_tokens` is only forwarded when the resolved target is an Anthropic-wire upstream; there is no
  local tokenizer, and guessing a count would be worse than saying it is unavailable.
- A canceled request is recorded with no status, since nothing was returned to the client.

## Not implemented yet

- Policy routing (`policy/<id>`) scored on quota, health, cost, and latency evidence.
- Weighted, round-robin, or least-used combo strategies; only ordered failover exists.
- Pinning a Codex account pool; Gemini and Ollama-native wires.
- Connectors for the other hosts (OpenCode, Kilo, Cursor, Gemini CLI); only Codex and Claude Code
  have one, and each needs that host's own model-list mechanism rather than a generic file edit.
- Per-model context windows in the generated catalogue: every entry declares 200k, because the router
  does not yet know each upstream model's real window.
- Cost estimates on ledger rows and correlation with log-derived Usage records.
- Quota and spend thresholds raised as attention requests.
