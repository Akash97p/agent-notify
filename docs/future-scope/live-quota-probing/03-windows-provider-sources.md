# How the native Windows port gets quota data

This document describes a **separate, native Windows implementation** of the quota monitor: a Rust provider crate and CLI, with a Tauri 2 desktop shell and React 18/Vite frontend. Its live quota data comes from the Rust crate, not from the original project's Swift a shared core library, and it does not need WSL2. The root Cargo workspace makes the Tauri shell depend on that crate. The code path is factory → `Provider::fetch_usage` → provider-specific HTTP/credential parser → normalized snapshot. See [refresh and normalization](04-windows-normalization-and-cache.md) for publication rules.

## Codex: native credential read and HTTP probe

`CodexApi::codex_dir` uses `CODEX_HOME` when set, otherwise `~/.codex`. It reads `auth.json` and optionally `config.toml` for a custom `chatgpt_base_url`. The OAuth token is taken from `tokens.access_token`, with `tokens.account_id`; a top-level `OPENAI_API_KEY` is another recognized credential shape. A five-second cache is keyed by auth-file path and modification time. The port treats a `refresh_token` as Codex CLI-owned OAuth and checks `last_refresh` plus access-token expiry. Stale external OAuth sources fail closed by default; the usage probe does not silently rotate Codex's credential file.

`CodexProvider::fetch_usage` first tries a personal access token in `Auto` mode when one exists, falling back to OAuth only for missing/auth-required PAT errors. `pat.rs` reads `personal_access_token` or `personalAccessToken` from `auth.json`, calls `https://auth.openai.com/api/accounts/v1/user-auth-credential/whoami`, and uses the returned ChatGPT account ID in its subsequent usage request. This avoids applying a token to the wrong account. Other PAT failures surface as errors instead of silently switching credentials.

The OAuth HTTP fetch sends `GET https://chatgpt.com/backend-api/wham/usage` by default, with bearer access token and `ChatGPT-Account-Id` when available. It optionally calls `/wham/rate-limit-reset-credits`; failure of that enrichment does not discard the usage response. It parses `rate_limit.primary_window`, `secondary_window`, `code_review_window`, `additional_rate_limits`, and `credits`. It also accepts a `rate_limits` array or direct percentage fields. Windows' `SourceMode::Cli` is listed as available, but this `fetch_usage` implementation still calls the same direct OAuth HTTP method for that mode. Do not infer a Swift CLI/app-server RPC implementation from the mode name.

## Claude: separate authenticated sources

`ClaudeProvider` implements `Auto`, `OAuth`, `Web`, and `Cli`. Auto tries an eligible Admin API source, then Web, then OAuth, then CLI. The OAuth fetcher sends `GET https://api.anthropic.com/api/oauth/usage` with bearer token and `anthropic-beta: oauth-2025-04-20`. It decodes five-hour, seven-day, scoped weekly, `limits`, and extra-usage fields. It checks OAuth scope, distinguishes revoked from expired tokens, and backs off after 429 using `Retry-After` when present.

The Web fetcher takes a `sessionKey` from environment, a validated cookie cache, or browser-cookie extraction. It discovers the Claude organization ID via `/api/organizations`, then gets `/api/organizations/{id}/usage`; extra usage, credits, and account info are optional enrichment. A selected manual Claude cookie can take precedence over an active OAuth token account in the Tauri fetch-context builder. The winning source is recorded in the provider result, so the integration can retain provenance.

For a combined app, use one adapter per source with its own credential owner, account identity, response fixture, and fallback policy. These are provider-reported quota probes; they are not token totals reconstructed from local logs.
