# Live quota: fetch sources

Live quota answers “how much of this account's current allowance is used, and when does that
allowance reset?” It is a provider/account snapshot, never derived from the local usage ledger. The
probe strategy is chosen per provider, and the winning result carries a source label and the account
identity it belongs to.

## Codex

`LiveQuotaService` in Core probes Codex through the documented app-server RPC: an `account/read`
plus `account/rateLimits/read` exchange against a Codex child process started with the selected
`CODEX_HOME`. Rate-limit windows and credits map into the same snapshot shape as the HTTP path, and
a terminal fallback can parse a percentage reading when the RPC is unavailable.

Credentials are owned by the agent, not by AgentNotify. The probe reads `${CODEX_HOME}/auth.json`
and never rewrites or redeems the rotating OAuth token in it; a refresh is at most a narrowly scoped
retry after an authentication failure, never a generic response to a network or decode error.

## Claude Code

A Claude probe reads only the selected profile's `.credentials.json` and calls the first-party
account-usage endpoint with that profile's token. It parses the five-hour window, the weekly window,
scoped weekly windows, the newer limits list, and optional extra usage, and records a per-token
backoff when the endpoint answers with a rate limit.

## Boundaries

These endpoints and RPC shapes are provider implementation details, not a stable cross-provider
contract, so each one stays behind a versioned fetcher with response fixtures. Access tokens never
enter a persisted snapshot, a report, or a log. Which sources are probed, how often, and for which
profiles is owner configuration; nothing is probed until the owner enables it.
