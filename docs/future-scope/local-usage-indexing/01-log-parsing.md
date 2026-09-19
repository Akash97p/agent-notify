# Local usage indexing: obtaining records

AgentNotify's usage view never queries a provider billing API. `LocalUsageService` in Core discovers
the records each agent already writes, parses usage-bearing lines, and emits a normalized record
shape that the aggregator and the price catalog share. One adapter exists per source, and each owns
its own discovery, parsing, and deduplication rules.

## Claude Code: `message.usage` is the ledger

Discovery covers `~/.claude/projects`, `~/.config/claude/projects`, and configured overrides, with
`/projects` appended. The JSONL parser streams lines, ignores malformed JSON, and retains assistant
entries only when `message.usage` and a non-synthetic `message.model` are present.

Token mapping is direct: `message.usage.input_tokens`, `output_tokens`,
`cache_creation_input_tokens`, and `cache_read_input_tokens`. Detailed caching is retained from
`cache_creation.ephemeral_5m_input_tokens` and `ephemeral_1h_input_tokens` so the two cache-write
tiers can be priced separately. Each retained record also carries timestamp, session ID, working
directory, message ID, request ID, and model, which is what makes project grouping and
deduplication possible without reading message text.

One API answer can appear as several streamed assistant rows sharing `message.id` and `requestId`.
Deduplication keeps the earliest usage row for that identity and ignores the repeats, so a streamed
answer is counted once.

## Codex: turn usage is a counter delta

Discovery covers `${CODEX_HOME:-~/.codex}/sessions` and `archived_sessions`, plus the WSL homes of
running distributions. The relevant rollout event is `event_msg` with `payload.type: "token_count"`.

`total_token_usage` is cumulative per rollout, so the parser subtracts the previous total per field,
clamps negative differences to zero, and ignores an unchanged sample. `last_token_usage` is used
once when a cumulative total is missing. `cached_input_tokens` is a subset of input, so uncached
input is `input_tokens - cached_input_tokens` (saturated at zero) and cache read is
`cached_input_tokens`. Codex output already includes reasoning output, so reasoning is retained for
display and never billed again. `turn_context` supplies the authoritative per-turn model, with
`thread_settings_applied` as the fallback and as the source of the Fast service tier.

Rollouts can replay parent history, so records marked as a fork mirror are dropped rather than
counted twice. Replayed prefixes in user-initiated forks are a known gap.

## Remaining work

A durable, versioned file index with scan progress; a parser contract that records `source`, session
identity, counters, and provenance flags per record; and per-source counting rules chosen before
aggregation. Malformed-line handling stays local to each file so one bad line cannot lose the rest
of a session.
