# How the usage indexer keeps usage current and prices it

The indexer is a single in-process owner of parsed files and an immutable-ish scan snapshot. `getCachedScan()` initializes it once, then consumers use the snapshot. Each file entry records provider, parser version, mtime, size, parsed assistant/user rows, parent links, and spawned-session links. On startup, an entry from the persisted index is reused only if provider, `parserVersion`, mtime and size all match; otherwise it is reparsed. Deleted files are removed from the map.

Scanning uses bounded lanes (`max(8, min(32, CPU count × 4))`), preventing thousands of simultaneous open streams. Recursive `fs.watch` events are debounced 200 ms per file, snapshot rebuilds 100 ms, disk persistence 2 s, and a 30 s poll can reconcile missed watcher events. Index mutations are serialized through a promise tail, so a watch event and full rescan cannot publish conflicting snapshots. The cache is JSON at `~/.the usage indexer/cache/index-v2.json` by default (or `INDEXER_STATE_DIR`), written to a temp file and renamed. Parser-version changes invalidate stale parsed data even if the source file is unchanged.

The snapshot build concatenates cached rows, deduplicates assistant/user rows, links subagent parentage, sorts timestamps, and computes source counts. The pure aggregator then filters by source/time/model/project and groups by local hour/day/week/month, model, project, or session. `costOfRecord()` is applied to each normalized assistant row before rolling costs up.

## Cost mechanics

The pricing store overlays a bundled LiteLLM snapshot with refreshed pricing; Claude and Codex adapters resolve exact model IDs first, then date/prefix-normalized IDs, then family fallbacks. Fallback matches are estimates; unresolved pricing yields zero in this implementation, so a new app should additionally mark `price_unknown` to avoid presenting zero as free.

The cost formula prices five buckets at per-million-token rates:

```text
cost = uncached_input × input_rate
     + output × output_rate
     + cache_creation_5m × cache_creation_5m_rate
     + cache_creation_1h × cache_creation_1h_rate
     + cache_read × cache_read_rate
all products divided by 1,000,000
```

When detailed cache-creation buckets are absent, aggregate cache creation is charged at the 5-minute rate. Cache savings are estimated as `cache_read × (input_rate - cache_read_rate) / 1,000,000`. The Codex adapter can scale pricing for Fast/priority tier based on active Codex configuration and model; that is a cost assumption, not a provider invoice.

`computeBlocks` starts a five-hour window at the first record, accumulates records until one falls outside it, then starts another. Its progress/burn-rate projection is derived from local token events. It is **not** an authoritative subscription reset window or quota percentage; use a live quota source for that.
