# Indexing CPU and I/O benchmark (2026-08-01)

## Observed failure mode

The existing portable index in `artifacts/ListaryOpen/data/index.db` contained
2,548,792 rows (2,333,748 on `C:` and 215,044 on `D:`), occupied 4.106 GB, and
had no `C:` USN checkpoint. `C:` rows were split across two incomplete scan
generations (1,429,668 in generation 3 and 904,000 in generation 4), while the
FTS state was still `building` and its data blobs alone occupied 1.435 GB.

The highest indexed `C:` MFT segment was 2,966,344, which implies a minimum MFT
logical span of 2.83 GiB with 1 KiB records. The fast scanner narrowed an entire
data-run length to a signed 32-bit integer before reading 4 MiB chunks. A large
extent could therefore overflow and abandon the MFT path, explaining why the
1.05 GiB `D:` MFT completed and checkpointed while `C:` repeatedly fell back to
expensive compatibility traversal.

## Reproduce

```powershell
dotnet run -c Release --project tools/ListaryOpen.IndexingBenchmark -- `
  --records 20000 `
  --pipeline-records 4096 `
  --sqlite-records 20000 `
  --progress-batches 100000 `
  --repeats 3 `
  --warmups 1 `
  --producer-delay-ms 5 `
  --json artifacts/indexing-benchmark-after-2026-08-01.json
```

The benchmark now records wall time, total process CPU time, CPU normalized by
logical processor count, process read/write transfer bytes, allocation, and the
database/WAL footprint. Each SQLite variant verifies row count, size checksum,
and minimum/maximum size after every measured run.

## Identical full-rescan A/B

The scenario seeds 20,000 identical records before timing either path. The
baseline reproduces the old 2,000-row transactions, full-row generation rewrite,
and unconditional FTS rebuild. The current path uses 10,000-row transactions,
generation-only touches for unchanged rows, and preserves an already-correct FTS
snapshot.

| Variant | Median | Throughput | CPU time | Read | Write | DB + WAL |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline rewrite + rebuild | 1,141.88 ms | 17,515/s | 1,031.25 ms | 99.68 MiB | 51.26 MiB | 32.35 MiB |
| Current touch, no rebuild | **470.31 ms** | **42,525/s** | **390.62 ms** | **6.79 MiB** | **5.62 MiB** | **24.49 MiB** |

For an unchanged rescan, median elapsed time fell **58.8%**, CPU time fell
**62.1%**, read transfer fell **93.2%**, write transfer fell **89.0%**, and
throughput improved **2.43x**. The storage footprint fell **24.3%**.

## Production-scale reconciliation probe

The synthetic result was checked against a disposable snapshot of the live
4.11 GB index. The probe sampled 10,000 `C:` records in MFT/file-reference
order and ran each variant five times. Every run verified that all sampled paths
received the expected generation. A scan-output comparison found 9,996 of a
10,000-record batch completely unchanged; only three sizes and four timestamps
differed. This makes the unchanged-row path representative of the actual scan.

```powershell
dotnet run --project tools/ListaryOpen.IndexingBenchmark -- `
  --probe-reconciliation artifacts/indexing-probe/index-live-snapshot.db `
  10000 5 artifacts/indexing-benchmark-live-10000-2026-08-01.json
```

| Variant | Median | Throughput | CPU time | Read | Write | Allocation |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Previous path-key upsert + second touch | 431.26 ms | 23,188/s | 343.75 ms | 16.15 MiB | 7.84 MiB | 17.46 MiB |
| Current file-reference reconciliation | **328.89 ms** | **30,406/s** | **281.25 ms** | **6.64 MiB** | **4.16 MiB** | **13.73 MiB** |

On the production-scale database, elapsed time fell **23.7%**, CPU time fell
**18.2%**, reads fell **58.9%**, writes fell **46.9%**, allocation fell
**21.4%**, and throughput improved **31.1%**. The current query follows the
scanner's natural MFT/file-reference order and performs one narrow indexed touch
for an unchanged row. New or changed rows remain on the full upsert path and are
sorted by path inside the bounded batch.

## Short-query interference probe

One- and two-character pinyin-initial searches previously included
`lower(name) like '%query%'`. On a multi-million-row database this forced a
full files-table scan. A canceled or superseded interactive search could still
occupy SQLite long enough to contend with the indexing writer. Schema version 4
adds a partial covering index containing only nonempty transliteration aliases;
ASCII prefixes continue to use the existing name index.

```powershell
dotnet run --project tools/ListaryOpen.IndexingBenchmark -- `
  --probe-short-alias artifacts/indexing-probe/index-live-snapshot.db `
  ht 5 artifacts/indexing-benchmark-short-alias-2026-08-01.json
```

| Variant | Median | CPU time | Read | Returned rows |
| --- | ---: | ---: | ---: | ---: |
| Previous full files-table scan | 3,398.86 ms | 3,343.75 ms | 1,065.07 MiB | 200 |
| Current sparse alias-index scan | **43.00 ms** | **31.25 ms** | **10.97 MiB** | 200 |

For identical ordered results, median latency fell **98.7%**, CPU time fell
**99.1%**, and read transfer fell **99.0%**. The migration builds this sparse
index once; subsequent keystrokes no longer rescan the full file table.

## Elevated real-volume MFT benchmark

The synthetic database benchmark was followed by an administrator-level scan of
the actual `C:` NTFS volume. `fsutil fsinfo ntfsinfo C:` reported 1,024-byte FILE
records and a 2.83 GB MFT valid-data length. Each helper scanned the same volume
while filtering output to an empty directory under `artifacts/mft-probe-root`, so
the measurement exercised raw-volume reading, parsing, path projection, and
priority behavior without changing the real index database. Process CPU and I/O
counters were sampled by the persistent elevated benchmark session.

| Variant | Result | Elapsed | CPU time | Process read | Process write | Memory observation |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Packaged baseline | `0xE0434352` failure | 5.65 s | 0.13 s | 111 KiB | 0 | Failed before scanning the MFT |
| 64-bit extent fix, retained record map | Success | 520.28 s | 512.28 s | 3.04 GB | 106 B | 1.79 GiB private bytes sampled |
| Streaming projection | Success | 313.69 s | 317.13 s | 3.04 GB | 106 B | 878.6 MiB peak private bytes |
| Streaming + allocation fast paths | **Success** | **191.90 s** | **194.81 s** | **3.04 GB** | **106 B** | **842.6 MiB peak private / 33.6 MiB peak working set** |

The final scan completed in Windows `Idle` background mode. Compared with the
first correct full-MFT implementation, elapsed time fell **63.1%** and CPU time
fell **62.0%**. The 3.04 GB process-read count is the single sequential MFT read;
it did not grow across the CPU optimizations. Once this initial scan commits its
USN checkpoint, ordinary starts use journal deltas instead of repeating it.

## Implemented controls

- Keep MFT run arithmetic 64-bit until each bounded 4 MiB read.
- Parse mutable 4 MiB buffers in place and project each batch immediately rather
  than retaining millions of parsed records and building a second link list.
- Cache resolved directory paths and exclusion state, retry only links whose
  parent genuinely appears later, and normalize the requested root once.
- Lazily derive `FileRecord` search fields and avoid repeated metadata-path and
  full-path normalization allocations in the elevated transport process.
- If MFT parsing fails before its first record, fall back to `FSCTL_ENUM_USN_DATA`
  inside the elevated helper instead of restarting as a recursive directory walk.
- On existing databases, retain incremental FTS triggers and rebuild only an
  actually incomplete snapshot; fresh databases still use one deferred rebuild.
- Reconcile unchanged NTFS rows through the compact file-reference index in MFT
  order, using the path primary key only for records without an NTFS identity;
  SQLite no longer performs a full upsert plus a second random path lookup.
- Skip reconciliation lookups entirely while initially loading a known-empty
  database, and path-sort only the bounded new/changed remainder of rescans.
- Defer recovery of an interrupted FTS snapshot until startup reconciliation,
  avoiding a rebuild racing the scan followed by a second rebuild.
- Use a 64 MiB SQLite page cache, 64 MiB WAL auto-checkpoint interval, in-memory
  temporary storage, and 256 MiB read-only memory mapping.
- Put synchronous SQLite bulk work and the elevated scanner into Windows
  background mode, lowering CPU, disk-I/O, and memory priority.
- Avoid lower-casing and allocating ASCII paths when no transliteration alias is
  required.
- Route one- and two-character transliteration lookups through a sparse partial
  alias index instead of scanning every row and evaluating `lower(name)`.
- Emit per-batch `indexing.sqlite_batch` timings and counters for reconciled and
  full-upsert rows to `performance-metrics.jsonl` for live verification.
