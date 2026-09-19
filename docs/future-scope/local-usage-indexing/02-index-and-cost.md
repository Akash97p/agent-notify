# Local usage indexing: index, aggregation, and cost

## Index

The indexer is one in-process owner of parsed files and one immutable scan snapshot. A file entry
records provider, parser version, length, modification time, and parsed rows; an entry is reused
only when provider, parser version, and file identity all match, and deleted files are removed.
JSONL events are cached per path, size, and mtime for the lifetime of the broker. OpenCode's SQLite
database is queried read-only on each report instead of cached, so a live database stays visible.

Scanning is bounded: a limited number of files are open at once, directory walks are not repeated
per report, and the parse step only JSON-parses a line when it contains the marker of a record that
can carry usage. The cache is in memory and is rebuilt after a restart, which is why a cold scan is
expensive on a large history.

**Remaining work:** persist the index, version it, show scan progress, and reconcile filesystem
watch events with a full rescan so a partially written ledger cannot publish a conflicting snapshot.

## Aggregation

The snapshot build concatenates cached rows, deduplicates by record identity, links subagent
parents, sorts timestamps, and computes source counts. The pure aggregator then filters by
source/time/model/project and groups by local day, model, project, or session. Project grouping uses
each row's working directory, active turn directory, or session directory, and the page receives
only a basename and a stable opaque hash — never the full path.

## Cost

The price catalog is a dated, exact-model table keyed by provider and model. The rate is chosen per
record, because each record is one request: its prompt size selects a long-context tier, its service
tier selects a provider's fast rates, and its timestamp selects time-of-day rates where one exists.
Claude 5-minute and 1-hour cache writes are priced separately. A record is **unpriced** when no
published rate covers that combination, and unpriced is shown as unpriced rather than zero.

```text
cost = uncached_input × input_rate
     + output × output_rate
     + cache_creation_5m × cache_creation_5m_rate
     + cache_creation_1h × cache_creation_1h_rate
     + cache_read × cache_read_rate
     (each product divided by 1,000,000)
```

Cache savings are estimated as `cache_read × (input_rate - cache_read_rate) / 1,000,000`. The source
logs remain authoritative: this is a priced estimate at published standard rates, not a provider
invoice, and historical rate schedules, fork replay attribution, and durable indexing remain open.
