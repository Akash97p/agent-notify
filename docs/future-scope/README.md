# Future scope: usage, quota, and routing

The read-only Claude Code, Codex, and OpenCode local usage view now includes project groups and a
dated API/Go token-rate estimate. A separate live quota view now shows named Codex and Claude Code
profile windows and a clearly labeled local OpenCode Go per-model estimate for 20 verified
fixed-rate model IDs. Recent-session grouping
is also in the Usage view, and the Insights dashboard combines the existing summaries without
mixing their provenance. A first local provider
router now ships behind its own switch ([ROUTER.md](../ROUTER.md)): routing, protocol translation,
failover, and a proxy-observed ledger. Durable indexing, broader provider quota sources, and
policy-scored routing remain unscheduled.
These notes record how four capabilities
work, so that when AgentNotify grows past human attention into the cost and capacity of the agents
it watches, the design starts from known mechanisms instead of a blank page.

The notes describe mechanisms, not products. Provider endpoints and log formats are other
programs' implementation details and change without notice; re-verify each one against the real
source before building on it.

| Capability | What it would measure | Notes |
| --- | --- | --- |
| Local usage indexing | WebUI view: local Claude Code, Codex, and OpenCode tokens by source, provider, project, model, day, and recent session, with current published token-rate estimates, including 20 fixed-rate Go models. Durable index, historical pricing, and cache savings remain. | [Log parsing](local-usage-indexing/01-log-parsing.md), [index and cost](local-usage-indexing/02-index-and-cost.md) |
| Multi-agent usage adapters | Historical usage from each agent's own log or database (Claude Code, Codex, OpenCode) is projected into the Usage view. Richer normalization, fork replay handling, and pricing remain. | [Source adapters](multi-agent-usage-adapters/01-source-adapters.md), [normalization and pricing](multi-agent-usage-adapters/02-normalization-and-pricing.md) |
| Live quota probing | WebUI view: current and named Codex/Claude profiles, with per-account windows, reset times, optional credits, freshness and unavailable state. OpenCode Go receives a separate local-only per-model estimate. Additional provider sources remain. | [Fetch sources](live-quota-probing/01-fetch-sources.md), [normalization and refresh](live-quota-probing/02-normalization-and-refresh.md); native Windows engine: [provider sources](live-quota-probing/03-windows-provider-sources.md), [normalization and cache](live-quota-probing/04-windows-normalization-and-cache.md) |
| Local provider routing | **Partly implemented.** Routing, Responses/Chat/Anthropic translation, ordered failover, and a proxy-observed ledger ship in [ROUTER.md](../ROUTER.md); policy- and quota-aware selection, richer combo strategies, and ledger pricing remain. | [Routing and translation](local-provider-routing/01-routing-and-translation.md), [failover and usage ledger](local-provider-routing/02-failover-and-usage-ledger.md) |

## Three records that must stay separate

**Historical token usage**, **live account quota**, and **proxy-observed usage** overlap but are
not interchangeable:

- A five-hour group of log records is a time bucket of observed tokens, not a percentage of a
  subscription allowance.
- A proxy ledger sees only the traffic that went through the proxy.
- A live quota endpoint can include usage from other devices and clients, but generally has no
  per-request token ledger.

A common envelope for all three: `source`, `provider`, `account_scope`, `model`, `observed_at`,
`period_start`/`period_end`, `origin` (`local_log | local_db | provider_api | cli_rpc | proxy`),
`confidence` (`reported | calculated | estimated | unknown`), and `raw_reference` (file and event
ID, or a redacted endpoint or response ID). Token counters and quota percentages belong in distinct
payload types. Normalize counters before pricing, especially whether `input_tokens` already
includes cache reads and whether reasoning is already inside `output_tokens`.

Never add rows from two scanners of the same logs. Either make one parser the authority for a log
family, or deduplicate on stable provider, session, message, and request identities. Likewise,
never count a proxied request and the agent-log record of the same physical call twice. Scope
account identity to the credential that supplied the data, and invalidate any pending refresh when
the account changes.

## Build order

1. A typed source adapter interface, one normalized token schema, and fixture-based parser tests.
   Start with Claude Code JSONL, Codex JSONL, and OpenCode SQLite.
2. Local index invalidation and durable caching. Version each parser, and test cumulative versus
   delta accounting and replayed or sidechain sessions.
3. Pricing as a separate, versioned estimate layer. Unknown pricing stays unknown; it is never
   treated as free.
4. Live quota fetchers with explicit credential ownership, account-scoped snapshots, stale-response
   guards, and rate-limit handling. Named Codex/Claude profiles and a local-only OpenCode Go
   estimate are implemented; broader sources and a stable Claude integration remain.
5. A local routing proxy only if routing is actually wanted, with its own ledger correlated to
   log-derived records where possible. The first one is implemented; its ledger is not yet correlated
   with log-derived records, and its rows are not priced.

## How it would fit AgentNotify

These are directions, not decisions. The standing decisions in
[ARCHITECTURE.md](../ARCHITECTURE.md) still apply to all of them.

- **Where it runs.** Each capability would be a broker module in Core behind the same boundaries as
  delivery: SQLite as the source of truth, versioned migrations, credentials encrypted before
  storage, and no network work on the API request path. Reading another agent's logs is local and
  read-only; live quota probes run only when the owner opens or refreshes the Quota page, while a
  routing proxy remains off until explicitly enabled.
- **Where it shows.** The [web interface](../WEB_UI.md) now has Dashboard, Usage, and Live quota under Insights.
  Routing can sit alongside Channels. Its local `/ui/api` convention and
  write-only secrets carry over.
- **How it meets attention.** Quota and spend thresholds are natural attention requests: "Claude
  weekly limit at 90%, resets Thursday" is an ARC `request.created` with a stable key that updates
  and resolves like any other condition, and routes to the phone through the existing delivery
  pipeline.
- **What never happens.** AgentNotify does not rotate or write another tool's credentials. A
  credential file owned by an agent is read-only input; refreshing it is that agent's job.
