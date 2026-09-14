# How the usage reader reads agent usage

The usage reader is a Rust-first scanner. The production parsing logic lives in `rust/adapters/<agent>`, with common JSONL/report helpers in `rust/adapters/common` and normalized types/pricing in a shared core crate. Each adapter owns path discovery, source parsing/loading, and report-specific aggregation. The npm app is a launcher, not the parser.

## Claude JSONL adapter

`claude_paths()` discovers `~/.config/claude/projects` and `~/.claude/projects`, or comma-separated `CLAUDE_CONFIG_DIR` paths; it recursively collects both flat and nested session JSONL. `load_entries_inner()` reads files in parallel, parses usage-bearing assistant records, then deduplicates across files before reporting. The daily fast path first byte-scans for `"usage":{`, directly deserializes only matching lines, and groups by date in the requested timezone. It also unwraps `agent_progress` envelopes, so nested agent usage is not silently missed.

The usable ledger is `message.usage`, with `message.model`, `message.id`, `requestId`, timestamp, session and project identity. Stored `costUSD` may be used depending on cost mode. Dedupe first matches `(message_id, request_id, effective_session_id)`. Sidechain replay can have the same message ID but a different request ID; if a duplicate is a sidechain, the parent entry wins. Distinct sidechain responses still count. The regular loader requires matching timestamps for this looser replay match; the daily path has its own documented matching rule. Advisor spend inside `message.usage.iterations` is counted as separate `advisor_message` model entries; the top-level usage remains assigned only to its main model.

## Codex rollout adapter

`codex_usage_sources()` reads `${CODEX_HOME:-~/.codex}/sessions` and `archived_sessions`; duplicate relative paths within one home favor the active session copy. `parser.rs` accepts `event_msg`/`token_count`, prefers `payload.info.last_token_usage` **only when the cumulative total advanced**, otherwise subtracts `total_token_usage` from the prior total. Repeated `last_token_usage` with unchanged total is ignored. The parser bounds cached tokens to total input, preserves `reasoning_output_tokens` as an output subset, and resolves model from `turn_context` (older metadata can fall back to a marked approximate model). `thread_settings_applied.service_tier` carries Fast/Standard classification when emitted.

The replay planner relates child rollouts to parents and removes replayed usage prefixes. MultiAgent V2 boundaries may be `task_started` and `inter_agent_communication_metadata`/legacy `inter_agent_communication` with `trigger_turn: true`; the adapter scans for the marker rather than guessing from CLI version. This is essential to avoid counting the same parent history twice.

## OpenCode SQLite adapter

The implemented `LocalUsageService` opens `opencode.db` read-only under `${OPENCODE_DATA_DIR}` or `${XDG_DATA_HOME:-~/.local/share}/opencode`. It reads the current `message` table's assistant rows and joins `session.directory` for project grouping. The SQL extracts only usage scalars, so prompt and response payloads do not cross into the broker's report path. It queries the live database on each refresh, and never uses cumulative session aggregates for a date-filtered report.

It maps `tokens.input`, `tokens.output`, `tokens.cache.read`, and `tokens.cache.write` into the common record. OpenCode's separate `tokens.reasoning` is added to billed output once and retained as an informative counter. `modelID` and `providerID` stay distinct so similarly named models from different providers cannot share a price. Malformed JSON and non-assistant rows are skipped. Stored `cost` is not used: the WebUI explicitly calculates a dated token-rate estimate.

The proposed `session_message` and legacy JSON fallback paths remain future work if the installed OpenCode schema changes; the current pre-release implementation uses only the observed `message` shape rather than maintaining multiple compatibility paths.

## Extending the design

An adapter should expose `detect source → enumerate files/database rows → parse events → normalize counters → deduplicate → aggregate`. Detection must be cheap and independent of the user's report date filter. Prefer reading only fields required for accounting, and include fixture cases for legacy and current schemas. The common JSONL helper demonstrates a byte-line prefilter followed by direct typed deserialization; ensure the prefilter cannot reject a valid usage row.
