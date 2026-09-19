# Router: failover and the proxy-observed ledger

## Attempts and failover

A combo attempt list is built from the combo's eligible members in strategy order. A member is
excluded when it is disabled, unusable, or cooling. A failure is classified before the next hop:
a request-shape error fails the request, while a target-specific failure moves to the next member.
A cooling period can come from the upstream's own retry hint, a quota reset, or a configured
fallback duration. Selection state is committed only for the active configuration generation, so a
concurrent settings change cannot resurrect a stale target list.

## Ledger

The router keeps its own SQLite ledger, separate from Usage and Live quota. One row per logical
request records request id and time, selected upstream and model, inbound protocol, response status,
usage status (`reported`, `estimated`, `unreported`, `unsupported`), token counts when the upstream
reported them, the redacted route trace, and per-attempt upstream/model/status/duration. Prompts,
responses, headers, keys, and provider error bodies are never stored.

Proxy-observed usage has a precise blind spot: it covers requests that passed through the router,
and its status may be estimated or unreported. It is not a substitute for local-log totals or
account-level quota snapshots, and the three are shown with their own provenance rather than summed.

## Remaining work

Price ledger rows using the same catalog as the Usage view, correlate router rows with log-derived
records without double counting, raise spend and quota thresholds as ARC attention requests, and
extend connector coverage to OpenCode, Kilo, Cursor, and the Gemini CLI.
