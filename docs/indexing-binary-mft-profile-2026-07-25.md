# Binary transport + streaming MFT profile (2026-07-25)

## Question

Would replacing JSONL scan IPC with binary frames, and streaming $MFT parse (no full raw image), reduce indexing cost?

## Profiling method

```powershell
dotnet run -c Release --project tools/ListaryOpen.IndexingBenchmark -- `
  --records 20000 --repeats 3 --warmups 1 `
  --json docs/indexing-transport-binary-2026-07-25.json
```

Machine: Intel Family 6 Model 94, 8 logical CPUs, Windows 10.0.19045, .NET 8.0.29.

## Transport codec A/B (20,000 records, median of 3 runs)

| Variant | Median ms | Throughput | Alloc | Bytes/op |
| --- | ---: | ---: | ---: | ---: |
| `jsonl-serialize-parse` | **167.38** | 119,487/s | 39.55 MiB | 2,073 |
| `binary-encode-decode` | **32.15** | 622,177/s | 16.39 MiB | 859 |

**Result:** binary frames are about **5.2× faster** and use about **59% fewer allocated bytes/op** for encode+decode of the same records. This justifies switching the elevated **scan** path from JSONL to `.bin` frames.

Journal state/changes remain JSONL (lower volume, complex shapes).

## Streaming MFT

Implementation change (not micro-benchable without admin raw volume access in CI):

- Before: `ReadEntireMft` → hold full raw `$MFT` (up to 768 MiB) + parsed map.
- After: stream data runs in 4 MiB chunks, parse FILE records into a dictionary, **discard raw chunks**.
- Peak memory is dominated by the parsed map, not the raw image.

Existing unit tests for MFT record projection remain the correctness gate for path expansion / hard links.

## Product wiring

- Helper `scan-to-file` writes `.bin` via `ElevatedIndexerBinaryWriter`.
- Main process tails `.bin` with `TailBinaryOutputRecordsAsync`.
- Path validator allows `.bin` under the trusted temp directory.
