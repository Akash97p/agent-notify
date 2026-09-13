# Native Windows port: snapshots, refresh, and local spend

The Windows Rust `UsageSnapshot` stores primary, secondary, model-specific, tertiary, and labeled extra rate windows, plus capture time and optional account/plan/subscription metadata. A `RateWindow` stores `used_percent`, optional duration in minutes, `resets_at`, and an informational flag. Subscription dates are kept separate from quota reset timestamps. A credit balance is represented as a separate cost/credit payload rather than folded into the quota percentage.

## Mapping Codex responses

The Codex parser reads `used_percent`, Unix `reset_at`, and `limit_window_seconds / 60`. It classifies windows by duration rather than trusting their order: session, weekly, and monthly map to distinct lanes; code-review and named additional limits retain separate identities. A weekly-only response gets an informational `no_active_session()` primary instead of a fabricated zero-percent five-hour quota. Empty placeholder windows are skipped. `credits.balance` is captured only when `has_credits` is true and credits are not unlimited; available reset credits are an informational extra window, not spend or usage percentage.

The same Codex fetcher protects against a suspicious early weekly drop to at most 1%: it can issue a second usage request and preserve the last published weekly window if the reset is not confirmed. Its reset-credit inventory is evidence only; observing it does not redeem credits. When a fresh response has no reset time, the Tauri publication code may backfill a still-future reset time from that provider's cached window, leaving the fresh usage percentage unchanged.

## Refresh and credential scope

The Tauri refresh engine loads settings, stored cookies/API keys, and active token accounts into a typed `FetchContext` per provider. It fetches enabled providers concurrently, capped at eight by a semaphore, and wraps each probe in a timeout (35 seconds by default; 75 seconds for Claude, Codex, and Copilot). It stores the resulting bridge snapshots in `AppState.provider_cache` and emits an update event. The timer uses a configurable refresh interval, 300 seconds by default, with optional adaptive and low-power adjustments.

Every refresh batch has a generation. On account changes or provider-setting changes, the generation is invalidated; late results from the old batch are dropped before entering the cache. Provider-specific `LastGoodFailurePolicy` determines whether a transient failure preserves the last good snapshot, preserves it once, or replaces it with an error. The cache logic reuses the previous snapshot for configured failure classes. Keep `fetched_at`, last-success time, account scope, and error state distinct in your own data model so a preserved value cannot be mistaken for a fresh probe.

On Windows, `Settings::load/save` uses the app's config `settings.json`, and `secure_file` writes protected content using Windows DPAPI where available. Manual cookies, API keys, and managed token accounts are separate credential inputs resolved before a fetch. Treat account changes as cache invalidations, not only as a visual selection change.

## Local spend is a different pipeline

The port also calculates *estimated local spend* from agent records. `CostScanner` walks Claude project JSONL files, extracts and deduplicates usage records, and applies pricing. `codex_costs` aggregates Codex local usage records by day/model and prices token categories. This pipeline measures observed local activity; the provider API pipeline measures current account allowance. Store these as different record types and do not add their dollar/credit numbers or percentages together.
