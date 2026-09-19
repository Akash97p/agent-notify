# Live quota on Windows: normalization and cache

Windows uses the same normalized snapshot as macOS and Linux. What this note records is how the
broker keeps the display honest when several sources, accounts, and refresh timers coexist.

## Normalization rules

- Windows are classified by duration rather than by position in the response, so a session window, a
  weekly window, and a monthly window keep separate identities.
- A weekly-only response produces an informational primary entry instead of a fabricated zero-percent
  five-hour window.
- Empty placeholder windows are skipped.
- A credit balance is a separate payload; it is not folded into the quota percentage and is not
  treated as spend.
- When a fresh response omits a reset time, a still-future reset time from the cached window for the
  same account may be backfilled. The fresh percentage is never rewritten from cache.

## Cache and failure policy

Every refresh belongs to a generation, and results from a superseded generation are dropped before
they reach the display. On provider or account change the generation is invalidated; a late probe
for the previous account cannot publish. Per-provider failure policy decides whether a transient
failure keeps the last good snapshot, keeps it once with a stale marker, or replaces it with an
unavailable state, and `fetched_at`, last success, account scope, and error state stay distinct so a
preserved value cannot be mistaken for a fresh probe.

## Provenance

Provider-reported quota and local estimated spend are different record types and are never added
together. The quota probe measures current allowance; the local estimate measures observed activity
from agent records. The dashboard shows both, labelled, in separate blocks.
