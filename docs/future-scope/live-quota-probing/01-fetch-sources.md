# How the quota monitor gets live usage and quota

The quota monitor treats usage as a provider/account snapshot obtained through a selected fetch strategy. It is not estimating quota from local JSONL. The fetching implementation is in a shared core library, shared by the macOS app and macOS/Linux Swift CLI. The repo also has a Qt 6 Linux desktop that calls the CLI. The [native Windows Rust port](03-windows-provider-sources.md) is documented on its own terms. `CodexUsageDataSource` exposes `auto`, `pat`, `oauth`, and `cli`; the Codex provider descriptor implements strategy availability, ordering, and fallback rules. The winning result carries a source label and account identity.

## Codex OAuth endpoint

`CodexOAuthCredentialsStore` reads the active `${CODEX_HOME:-~/.codex}/auth.json` once, obtaining access/refresh tokens and a ChatGPT account ID. `CodexOAuthUsageFetcher.fetchUsage` sends `GET https://chatgpt.com/backend-api/wham/usage` by default, with `Authorization: Bearer <access token>`, `Accept: application/json`, and `ChatGPT-Account-Id` when known. A configured alternate base can use `/api/codex/usage`; URL selection is in `resolveUsageURL`, not the decoder.

The response decoder reads `plan_type`, `account_id`, `rate_limit.primary_window`, `secondary_window`, `credits`, optional `additional_rate_limits`, and spend-control limit variants. A window supplies `used_percent`, Unix `reset_at`, and `limit_window_seconds`. The decoder tolerates missing/malformed *additional* limits without discarding primary/secondary; it records decode failure so the resulting snapshot can carry unknown confidence. Credit-limit precedence is root `individual_limit`, then `rate_limit.individual_limit`, then `spend_control.individual_limit`.

Credential ownership is important. The OAuth strategy refuses to redeem a stale refresh token from Codex-owned `auth.json` itself: Codex CLI owns rotating and writing that token. A native CLI refresh can be attempted only when it can preserve account scope. External auth files are read-only. A 401 may qualify for a narrowly scoped fallback; generic network/decoding failures do not cause repeated CLI launches.

## Codex PAT and CLI sources

The PAT fetcher first calls `https://auth.openai.com/api/accounts/v1/user-auth-credential/whoami` with the PAT, then calls the same Codex usage endpoint. It uses the **whoami account ID** for `ChatGPT-Account-Id`; a stale account ID from another workspace would query the wrong account. Its request also carries a Codex CLI-style user agent and `originator` header.

The CLI/RPC source talks to a Codex app-server session and requests `account/read` plus `account/rateLimits/read`. It maps RPC rate-limit windows and credits into the same snapshot type; a TTY fallback can parse percentage remaining and convert it to used percentage (`100 - percent_left`). This source can refresh native Codex credentials through the owning CLI but cannot safely masquerade as a separately selected managed-workspace account.

## Claude OAuth source

`ClaudeOAuthUsageFetcher` sends `GET https://api.anthropic.com/api/oauth/usage` with the credential bearer token, `anthropic-beta: oauth-2025-04-20`, and a Claude Code user agent. It parses `five_hour`, `seven_day`, scoped weekly windows, the newer `limits` list, and optional extra usage. On 429 it records a per-token rate-limit gate using `Retry-After` when available. `ClaudeUsageFetcher` turns `utilization` and `resets_at` into normalized windows; if the primary window is missing but a spend limit exists, it marks that window as a spend limit rather than pretending it is usage quota.

These endpoints are implementation dependencies, not a stable cross-provider contract. In a new app, isolate each behind a versioned fetcher and response fixtures, and keep access tokens out of persisted usage snapshots and logs.
