# Live quota: normalization, publication, and refresh safety

The common window shape is used percentage, optional duration, reset time, and derived remaining
percentage. Mapping is explicit per provider: a reset timestamp becomes the window's reset time,
and a duration is carried in minutes. Credits, monthly spend limits, and model-specific limits stay
in their own fields — they are never collapsed into the primary percentage, because a five-hour
window and a weekly window can both gate the next request.

Snapshots are published, not merely fetched:

- `{provider, account_scope, source, fetched_at, windows[], credits?, confidence}` is the published
  shape.
- A refresh result is applied only if it still matches the account and credential scope that
  requested it, so a slow probe for account A cannot overwrite the display after switching to
  account B.
- Cached, stale, unavailable, confirmed-empty, and a real zero-percent reading are distinct states;
  a transient failure never rewrites known usage as zero.
- A rate-limited response backs off using the provider's own retry hint, and that backoff is per
  token, not global.

`LiveQuotaService` coalesces simultaneous requests for the same account and caches each account for
its configured interval (5–60 minutes). Optional credit enrichment is tied to the same credential
snapshot that produced the quota result, so an enrichment failure cannot change the quota reading.
