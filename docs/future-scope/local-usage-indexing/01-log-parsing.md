# How the usage indexer obtains local usage

The usage indexer does not query the provider billing APIs. Its adapters discover Claude Code and Codex rollout files, stream JSONL, and emit a shared `AssistantRecord`/`UserRecord` shape. Each adapter implements one provider contract and is registered in one place.

## Claude Code: `message.usage` is the ledger

The Claude adapter looks in `~/.claude/projects`, `~/.config/claude/projects`, and configured overrides (`INDEXER_CONFIG_DIR` / `CLAUDE_CONFIG_DIR`, each with `/projects` appended). The JSONL parser reads lines with a stream, ignores malformed JSON, and keeps assistant entries only when `message.usage` and a non-synthetic `message.model` exist.

The token mapping is direct: `message.usage.input_tokens`, `output_tokens`, `cache_creation_input_tokens`, and `cache_read_input_tokens`. For detailed Claude caching, `message.usage.cache_creation.ephemeral_5m_input_tokens` and `ephemeral_1h_input_tokens` are retained separately. Every retained record also carries timestamp, session ID, cwd, message ID, request ID, parent UUID, model, and tool-use IDs. These fields make project/session grouping and deduplication possible without reading message text as a usage source.

One API answer can appear as several streamed assistant rows with the same `message.id` and `requestId`. `dedupAssistantRecords` keeps the earliest usage row and unions the tool-use references from duplicates. Its key prefers `source:messageId::requestId`, then falls back to message ID, request ID, or UUID. The parser also marks injected `user` records (`isMeta`, sidechains, known synthetic prefixes) so a turn is rooted in the real prompt rather than a skill preamble or task notification. Parent links and turn construction are attribution logic, not a new token source.

## Codex: turn usage is a counter delta

The Codex adapter scans `~/.codex/sessions`, `~/.codex/archived_sessions`, corresponding `CODEX_HOME` directories, and `INDEXER_CODEX_DIR`. In the rollout parser, the relevant event is `type: "event_msg"` with `payload.type: "token_count"` and `payload.info`.

`total_token_usage` is cumulative per rollout. The usage indexer subtracts the previous total **per field**, clamps negative differences to zero, and ignores an unchanged sample. If cumulative totals are missing, it uses `last_token_usage` as a delta. It treats `cached_input_tokens` as a subset of input, so normalized uncached input is `input_tokens - cached_input_tokens`; cache read is `cached_input_tokens`. Codex `output_tokens` already includes `reasoning_output_tokens`: reasoning is retained for display and must not be billed again. `turn_context` supplies the authoritative per-turn model; `thread_settings_applied` is only a fallback when that context has not appeared.

Codex subagent files can replay parent history. The parser excludes subagent fork mirrors marked by both `thread_source: "subagent"` and `forked_from_id`; a non-mirror subagent can still have real spend. It also links kept sidechain turns back to their parent turn. This is a source-specific rule: dropping every subagent or counting every rollout produces wrong totals. The parser documents one known gap: replayed prefixes in user-initiated forks are not fully removed.

## A reusable parser contract

Return normalized usage events with `source`, `file`, `session_id`, `message/request identity`, `timestamp`, `model`, counters, and provenance flags (`sidechain`, `synthetic`, `counter_kind`). The counting rule must be chosen per source before aggregation. Never sum Codex cumulative samples or add Codex reasoning to output. Keep malformed-line handling local to each file so one bad line does not lose the rest of a session.
