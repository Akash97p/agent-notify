# How the routing proxy routes local agent requests

The routing proxy is a Bun local proxy. It configures Codex to send its native Responses traffic to the proxy by inserting an `openai_base_url` plus model catalog path into Codex TOML; the injector design preserves the native OpenAI provider identity on loopback. The server also accepts Anthropic Messages and OpenAI Chat Completions. The HTTP entrypoint authenticates/admit requests, then dispatches `/v1/responses`, `/v1/messages`, and `/v1/chat/completions` to the appropriate handlers.

## Route selection

`routeModel` turns a client-facing model selector into `{providerName, effective provider config, native modelId, routeKind, reason, decision trace}`. Its essential precedence is:

1. A `policy/<id>` or configured policy alias evaluates candidates using capability, health, quota, cost, and compatibility evidence. It then resolves the selected `provider/model` concretely, with policy recursion disabled.
2. An exact Codex account namespace can pin a native OpenAI model to one pool account. A configured `combo/<id>` can select from multiple provider/model targets.
3. Explicit `provider/model` (or a unique configured/registry provider alias) wins over the default. Known native model IDs preserve slash-containing IDs; a slug codec decodes Codex-facing aliases back to native IDs.
4. Bare native OpenAI-family IDs remain assigned to the canonical OpenAI provider. Other bare IDs try configured defaults, model patterns/lists, unique aliases, then the configured default provider. Ambiguous aliases fail rather than choosing arbitrarily.

`routedProviderConfig` resolves configured key references, merges trusted registry defaults with operator overrides, and validates the final destination URL. The provider registry contains known adapters, base URLs, model capabilities and trusted discovery policy; the destination policy blocks unsafe credential destinations. Keep such validation before building a credential-bearing upstream request.

## Protocol translation

The Responses core reads and bounds the inbound body, resolves a route, constructs an adapter, sends upstream, and emits Responses-compatible events. `ProviderAdapter` is the key seam: `buildRequest` serializes the native upstream wire, `fetchResponse` may override transport, and `parseStream`/`parseResponse` turns the upstream response into common adapter events. Some adapters own a full `runTurn`. `ADAPTER_REGISTRY` maps wire types such as `openai-responses`, `openai-chat`, `anthropic`, `google`, `ollama-native`, and others to factories.

Anthropic Messages and Chat Completions requests are normalized into an internal Responses-shaped request when translation is needed (Messages, Chat). The common adapter-event stream is then reframed to the original client wire. Native passthrough is used where the selected upstream already speaks the required protocol. The translator maintains request/body/event budgets and terminal-stream checks because one partially delivered stream cannot safely be retried as though no bytes were sent.

## Policy algorithm worth extracting

`evaluatePolicyProfile` evaluates candidates in declaration order. Hard requirements and known cost caps can exclude a target; unknown evidence follows a configured allow/penalize/exclude policy. Remaining candidates get a deterministic weighted score from configured priority, health, quota, cost, and latency, with compatibility as a penalty. Strict `>` selection makes earlier declaration win ties. The quota evidence uses cached percentages and reset times; missing quota remains unknown, not healthy. The result carries a bounded, redacted decision trace suitable for debugging why a route was picked.

For your app, keep routing, credential resolution, protocol adapters, and request accounting as separate modules. A route decision should be reproducible from one config generation and one evidence snapshot, and the chosen credential should remain pinned for the physical attempt.
