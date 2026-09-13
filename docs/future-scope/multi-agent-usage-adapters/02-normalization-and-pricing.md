# the usage reader normalization, cost, and reporting

The shared core types are the boundary between source-specific log shapes and reports. Adapters produce normalized token buckets, model, timestamp, session/project identity, optional reported cost, and source-specific extra fields. The source adapters perform deduplication *before* grouping, so report totals do not depend on whether the user requests daily, monthly, session, or model output. Date grouping uses the requested timezone, while pricing lookup can use the event timestamp to choose scheduled rates.

`calculate_cost_for_usage_at` has three explicit semantics:

| Mode | Cost selection |
| --- | --- |
| `display` | Use the source's `costUSD`; absent means zero displayed. No price table is loaded. |
| `auto` | Use `costUSD` when present, otherwise calculate from counters and model pricing. |
| `calculate` | Ignore `costUSD` and calculate from counters and model pricing. |

The price map is a bundled, compressed snapshot with optional refresh and user overrides (`pricing.rs`). Models.dev rates are converted from per-million-token to per-token rates before core calculation; output/input/cache-read/cache-creation are separate. Model aliases and time-specific rates are resolved before multiplication. A missing price is tracked as a missing-pricing model in report data; do the same in a combined app instead of silently interpreting zero as free.

For Claude-shaped usage, the calculator uses uncached input, output, cache read, 5-minute cache creation and 1-hour cache creation. If detailed cache creation is absent, all creation is assigned to the 5-minute bucket. One-hour creation is priced at `2 × input` when that tier applies. Long-context pricing chooses the rate tier from the **whole prompt context** (uncached + cached + created), not just new input. A Fast service tier can multiply the selected rate. The Codex adapter has additional source-specific pricing/report logic, including per-model speed handling; it does not re-add reasoning output to billed output.

The practical reusable design is to keep three values separate: `reported_cost_usd`, `calculated_cost_usd`, and `cost_source`. That lets users compare a provider-stored cost with your estimate, update price tables without changing raw events, and show a clear `unpriced` state. Normalize inclusive input counters into non-overlapping buckets before cost calculation; if a source reports `input` including cache, subtract cache from input once, with saturation. Keep reasoning as metadata unless the source proves it is an extra billable bucket.

The usage reader processes local historical records. It does not obtain live subscription headroom or reset timers from the logs; that part belongs to a live quota probe such as the quota monitor's.
