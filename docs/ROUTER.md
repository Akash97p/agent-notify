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
  `x-agentnotify-router-key: <key>`, `Authorization: Bearer <key>`, or `x-api-key: <key>` and compared
  in constant time. The key only grants spending through the router; it cannot read notifications or
  configuration. It is generated when the router is first enabled and can be regenerated. When it
  arrives in `x-agentnotify-router-key`, the client's `Authorization` or `x-api-key` is its **own**
  Anthropic credential, used only for its own models (see [Claude Code's own models](#claude-codes-own-models)).
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
`provider_key_unreadable`, `subscription_signed_out`, `rate_limited`, `payment_required`, `timeout`,
`connection_error`, `client_error`, `upstream_error`, `all_targets_unavailable` — and never a
provider's own message. What the message may carry is the provider's own short error *code*, such as
`Upstream returned HTTP 429 (usage_limit_reached)`: taken from `error.code`, `error.type`,
`detail.code`, or a top-level `code`/`type`, and only when it is an identifier of at most 64
characters (`[A-Za-z0-9_.-]`). It is also written to the broker log with the upstream and model, so a
failed request can be explained afterwards. The ledger keeps its fixed code. A native Anthropic
request is the exception: Anthropic's own error body and `retry-after` are returned to the agent
unchanged.

## Configuration and storage

`config.json` holds only the switch, the router key, and limits:
`RouterEnabled` (default `false`), `RouterKey`, `RouterMaxRequestBodyBytes`, `RouterLedgerRetentionDays`
(default 30).

SQLite holds everything else, in tables created idempotently by `RouterRepository.InitializeAsync`:

- **`router_upstreams`** — `id`, `slug` (unique, `[a-z0-9][a-z0-9-]{0,31}`, the `provider` part of
  `provider/model`), `label`, `wire` (`openai_responses | openai_chat | anthropic_messages`),
  `base_url`, `encrypted_key` (nullable: local servers and subscriptions need none), `models` (JSON
  array of up to 500 native model IDs the owner declared), `enabled`, timestamps, `auth`
  (`api_key | codex_chatgpt | muse_code`, default `api_key`), and `model_wires` (nullable JSON object
  of model ID → wire, for a provider that serves some models over a different wire than its own). The
  last two columns are added to an older database in place. Keys are sealed with the broker's `ISecretProtector`
  before storage, decrypted only while building an upstream request, and never returned, logged, or
  put in the ledger — the same rules as API accounts.
- **`router_routes`** — `id`, `name` (unique), `kind` (`alias | combo`), `targets` (JSON ordered array
  of `provider/model` strings), `enabled`, timestamps. An alias has exactly one target; a combo has
  1–8 and fails over in order.
- **`router_settings`** — one row: `default_route` (a `provider/model`, alias, or combo name, nullable).
- **`router_requests`** — one row per logical request: `id`, `started_at`, `finished_at`,
  `inbound_wire`, `requested_model`, `route_kind` (`explicit | alias | combo | native | model_list | default`),
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

`RouterPresetCatalog` offers presets that fill everything except the key: slug, label, wire, base URL,
how the upstream authenticates, and which wire each model family uses. The owner can still enter any
base URL as a custom provider. `base_url` is the prefix the wire path is appended to (`/responses`,
`/chat/completions`, `/messages`):

| Preset | Kind | Wire | Base URL |
| --- | --- | --- | --- |
| ChatGPT plan (Codex) | subscription, unofficial | `openai_responses` | `https://chatgpt.com/backend-api/codex` |
| OpenCode Go | subscription (API key) | `openai_chat`, per model | `https://opencode.ai/zen/go/v1` |
| Muse Code plan | subscription, unofficial | `openai_responses` | `https://api.meta.ai/v1` |
| OpenCode Zen | per token | `openai_chat`, per model | `https://opencode.ai/zen/v1` |
| Meta Model API (Muse Spark) | per token | `openai_responses` | `https://api.meta.ai/v1` |
| OpenAI | per token | `openai_responses` | `https://api.openai.com/v1` |
| Anthropic | per token | `anthropic_messages` | `https://api.anthropic.com/v1` |
| OpenRouter | per token | `openai_chat` | `https://openrouter.ai/api/v1` |
| DeepSeek | per token | `openai_chat` | `https://api.deepseek.com/v1` |
| Moonshot (Kimi) | per token | `openai_chat` | `https://api.moonshot.ai/v1` |
| Z.ai | per token | `openai_chat` | `https://api.z.ai/api/paas/v4` |
| SiliconFlow | per token | `openai_chat` | `https://api.siliconflow.com/v1` |
| Groq | per token | `openai_chat` | `https://api.groq.com/openai/v1` |
| Ollama | this computer | `openai_chat` | `http://127.0.0.1:11434/v1` |
| LM Studio | this computer | `openai_chat` | `http://127.0.0.1:1234/v1` |

**Per-model wires.** OpenCode Zen and Go serve several model families under one base URL and key, each
on its own wire: Claude, Qwen, and Union (and Go's MiniMax) on `/messages`; GPT, Grok, and Muse Spark on
`/responses`; everything else on `/chat/completions`. The preset's prefix rules turn the saved model
list into `model_wires`, and `RouteResolver` narrows each resolved target to its model's wire, so the
proxy, translator, and ledger see the wire actually used. Zen's Gemini models use Google's own wire,
which the router does not speak, so they are left out of its model list. A model typed as an explicit
`provider/model` that was never declared uses the upstream's own wire.

**Finding a provider's models.** `POST /ui/api/router/models/fetch` lists a provider's models so they
can be ticked rather than typed. It asks `GET <base_url>/models` with the key being entered, the key
OpenCode already holds, or — for a saved upstream, and only when the base URL is unchanged — the
stored key, under the same destination rule as routed traffic; nothing is saved. The ChatGPT plan has
no public list, so Codex's own `models_cache.json` (entries whose `visibility` is `list`) is read
instead. The OpenAI shape `{"data":[{"id"}]}`, a bare array, and `{"models":[{"name"}]}` are accepted,
responses are capped at 4 MiB and 500 models, and the reply carries each model's wire.

**Reusing a key AgentNotify already has.** A key saved under Live quota → API accounts for DeepSeek,
Moonshot, SiliconFlow, or OpenRouter can be chosen as a provider's key. The upstream then stores no key
of its own; its `credential_ref` is `api_account:<id>`, and each attempt opens that account's key, so
the key is entered and rotated in one place. OpenAI and Anthropic accounts there hold Admin keys, which
cannot send model requests, so they are not offered. A removed account fails the attempt with
`provider_key_unreadable` rather than sending no key. A typed key, or removing the key, replaces the
reference; an upstream has one credential source at a time.

**Reusing a key OpenCode has.** When OpenCode's `auth.json` holds a plain API key for the preset's
provider (OpenCode Go and Zen, OpenRouter, DeepSeek, OpenAI, Anthropic, Moonshot, Z.ai, Groq), the page
offers to use it. The broker reads it only when asked to fetch or save, seals it like a typed key, and
never returns it; the preset list says only whether one exists. OpenCode's OAuth sign-ins are not
reused.

### Subscriptions

Two presets use a monthly plan the owner already pays for, through a sign-in another tool on this
computer keeps. **Both are unofficial**: neither plan documents use from other applications. They are
opt-in, labelled so on the page, and store no key in AgentNotify.

- **ChatGPT plan (`codex_chatgpt`).** Each attempt reads the `auth.json` of the Codex account the
  upstream names (`credential_ref` `profile:<directory>`, one of the accounts listed above; the
  built-in `$CODEX_HOME` or `~/.codex` when none), so each Codex account can be its own provider and a
  fallback chain can move from one plan to the other. It sends that account's access token with `chatgpt-account-id`, `OpenAI-Beta: responses=experimental`,
  and `originator: codex_cli_rs`. When the token is within five minutes of its `exp` claim, or the
  backend answers `401`, the file is read again (Codex may have renewed it), and otherwise the router
  renews it with Codex's own OAuth client and writes the new tokens back into Codex's file, leaving
  every other field alone, so Codex and the router keep sharing one sign-in. A request to this backend
  is adjusted first: `store` is forced to `false`, `instructions` is added empty when missing, and
  `max_output_tokens`, `max_tokens`, `temperature`, `top_p`, and `previous_response_id` are removed.
  The backend only answers as a stream (it refuses `stream: false` with a `400`), so `stream` is
  forced on, and a client that asked for one response gets the stream collected into one.
- **Muse Code plan (`muse_code`).** The identity token in Muse Code's `~/.config/muse/auth.json`
  (`access_token`) is exchanged at `POST https://api.meta.ai/muse-code/key` for a subscription Model
  API key, as Muse Code's CLI does; the key is kept in memory for 23 hours and used against
  `https://api.meta.ai/v1`. Muse Code may keep its sign-in in the OS keychain instead of that file; only
  the file is read.

A missing or unrenewable sign-in fails the attempt with `subscription_signed_out` (and a warning in the
broker log that says what to run); in a fallback chain the next target is tried.

**OpenCode Go** is a subscription too, but authenticates with an ordinary key. Its service asks each
client to name itself and to send a stable per-conversation ID, so every upstream request carries
`User-Agent: agentnotify-router/<version>`, and a request to an `opencode.ai` host carries
`x-opencode-session` with the agent's own conversation ID (`x-opencode-session`,
`x-claude-code-session-id`, `session_id`, or `conversation_id` from the inbound request), or a
per-process ID when the agent sent none.

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
3. **The agent's own Anthropic model.** On the Anthropic wire, when the client sent its own credential
   (see Authentication), a bare `claude-…` ID goes to Anthropic itself with that credential — route
   kind `native`, upstream `anthropic`. This comes before the declared-model rule, so a provider that
   happens to list `claude-opus-5` does not take over Claude Code's own Opus; only a route the owner
   named that way (rule 1) does.
4. **Declared model list.** A bare model ID declared by exactly one enabled upstream goes there. If
   two or more declare it, the request fails with `ambiguous_model` rather than picking one.
5. **Default route.** Otherwise the default route, when set, resolved by rules 1–2. A request that
   carries no model at all also lands here; without a default it is `missing_model` (`400`).
6. Otherwise `unknown_model` (`404`).

Disabled upstreams are skipped inside a combo, and a combo with no enabled target fails with
`no_enabled_target`.

### Smart switching

The Routing page's Smart switching card stores one strategy in `router_settings.switch_strategy`: `off`, `ordered`,
`sticky`, or `round_robin`. When enabled, whatever the rules above resolved to is expanded with **the
same model at every other enabled provider that lists it**, so a usage limit, an outage, or a refused
key can hand the request to the same model somewhere else with no route to set up:

- **Same model** means the IDs match ignoring case and any vendor path: `deepseek-v4-flash` at
  DeepSeek, `deepseek/deepseek-v4-flash` at OpenRouter, and `DeepSeek-V4-Flash` elsewhere are one model.
  Nothing else is treated as equivalent; a different version is a different model.
- **Order** of the added targets, cheapest kind first (`RouteResolver.CostTier`): a subscription through
  another tool's sign-in (each ChatGPT account, Muse Code), then OpenCode Go's flat plan, then a server
  on this computer, then anything billed per token, which is always last. Within a kind, the order the
  providers were added.
- **A bare model name several providers list** is no longer `ambiguous_model`: it goes to all of them in
  that order, starting with the cheapest.
- **Routes still apply.** A nickname or fallback chain resolves as before; switching appends the
  same-model fallbacks of every target after the chain's own.
- **Strategies.** Ordered begins from the configured first routed target every request. Sticky keeps
  the last routed target that completed successfully until it fails. Round robin rotates the routed
  starting target per model/route group, then retains ordinary failover order for that request.
- **Claude Code is one-way.** A credential-bearing native `claude-*` request starts at Anthropic,
  then tries the equivalent Claude model at configured providers, then the optional cross-model
  `claude_fallback_route`. Native Anthropic is never appended to another request or exposed to another
  harness. Sticky mode remains on a routed fallback after native Claude is exhausted; the other modes
  retry native Claude after its bounded cooldown. Without a configured fallback target, Anthropic's
  own errors still pass through unchanged.

The Routing page lists every model more than one provider serves, with the base order it can use.

### Every Codex account is a ChatGPT-plan provider

Codex accounts are one list, the one Insights and Live quota show. Once the owner has added the ChatGPT
plan at all, the broker keeps one ChatGPT-plan provider per signed-in Codex account
(`RouterConfigService.SyncCodexAccountsAsync`, run when the router page loads and after a plan is
added): a missing account gets a provider (`chatgpt-second` for `~/.codex-second`, same models as the
first), and a provider that duplicates another's account is pointed at one nothing uses yet, keeping its
slug so routes and agent pickers that name it keep working. A signed-out account is left out. The page
therefore never asks which Codex account to use; with smart routing on, a GPT model moves from one plan
to the next when the first runs out.

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
- Anthropic `system` (string or text blocks), mid-conversation `system`-role messages (Claude Code's
  environment block; joined to `system`, as the other decoders do with system and developer turns),
  `text`, `image` (base64 or URL), `tool_use`,
  `tool_result` (text or text blocks; `is_error`), `thinking`/`redacted_thinking` (dropped), any other
  block type — server tools, documents — (dropped, noted as `blocks_dropped`),
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
and the failure is one of: connection error, timeout before response headers, HTTP `401`/`403` (the
provider refuses its key or sign-in), `402` (that target's account is out of credit), `404` (a model
the provider lists but does not serve), `408`, `429`, `500`, `502`, `503`, `504`, `529`. Each of those
is about the target, not the request, so another target can still serve it. Any other `4xx` is the request's fault: it is returned to the client
without trying another target and without cooling the target down. Once the first byte reaches the
client the attempt is committed; a later upstream failure ends the stream with the wire's error event
(`response.failed`, Anthropic `error` event, or a Chat error chunk) and is recorded, never retried.

Cooldown is in memory, per `upstream slug + model`: `Retry-After` (seconds or HTTP date, capped at
10 minutes) when present, otherwise 30 seconds after a `429`/`529` and 15 seconds after a connection
error or `5xx`, 60 seconds after a `401`, `403`, or `404`, and 5 minutes after a `402`. When every target has failed, the client receives the last
failure. When every target is already cooling down, the request is answered at once without asking
any of them: `429 rate_limited` if the soonest cooldown to end began with a `429`/`529`, otherwise
`503 all_targets_unavailable`, with `retry-after` set to the seconds until it ends, so the agent waits
that long instead of retrying into the same wall. A native Anthropic request is never cooled down:
Anthropic's own answer, `retry-after` included, goes back to the agent.

Timeouts: 30 seconds to connect, 300 seconds to the response headers, and 300 seconds of silence
between streamed chunks. A client disconnect cancels the upstream request and records `canceled`.

## Putting routed models in the agent's own picker

Typing a selector works, but the point is to pick a routed model from the menu the agent already has.
Each host exposes that differently, so AgentNotify writes each host's own mechanism. Connecting is a
button on the Router → Agents page, or `agentnotify router connect <agent>`.

**Every account, not only the default one.** The page lists each Codex and Claude Code account from
the list Live quota monitors (`QuotaAccountDefinition.Monitored`): the built-in `~/.codex` and
`~/.claude` (IDs `codex` and `claude_code`, always present), discovered profiles such as
`~/.codex-second` (ID `codex:home:second`), and accounts added by hand (`q_…`), minus WSL profiles.
Each is connected in its own directory, and a Codex account other than the built-in one gets its own
generated catalogue (`codex-model-catalog-<id>.json`) because its shell-tool choice is its own. The CLI
takes the same IDs: `agentnotify router connect codex:home:second`.

**Codex** reads a model catalogue from a file named by its `model_catalog_json` setting. AgentNotify
generates that file — one entry per `provider/model`, per alias, and per `combo/<name>` — and adds a
`[model_providers.agentnotify]` block pointing at `/router/v1` with the router key in
`experimental_bearer_token`, so Codex authenticates with no environment variable set. Every routed
model then appears in `/model`. For a model on a ChatGPT-plan upstream, the catalogue entry is Codex's own, copied from its
`models_cache.json` with only the slug, display name, and priority changed, so that model keeps Codex's
prompt, context window, reasoning levels, and shell tool exactly as without the router. Codex's picker
shows only the catalogue: it has no mode that adds to its own list, which is why the ChatGPT-plan
provider is how GPT models stay in it.

Codex also resolves several settings per model, and the same page
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
says so on every start. Optionally those rows replace Anthropic's own lineup instead of following it; that switch is off unless
the owner turns it on.
Separately, `ANTHROPIC_BASE_URL`, `ANTHROPIC_CUSTOM_HEADERS` (the router key, as
`x-agentnotify-router-key: <key>`, after any header lines the owner already sends), and the model each
built-in entry resolves (`ANTHROPIC_MODEL`, `ANTHROPIC_DEFAULT_OPUS_MODEL`, `…SONNET…`, `…HAIKU…`, and
the background `ANTHROPIC_SMALL_FAST_MODEL`) are set in the `env` block of `settings.json`.
`ANTHROPIC_AUTH_TOKEN` is deliberately not set.

### Claude Code's own models

Claude Code has one base URL for every model, so once it points at the router its built-in Opus,
Sonnet, and Haiku arrive there too. They must not run on some other provider under Claude's name, and
they should keep using the owner's Claude sign-in. So the router key travels in its own header, which
leaves Claude Code sending its own sign-in exactly as it would to Anthropic (`Authorization: Bearer
sk-ant-oat…` for a Claude plan, `x-api-key` for an API key). A `claude-…` model that no route claims
is then sent to `https://api.anthropic.com/v1/messages` (or `/messages/count_tokens`) with that
credential, the body byte for byte, the inbound query string (`?beta=true`), and the client's own
`anthropic-*`, `x-stainless-*`, `user-agent`, `x-app`, and `x-claude-code-session-id` headers. The
credential goes to Anthropic and nowhere else: it is never sent to another upstream, stored, logged,
or put in the ledger. A client that sends the router key as its bearer has no credential of its own to
forward, so its `claude-…` models are ordinary unrouted names (default route or `unknown_model`).

**Running sessions.** Claude Code applies an `env` variable added to `settings.json` to a running
session at once, but keeps one that was removed until it restarts (verified with Claude Code 2.1.276).
Connecting therefore takes effect in open sessions, and disconnecting does not: they keep sending to
the router until restarted. Because their built-in models pass through to Anthropic, they keep
working; routed models keep working too while the router is on.

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

## Effort mapping

Claude Code sends its selected five-step effort as `output_config.effort` (`low`, `medium`, `high`,
`xhigh`, `max`); Codex sends its own `low`/`medium`/`high` as `reasoning.effort` or
`reasoning_effort`. Those five names mean the same thing on both scales, so the router normalizes
either wire onto them (`RouterRequest.reasoningEffort`) and maps once, after a route resolves to a
concrete target. Responses targets receive `reasoning.effort`; Chat targets receive
`reasoning_effort`. Native Anthropic requests remain byte-for-byte passthrough. Anthropic-compatible
aggregator hops keep every native field but replace or omit only `output_config.effort` when their
concrete model's mapping requires it. A same-wire hop whose mapped effort equals what the client
sent is not rewritten at all.

A mapping never silently changes a level the target can spell: the automatic tables are identity
wherever the target has the same name, and only levels above the target's top collapse onto it
(Claude's `max` becomes a four-level family's `xhigh`). A value outside the five names — OpenAI's
`minimal`, or a provider-specific word — is sent verbatim when the target supports it, and otherwise
falls back to the target's default (`minimal` counts as low). An unknown model omits effort
entirely.

**One table per family.** The Effort mapping page groups every routed model by family (DeepSeek,
GLM, Kimi, Qwen, MiniMax, Grok, Muse, OpenAI, Claude, or other) — a hundred models are ten cards.
Aggregators such as OpenRouter and OpenCode are classified from the model ID, not the aggregator
name. Each family card holds the exact accepted values, the target value for each of the five
levels, and an optional default used when the request carries no effort; the **Per model** tab
overrides any single `provider/model` the same way. Precedence: per-model override, family override,
automatic. Every map is bounded, monotonic, and may explicitly choose `omit`. Overrides live in
`router_effort_mappings` and `router_effort_family_overrides`; routes and combos need no copies
because the final target owns the capability.

## Web interface and CLI

The **Model router** group in the navigation holds five pages:

| Page | What it does |
| --- | --- |
| Providers | The on/off switch; your providers, each with an on/off switch; and a gallery of presets grouped as subscriptions, pay per token, and this computer. Adding one is: pick it, paste a key (or reuse OpenCode's, or a subscription sign-in), tick models from the fetched list, save. Slug, wire, and base URL sit under Advanced. The base URLs and key regeneration are under a disclosure. |
| Routing | Optional: nicknames (an alias, one model) and fallback chains (a combo, tried in order), each picked from the providers' models; what an unknown model falls back to; and Smart switching — ordered, sticky, or round-robin across providers that serve the same model, plus Claude Code's cross-model fallback. The page says plainly that no route is needed to use a model. |
| Agents | Connect an agent so its own picker lists these models, choose its subagent/review/effort settings, disconnect, and restore a saved copy of its configuration |
| Activity | The request ledger with per-attempt detail, and totals by model |
| Effort mapping | One effort table per model family (Families tab), with per-model overrides under Per model; applies to Claude Code's and Codex's efforts alike |

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
export ANTHROPIC_CUSTOM_HEADERS="x-agentnotify-router-key: $(agentnotify router key)"
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
- Weighted, weighted-random, or least-used combo strategies; ordered, sticky, and round-robin smart switching exist.
- Pinning a Codex account pool; Gemini and Ollama-native wires.
- Connectors for the other hosts (OpenCode, Kilo, Cursor, Gemini CLI); only Codex and Claude Code
  have one, and each needs that host's own model-list mechanism rather than a generic file edit.
- Per-model context windows in the generated catalogue: every entry declares 200k, because the router
  does not yet know each upstream model's real window.
- Cost estimates on ledger rows and correlation with log-derived Usage records.
- Quota and spend thresholds raised as attention requests.
