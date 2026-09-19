# Future scope: usage, quota, and routing

The read-only local usage view already reports Claude Code, Codex, OpenCode, Kilo CLI, Muse Code,
and Gemini CLI tokens by day, provider, project, model, and recent session, with dated
published-rate estimates. Live quota already reports named Codex and Claude Code profile windows,
WSL profiles, and a clearly labelled local OpenCode Go estimate for verified fixed-rate models.
The Insights dashboard combines those summaries without mixing their provenance, and the first
local provider router ships behind its own switch ([ROUTER.md](../ROUTER.md)): routing, protocol
translation, failover, and a proxy-observed ledger.

These notes record the mechanisms the remaining work will use, so that when AgentNotify grows past
human attention into the cost and capacity of the agents it watches, the design starts from a known
shape instead of a blank page. They describe the AgentNotify broker's own boundaries; provider
endpoints, log formats, and session schemas are third-party implementation details that change
without notice, so re-verify each one against the real source before building on it.

| Capability | What it measures | Notes |
| --- | --- | --- |
| Local usage indexing | Local Claude Code, Codex, and OpenCode tokens by source, provider, project, model, day, and recent session, with current published token-rate estimates. Durable index, historical pricing, and cache savings remain. | [Log parsing](local-usage-indexing/01-log-parsing.md), [index and cost](local-usage-indexing/02-index-and-cost.md) |
| Multi-agent usage adapters | Historical usage from each agent's own log or database, projected into the Usage view. Richer normalization, fork replay handling, and pricing remain. | [Source adapters](multi-agent-usage-adapters/01-source-adapters.md), [normalization and pricing](multi-agent-usage-adapters/02-normalization-and-pricing.md) |
| Live quota probing | Current and named Codex/Claude profiles with per-account windows, reset times, optional credits, freshness, and unavailable state. OpenCode Go receives a separate local-only per-model estimate. Additional provider sources remain. | [Fetch sources](live-quota-probing/01-fetch-sources.md), [normalization and refresh](live-quota-probing/02-normalization-and-refresh.md); Windows and WSL: [provider sources](live-quota-probing/03-windows-provider-sources.md), [normalization and cache](live-quota-probing/04-windows-normalization-and-cache.md) |
| Local provider routing | **Partly implemented.** Routing, Responses/Chat/Anthropic translation, ordered failover, and a proxy-observed ledger ship in [ROUTER.md](../ROUTER.md); policy- and quota-aware selection, richer combo strategies, and ledger pricing remain. | [Routing and translation](local-provider-routing/01-routing-and-translation.md), [failover and usage ledger](local-provider-routing/02-failover-and-usage-ledger.md) |
