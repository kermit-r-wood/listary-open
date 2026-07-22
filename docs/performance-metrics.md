# Performance metrics

ListaryOpen records one JSON object per search and Quick Switch activation in
`data/performance-metrics.jsonl`. The search panel also shows the latest end-to-end and index time.

## Search stages

| Stage | Meaning | First action when slow |
| --- | --- | --- |
| `ui.debounce` | Wait after the latest keystroke (80 ms after production measurement) | Make adaptive if cancellation volume becomes excessive |
| `query.parse` | Token, filter, and mode parsing | Cache only if this becomes measurable |
| `index.connection_wait` | Wait for the dedicated read connection | Look for overlapping searches; keep cancellation early |
| `index.read_candidates` | SQLite exact/FTS candidate lookup | Check FTS readiness, short-query plans, and candidate limits |
| `candidates.exact` / `candidates.fts` / `candidates.fallback` | Candidate lookup subqueries | Optimize the specific SQL path that dominates |
| `index.read_usage` | Usage boosts for candidate paths | Keep batched lookups and inspect `usage_count` |
| `rank.total` | In-memory fuzzy matching and ranking | Reduce `candidate_count` before micro-optimizing the ranker |
| `ui.merge_results` | Filter, deduplicate, and publish WPF rows | Virtualize/reuse rows if large result sets dominate |

Quick Switch reports `hook.capture_dialog`, `quick_switch.collect_candidates`, and
`quick_switch.show_panel`.

## Reading the data

Use P50 for the normal experience and P95 for stalls. Split by `Operation` and `Outcome`; cancelled
searches should not be mixed into completed-search latency. Correlate high stage time with
`candidate_count`, `usage_count`, `query_length`, and `pinned_count`.

The repository benchmark prints per-stage averages:

```powershell
dotnet run --project tools/ListaryOpen.SearchBenchmark -- data/index.db listary project src
```

Explorer overlay, input, COM, QuickSwitch layout, native HookHost, and ReadyToRun A/B measurements are
documented in [ui-performance-benchmarks.md](ui-performance-benchmarks.md).

On the small checked build artifact, warm-cache queries measured 0.4–1.1 ms end to end, with
candidate reads accounting for 0.35–0.61 ms. The first production trace contained 30 operations:
successful searches had P50 201.31 ms and P95 216.34 ms, while index work had P50 0.78 ms and P95
14.12 ms. The old 200 ms debounce accounted for almost all normal latency, so it was reduced to 80 ms.
For a large, actively indexed database, benchmark a snapshot or stop indexing first so WAL writer contention does
not distort the result.

Detailed first-trace breakdown (12 successful searches, 16 cancelled searches, 2 Quick Switch opens):

| Measurement | P50 | P95 |
| --- | ---: | ---: |
| Successful search total | 201.31 ms | 216.34 ms |
| Search debounce | 200.31 ms | 200.86 ms |
| Index total | 0.78 ms | 14.12 ms |
| Candidate read | 0.59 ms | 6.62 ms |
| UI result publish | 0.12 ms | 0.22 ms |
| Quick Switch total | 24.75 ms | 50.60 ms |
| Hook dialog capture | 2.26 ms | 8.10 ms |
| Quick Switch candidate collection | 20.22 ms | 20.92 ms |

All successful searches in this trace returned zero candidates. It accurately identifies debounce and
empty-result overhead, but it is not sufficient to characterize ranking or UI costs for large result
sets. Collect another trace containing successful hits before tuning candidate limits or ranking.

## Recommended optimization order

1. Gather at least several hundred successful searches and compute P50/P95 per stage.
2. If index P95 stays below 20 ms, reduce debounce to 80–100 ms or run the first query immediately
   and debounce only subsequent keystrokes.
3. If `index.read_candidates` dominates, separate its exact, name-FTS, path-FTS, and fallback SQL
   timings, verify FTS is ready, then tune the 200–5000 candidate window by query length.
4. If `rank.total` grows with `candidate_count`, preselect fewer candidates and cache normalized match
   keys; avoid parallel ranking until allocation and CPU profiles justify it.
5. If `index.connection_wait` grows, coalesce pending queries before they enter SQLite and retain only
   the newest request.
