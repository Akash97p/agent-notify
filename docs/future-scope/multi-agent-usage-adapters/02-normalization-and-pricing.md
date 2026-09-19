# Usage adapters: normalization, pricing, and reporting

The normalized record is the boundary between source-specific log shapes and every report. An adapter
produces token buckets, model, timestamp, session/project identity, an optional provider-reported
cost, and source-specific extra fields; deduplication happens before grouping, so a daily, monthly,
session, or model report always totals the same records.

## Normalization

- Non-overlapping buckets only: if a source reports input including cached tokens, cached input is
  subtracted once, with saturation at zero.
- Reasoning stays metadata unless the source proves it is a separate billable bucket. Codex already
  includes reasoning in output; OpenCode reports it separately and it is added to billed output once.
- `model` and `provider` stay distinct so similarly named models from different providers cannot
  share a price.
- Date grouping uses the requested timezone; pricing lookup can use the event timestamp to choose a
  scheduled rate.

## Pricing

AgentNotify keeps three values conceptually separate — provider-reported cost, calculated estimate,
and the price source — and shows `unpriced` when no verified rate exists. The catalog is keyed by
provider and model with dated, exact entries; aliases and time-specific rates are resolved before
multiplication. Five buckets are priced per million tokens: uncached input, output, cache read,
5-minute cache creation, and 1-hour cache creation. When detailed cache-creation buckets are absent,
aggregate cache creation is charged at the 5-minute rate. Long-context pricing is chosen from the
whole prompt context, not just new input, and a Fast service tier selects the corresponding rates.

## Reporting

The View reports aggregates only. It never returns log paths, prompt text, response text, or
credentials, and project identity is a basename plus a stable opaque hash. Source logs remain
authoritative; the estimate at published standard rates is labelled as an estimate, and records
without a published rate remain visibly unpriced.
