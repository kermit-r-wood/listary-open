# Indexing pipeline A/B benchmark

Recorded on 2026-07-15 against the working tree based on commit `4964e48`. The benchmark is a standalone Release tool; it does not run as part of the application or packaging.

## Reproduce

```powershell
dotnet run -c Release --project tools/ListaryOpen.IndexingBenchmark -- `
  --records 20000 `
  --pipeline-records 4096 `
  --sqlite-records 5000 `
  --progress-batches 100000 `
  --repeats 7 `
  --warmups 2 `
  --producer-delay-ms 5 `
  --json docs/indexing-pipeline-benchmark-2026-07-15.json
```

Raw measurements, configuration, and environment metadata are in [indexing-pipeline-benchmark-2026-07-15.json](indexing-pipeline-benchmark-2026-07-15.json). The corrected tail-parser measurements immediately before and after its allocation optimization are also retained as [before](indexing-pipeline-tail-before-2026-07-15.json) and [after](indexing-pipeline-tail-after-2026-07-15.json) evidence.

Test machine: Intel Family 6 Model 94, 8 logical processors, Windows 10.0.19045, x64, .NET 8.0.29, workstation GC. Values below are medians of seven measured runs after two warmups.

## Compared implementations

| Scenario | Baseline/ablation | Current optimization |
| --- | --- | --- |
| Async enumeration | `await Task.Yield()` after every record, matching the prior providers | Inline async-iterator handoff with no per-record scheduler hop |
| Record writer | Default `StreamWriter` buffer and flush only on disposal, matching `HEAD` | 64 KiB UTF-8 writer and flush every 256 complete JSONL records |
| Helper pipeline | Finish writer/helper, then open and parse the complete JSONL file, matching `HEAD` | Current production writer plus current production `ElevatedIndexerClient` tail parser while the helper is running |
| SQLite upsert | Create and parameterize a new command for every record inside one transaction, matching `HEAD` | Reuse one typed, prepared command inside the same transaction |
| Progress dispatch | Publish writing and scanning events for every batch | Production-equivalent 200 ms throttle; deterministic ablation clock advances 1 ms per batch |

The helper pipeline uses an in-process fake process so that both variants see the same deterministic producer and UAC/process-start noise is excluded. The producer pauses 5 ms after each group of 256 records; this makes first-record visibility repeatable while retaining the production JSONL format, flush boundary, client parser, and record validation.

## Results

| Scenario | Variant | Median total | Throughput | Allocated | Bytes/op | First record |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Async enumeration | Baseline per-record yield | 12.24 ms | 1,634,281/s | 0.002 MiB | 0.10 | 0.01 ms |
| Async enumeration | Current inline | **1.14 ms** | **17,568,517/s** | **0.001 MiB** | **0.07** | 0.01 ms |
| Record writer | Baseline default buffer | **17.64 ms** | **1,133,980/s** | **7.48 MiB** | **392.4** | n/a |
| Record writer | Current 64 KiB + flush/256 | 19.28 ms | 1,037,231/s | 7.82 MiB | 409.8 | n/a |
| Helper pipeline | Baseline wait, then read | 306.99 ms | 13,342/s | 5.33 MiB | 1,365.1 | 294.07 ms |
| Helper pipeline | Current UTF-8 tail while running | **299.17 ms** | **13,691/s** | **4.08 MiB** | **1,045.0** | **2.79 ms** |
| SQLite upsert | Baseline command per row | 1,563.54 ms | 3,198/s | 22.74 MiB | 4,769.0 | n/a |
| SQLite upsert | Current prepared reuse | **1,222.17 ms** | **4,091/s** | **13.66 MiB** | **2,865.4** | n/a |
| Progress dispatch | Unthrottled ablation | 2.19 ms | 45,572,620 batches/s | 9.16 MiB | 96.0 | n/a |
| Progress dispatch | Current 200 ms throttle | **0.07 ms** | **1,474,926,254 batches/s** | **0.05 MiB** | **0.49** | n/a |

## Interpretation

- Removing per-record `Task.Yield` improved iterator throughput by about **10.8x** in this isolated scan loop. Absolute allocation is already small, but the optimized path also reduced it.
- Streaming reduced median first-record latency by about **99.1%** (294.07 ms to 2.79 ms) and improved same-run end-to-end throughput by about **2.6%**. This directly addresses the period where indexing appeared to make no progress.
- The tail parser now finds JSONL boundaries in pooled UTF-8 byte buffers and deserializes each complete record directly from UTF-8. It avoids whole-chunk UTF-16 conversion, `pending + new string(...)`, and substring materialization. Against the corrected pre-optimization run, current allocation fell from **1,746.5 to 1,045.0 B/record** (about **40.2%**) without losing first-record latency. In the final same-run A/B it also allocates **23.5% less** than the wait-then-read baseline.
- Periodic writer visibility still has a deliberate isolated cost: flush/256 was about **9.3%** slower and allocated **4.4%** more than flush-on-dispose in this run. The 256-record interval is retained because it is the bound that makes a slow producer visible; increasing it only to improve the synthetic writer microbenchmark would weaken the latency guarantee.
- Prepared-command reuse reduced SQLite batch time by about **21.8%** and allocation by about **39.9%**, with the same schema, FTS triggers, transaction boundary, rows, and aggregate checksum.
- With the deterministic one-batch-per-millisecond ablation, the 200 ms progress throttle reduced dispatched statuses from 200,000 to 1,001 (**99.5%**) while preserving the final indexed count. This protects the UI dispatcher without hiding long-running indexing progress.

## Semantic guards and limits

Every measured variant fails the run if its record count or deterministic size checksum changes. Stream variants additionally validate first and last path ordering; writer output is parsed as JSONL after timing; SQLite variants verify row count, sum, minimum, and maximum. Repository indexing tests are still the authoritative failure, cancellation, malformed-output, and transaction semantic checks.

Validation performed after the measured run:

```powershell
dotnet build tools/ListaryOpen.IndexingBenchmark/ListaryOpen.IndexingBenchmark.csproj -c Release --no-restore
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj `
  -c Release --no-restore --filter "FullyQualifiedName~Indexing"
```

The benchmark build completed with 0 warnings and 0 errors. The final indexing regression set also covers an empty, completely scanned NTFS root: after post-scan journal completeness is proved, stale records are pruned and the checkpoint is saved even when the scan yielded zero records. CRLF and multi-byte UTF-8 records spanning the 64 KiB tail buffer boundary are covered as well.

The benchmark isolates code-path costs. It does not model UAC consent, process startup, real NTFS/USN I/O, antivirus interference, storage contention, or UI dispatcher load. `GC.GetTotalAllocatedBytes` is process-wide, so the tool runs standalone and reports medians to reduce noise. The progress comparison is an ablation model rather than a historical implementation because `HEAD` did not emit per-batch progress.
