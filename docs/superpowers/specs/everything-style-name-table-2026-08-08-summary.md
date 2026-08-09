# Writer summary: Everything-style index/search design

## What was produced

| File | Role |
|---|---|
| `C:\Users\paulx\AppData\Local\Temp\grok-paulx\grok-design-doc-75806cc7.md` | Full design (**v3**, post round-2 review) |
| `C:\Users\paulx\AppData\Local\Temp\grok-paulx\grok-design-summary-75806cc7.md` | This summary |
| `C:\Users\paulx\AppData\Local\Temp\grok-paulx\grok-design-review-75806cc7.md` | Review: round 1 (48) + round 2 (9) all **addressed** |

## Design stance

Everything-**inspired** in-memory name table (parent pointers, unmanaged arenas, specialized indexes, crash-safe `LOSN`), elevated MFT/USN + sticky UAC kept, residual SQLite for usage/meta. G1 is **beat SQLite p95 by ≤0.7×**, not native Everything C parity.

**G11 / KD25 (user requirement):** cutover and “architecture success” require **mandatory ablation A/B** proving a **large** win — geo-mean warm p50 **≥2×** overall and **≥3×** on FTS-heavy slice vs SQLite, plus semantic equivalence and component attribution table. Fail-closed if not met (no PR8a default).

## Round-2 critical protocol additions (v3)

1. **KD21 — Mutation/checkpoint split**  
   `ApplyUsnMutationsAsync` never writes `volume_checkpoints`. Dual-write **must not** use today’s combined SQLite apply. Checkpoint only via durability owner after verified snapshot (C22).

2. **§8.2.1 — DualWrite partial failure**  
   Order: SQLite mutations first → NameTable second. Idempotent ops. On NameTable fail after SQLite ok: **ENTER_DIVERGED**, freeze USN/watcher/batches, no checkpoint, MFT rebuild SLA 5 min. Post-state table for tests (C23).

3. **§5.2.1 / KD24 — Full MFT dual-write crash**  
   Withhold SQLite prune/generation publish until NameTable catch-up + verified LOSN. Pre-snapshot NameTable not Steady. Startup C21 generation mismatch → forced NameTable rebuild.

4. **KD23 — path_key_map**  
   Arena open-address `hash64 → RecordId` (~20 B/entry); collision verify by materialize; no full path strings in map. §9.1 total ~177 B/entry @ steady.

5. **KD22 — Journal wrap / durable lag**  
   Track memory vs durable USN; force snapshot every 2 min or 50k mutations; journal danger vs **durable** watermark → force snapshot or full MFT. G2 = in-memory only.

6. **§5.6.1 — Checkpoint ownership**  
   SqliteOnly: combined apply OK. DualWrite/NameTable: only façade durability Save; coordinator Save forbidden (C25).

7. **PR8a new-install safety**  
   Default DualWrite for new installs too; NameTable-only only after bake+PR8b or experimental env + “G5 does not apply” warning.

8. **Tombstone compact** — ratio > 0.15 forces compact; dual-write when both quiescent (C26).

9. **GC soak gate** — 4×50 MB/s×60 s; fail p95>2× baseline or gen2>50 ms; PR7 nightly.

## Key decision IDs (v3)

KD16 durability · KD17 ranking · KD18 dual-write order/Diverged · KD19 filter-at-ingest · KD20 honest latency · **KD21 mutation split** · **KD22 durable lag** · **KD23 path_key_map** · **KD24 full-build dual-write** · **KD25 ablation large-win gate**

## PR order (summary)

`PR1 (mutation-only USN + ownership) → PR2 (arena+path_key_map) → PR3 indexes → PR4 LOSN → PR5 façade+app → PR6 watermark+§5.2.1 → PR7 dual-write+ablation harness+C21–25+GC → BAKE+G11 → PR8a DualWrite default → PR8b/c → PR11`

## Review counts

| Round | Open → addressed |
|---|---|
| 1 | 48 → 48 |
| 2 | 9 → 9 |
| **Still open** | **0** |

## Note

Design-only; no repository code changes; no package build.
