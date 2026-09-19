# Usage adapters: one source per agent

`LocalUsageService` in Core owns discovery, parsing, and aggregation for every supported source. Each
adapter owns path discovery, parsing, and its own deduplication rule; the aggregator and the price
catalog never learn source-specific shapes. Current sources are Claude Code, Codex, OpenCode, Kilo
CLI, Muse Code, and the Gemini CLI, plus the WSL homes of running distributions on Windows.

## Claude Code

Projects are discovered under the configured Claude directories. Session JSONL is read in parallel,
usage-bearing assistant records are parsed, and duplicates are removed across files before
reporting. A record's usable ledger is `message.usage` with `message.model`, message identity,
request identity, timestamp, session, and project directory. When a streamed answer repeats the same
message and request identity, the earliest row wins.

## Codex

Codex sessions are read from `${CODEX_HOME:-~/.codex}/sessions` and `archived_sessions`, with the
active session copy preferred over an archived duplicate. The parser accepts `event_msg` /
`token_count` records, prefers the last-turn usage when the cumulative total advanced, and otherwise
differences the cumulative total. Cached tokens are bounded to the total input, reasoning output is
retained as an output subset, and the model comes from `turn_context` with `thread_settings_applied`
as the fallback and as the Fast service-tier signal. Fork mirrors are excluded so replayed parent
history is not counted twice.

## OpenCode

`opencode.db` is opened read-only under the configured OpenCode data directory. The query selects
only the current `message` table's scalar usage fields and joins `session.directory` for project
grouping, so prompt and response payloads never cross into the report path. The database is queried
afresh on each report rather than cached, and a file on a UNC share is copied into a temporary
directory first because querying SQLite over the share is slow and can fail.

## Extending the design

A new adapter needs: discovery for every platform it supports, a parser with an explicit counting
rule, a deduplication identity, a project/work-directory rule, and fixture tests. Providers that
report inclusive input (input including cached tokens) must be normalized into non-overlapping
buckets before pricing, and a separate reasoning counter is added to output exactly once, or not at
all when the provider already includes it. Malformed records are skipped individually.
