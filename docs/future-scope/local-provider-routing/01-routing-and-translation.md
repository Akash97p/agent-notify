# Router: routing, resolution, and wire translation

The router in [`AgentNotify.Core/Router`](../../src/AgentNotify.Core/Router) is an opt-in loopback
endpoint with its own key, separate from the notification API token. It accepts OpenAI Responses,
OpenAI Chat Completions, and Anthropic Messages requests, resolves each to one concrete upstream
provider and model, and translates between wires only when the selected upstream does not already
speak the client's format.

## Resolution

Resolution turns a client-facing model selector into `{upstream, native model, route kind, reason,
decision trace}`:

1. An explicit `provider/model` selector, or a unique configured alias, wins over the default.
2. A configured combo selects from its member targets, applying the combo's strategy and skipping
   disabled or cooling members.
3. A smart-switching group routes across providers that expose the same model by health and
   cooldown rather than by position.
4. A bare model id falls back to the configured default upstream. An ambiguous selector fails with a
   clear error instead of choosing arbitrarily.

Credential resolution happens before the upstream request is built, and the destination is validated
before a credential-bearing request is sent. Upstream keys are sealed with the same current-user
encryption as delivery channels and are write-only in the interface.

## Translation

A codec owns each wire. Responses, Chat Completions, and Anthropic Messages are normalized to one
internal request shape when translation is required, and the upstream response is reframed to the
client's original wire. Same-wire traffic is passed through unchanged, streaming included. Request
and response bodies are bounded, and a stream that has already delivered bytes is never retried as
though nothing was sent.

## Remaining work

Policy-scored selection over capability, health, quota, cost, and latency evidence with a bounded
redacted decision trace; weighted and least-used combo strategies; Codex account pools; Gemini and
Ollama-native wires; and pricing on ledger rows.
