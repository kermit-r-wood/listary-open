# Search Maturity Design

## Goal

Move ListaryOpen search closer to an Everything-like local file search experience without attempting full Everything parity in one cycle.

The priority is index trustworthiness first, query responsiveness second, and richer syntax third. A search result that is briefly stale is acceptable. Silently deleting valid records because a scan or journal catch-up was partial is not acceptable.

## Requirements

- Keep the current product shape: tray-first, keyboard-first, WPF app, SQLite-backed local index.
- Preserve the existing elevated NTFS helper plus fallback provider architecture.
- Treat USN Journal support as a correctness system, not just a speed feature.
- Make every prune, checkpoint advance, and full-rescan decision explicit and testable.
- Stabilize the existing query syntax V1 before adding larger Everything-compatible syntax.
- Add local diagnostics before adding heavier search infrastructure.
- Avoid new dependencies for the stabilization work.

## Non-Goals

- Exact Everything search language compatibility.
- Content indexing, OCR, or archive indexing.
- Regex, wildcard grammar, full Boolean groups, macros, bookmarks, search history, size/date/attribute filters.
- ReFS fast indexing, `USN_RECORD_V3`, mounted-volume maturity, or volume GUID path parity.
- Persistent real-time watcher service.
- Network location live monitoring.
- HTTP/ETP/FTP servers, public SDK, or IPC.
- FTS5, n-gram tables, multiple SQLite read connections, or WAL changes before measurement proves they are needed.

## Review Inputs

This spec incorporates three independent review angles:

- NTFS correctness: checkpoint scope, full-scan prune boundaries, ambiguous USN events, and volume identity.
- Query behavior: existing parser semantics, filter-only searches, candidate-window limits, ranking, and UI mode contracts.
- Product sequencing: phase size, rollback, diagnostics, and explicit out-of-scope parity work.

The main conclusion is that P0 is correctly scoped, but P1 must be split. Checkpoint/planner work is not the same as robust real catch-up. Query syntax V1 already exists in code and should now be stabilized, documented, and measured.

## Current Code Baseline

ListaryOpen already has more than a basic recursive search:

- `NtfsUsnJournalReader` uses `FSCTL_ENUM_USN_DATA` for NTFS full scans and now reuses one queried journal state as the full-scan high watermark.
- `NtfsIndexProvider` rejects mapped network drives even when they report `NTFS`.
- `IndexingCoordinator` only prunes after apparently successful scans and attempts NTFS journal catch-up before falling back to a full scan.
- `SqliteSearchIndex` stores file records, usage records, generation metadata, and USN checkpoints.
- `SqliteSearchIndex.ApplyUsnJournalChangesAsync` applies journal changes and checkpoint advancement in one SQLite transaction.
- `ParsedSearchQuery` already supports bare terms, quoted phrases, `ext:`, `path:`, `!term`, `!ext:`, `file:`, and `folder:`.
- `SqliteSearchIndex.SearchAsync` releases the connection gate before final .NET ranking.
- Fallback indexing handles non-NTFS roots and unavailable NTFS paths.

The remaining gap is not "add USN" from zero. The gap is making the existing USN and query work robust enough that the index does not drift, the UI behavior is predictable, and future syntax/performance work has measurements.

## Current Risks

### Index Correctness

- Checkpoints are effectively per indexed root, but the type/table still call the key `volume_root`. The spec and code should make this explicit. A true per-volume checkpoint is unsafe unless one coordinator applies a journal range to every indexed root on that volume before advancing it.
- A full NTFS scan bounded at `HighUsn` can miss files changed after that watermark. Pruning immediately after the scan can delete records that should survive unless catch-up from `HighUsn` succeeds first.
- `checkpoint.NextUsn > current.NextUsn` should be treated as a corrupt or stale checkpoint and force full rescan/reset. It must not be treated as "no work."
- Busy volumes can advance `NextUsn` while catch-up is reading. Requiring the queried `NextUsn` to remain exactly equal to the requested end can cause repeated full rescans.
- Root path is not a stable volume identity. Drive-letter reassignment, mounted volumes, and volume GUID paths can reuse a checkpoint for the wrong filesystem if identity is only path text.
- Path-only delete/rename handling is acceptable for V1, but ambiguous USN sequences must preserve old records and schedule full rescan.

### Query Behavior And Performance

- Query syntax V1 exists, but unsupported Everything-style syntax does not have a documented contract.
- Most SQLite filters still use leading-wildcard `LIKE`, suffix `LIKE`, or `lower(name) LIKE`. Current B-tree indexes help little for those predicates.
- SQLite candidate queries still hold the single connection gate while they run.
- `listary_rank_score` still runs inside SQLite for fuzzy and combined-score candidate passes, then final ranking is recomputed in .NET.
- Cancellation is checked between candidate passes, but a long SQLite pass is not interruptible at fine granularity.
- Filter-only queries such as `folder:`, `ext:pdf`, and `!ext:tmp` need deterministic first-page behavior and should avoid expensive fuzzy/UDF passes.
- Dialog Jump and Quick Switch are folder-oriented UI modes. Their behavior for a user-entered `file:` token must be intentional and tested.

### Volumes And Permissions

- ReFS, FAT, exFAT, UNC, offline network roots, and permission-limited roots are fallback-only.
- Root-level fallback failure must not look like a successful empty scan.
- Child-level access failures can be skipped, but the status should say the run was permission-limited.
- UAC cancellation should be reported as canceled and must not trigger fallback prune.

## Design

### Phase P0: Stabilize The Existing Baseline

P0 is a hardening pass over behavior that already exists or is close.

#### Checkpoint Scope

Rename the conceptual checkpoint key from "volume checkpoint" to "root journal checkpoint" unless a real per-volume manager is introduced.

Required behavior:

- A checkpoint belongs to one indexed root path plus the journal identity it was observed on.
- Two indexed roots on the same NTFS volume must not cause either root to skip the other's journal range.
- If a future per-volume manager is added, it must apply one journal range to every indexed root on that volume before advancing the shared checkpoint.

The table name can remain for migration simplicity, but code, comments, and tests should describe the actual root-scoped semantics.

#### Checkpoint Safety Guards

Catch-up planning must force full rescan when:

- checkpoint is missing.
- `UsnJournalId` changed.
- rules/content version changed.
- checkpoint `NextUsn` is lower than `LowestValidUsn`.
- checkpoint `NextUsn` is greater than current `NextUsn`.
- checkpoint volume identity does not match the current root.

Checkpoint advancement remains transactional with applied changes. Failed catch-up preserves old records and falls back to full scan without pruning partial output.

#### Full-Scan Prune Boundary

A full NTFS scan records its starting journal state and `HighUsn`.

Before pruning stale records under the root, one of these must be true:

- catch-up from full-scan `HighUsn` to the current journal point has been applied successfully, or
- the system deliberately skips prune and reports that prune was skipped because the post-scan catch-up was incomplete.

This prevents files created, renamed, or modified after the scan watermark from being removed by generation prune.

#### Ambiguous USN Policy

The V1 journal applier should only apply changes it can resolve confidently:

- create or modify: upsert current path metadata when the path is resolvable and under the indexed root.
- delete: delete only when the path is known for that indexed root.
- file rename: delete old path and upsert new path only when both sides are unambiguous.
- directory rename/move, unresolved parent, hard-link ambiguity, coalesced rename, move in/out of root, transient create-delete, or metadata-read failure: preserve old records and schedule full rescan.

Ambiguous does not mean "empty success."

#### Busy-Volume Catch-Up

Reading a journal range should target the planned `[StartUsn, EndUsn)` range. If the live journal advances beyond `EndUsn` while reading, the read may still succeed for the planned range. A full rescan is required only when the range is unavailable, the journal identity changes, records are unsupported/malformed, or the read cannot prove it reached `EndUsn`.

#### Query Syntax V1 Contract

V1 syntax is:

- bare terms: existing name/path fuzzy behavior.
- quoted phrase: phrase filter in path/search text.
- `ext:pdf`: extension filter without leading dot.
- `file:`: files only.
- `folder:`: folders only.
- `path:src`: require path text.
- `!term` and `!ext:tmp`: exclusion filters.

Unsupported Everything-style syntax has defined behavior:

- `*.pdf`, `regex:foo`, `size:>1mb`, date filters, attribute filters, Boolean groups, and macros are treated as ordinary literal terms unless a later parser version explicitly claims them.
- They must not silently behave like partial Everything syntax.

Filter-only searches must:

- return a deterministic first page.
- respect `SearchQuery.Limit`.
- avoid fuzzy/UDF candidate passes when there is no useful positive ranking text.

#### UI Mode Contract

Dialog Jump and Quick Switch remain folder-targeting experiences.

In those modes:

- `folder:` is allowed and keeps folder-only results.
- `file:` must not surface file results in a folder-only jump flow.
- The UI should either ignore `file:` for these modes with a status message or return no results with a clear status. The chosen behavior must be covered by tests.

Normal search can honor `file:` as files only.

#### Diagnostics

Add lightweight local diagnostics before larger search architecture work:

- query elapsed p50/p95/max.
- candidate count per pass.
- final ranking time.
- SQLite connection-gate wait time.
- whether expensive fuzzy/UDF passes ran.
- index run result, indexed count, prune count, and prune-skipped count.
- catch-up action and reason.
- USN events read, applied, skipped, and full-rescan reason.
- database row count and file size.

Diagnostics can be trace/log output first. No UI is required for P0.

### Phase P1a: Root Journal Checkpoint Hardening

P1a finishes the USN catch-up foundation without promising Everything-grade real-time updates.

Required work:

- Persist root-scoped checkpoints with root path, filesystem kind, journal id, next USN, rules version, last successful full scan time, and enough volume identity to avoid drive-letter reuse.
- Add corrupt-checkpoint handling.
- Add tests for two indexed roots on the same NTFS volume.
- Add tests for checkpoint reset on journal id change, rules version change, low-watermark gap, future `NextUsn`, and volume identity mismatch.
- Ensure catch-up failure never advances the checkpoint and never prunes partial output.

Acceptance:

- A create/delete sequence under root A does not cause root B on the same volume to skip its own changes.
- Invalid checkpoint data causes a full rescan path and preserves existing records until a complete replacement is available.

### Phase P1b: Real Catch-Up Integration

P1b makes journal catch-up a dependable incremental update path.

Required work:

- Read planned USN ranges with `FSCTL_READ_USN_JOURNAL`.
- Apply simple file create, modify, delete, and unambiguous file rename.
- Detect directory-affecting or unresolved events and schedule root full rescan.
- Apply post-full-scan catch-up before prune, or skip prune if catch-up is incomplete.
- Advance checkpoints only after SQLite changes commit.
- Keep full rescan available as the conservative fallback for every uncertain case.

Acceptance:

- Local NTFS root create, modify, delete, and file rename update the index without a full root scan.
- Directory rename or move preserves old records, marks the root dirty, and completes a full rescan before prune.
- A file created after full-scan `HighUsn` but before prune is not removed from the index.
- Journal reset, unsupported record version, malformed helper output, and unreadable journal range all preserve existing records and request full rescan.

### Phase P1c: Query Syntax Stabilization

P1c locks down the current V1 parser and SQLite behavior.

Required work:

- Document syntax in user-facing docs.
- Add parser and SQLite tests for all V1 tokens.
- Add tests for unsupported syntax as literal text.
- Add tests for filter-only queries.
- Add candidate-window tests for path, phrase, and exclusion filters. Either prove a valid later result can still surface or explicitly accept the bounded-window limitation.
- Add ranking contract tests: exact name match beats path/pinyin without usage; usage and recency boosts can reorder results only within capped, stable limits.
- Decide and test `file:` behavior in Dialog Jump and Quick Switch.

Performance follow-up:

- If `ext:` or name filtering is slow in diagnostics, add narrow `extension` and `name_lower` columns.
- Do not add FTS/ngram/WAL before the diagnostics show the current candidate path is the bottleneck.

## Data Model

### Files

Keep the current `files` table for stabilization.

Optional future columns, only after measurement:

- `extension`
- `name_lower`
- `volume_identity`
- file reference number side table for rename/move identity

### Root Journal Checkpoints

The existing table can be migrated or reinterpreted, but the model should be:

```sql
create table if not exists root_journal_checkpoints(
    root_path text not null primary key,
    file_system_name text not null,
    volume_identity text,
    usn_journal_id text not null,
    next_usn text not null,
    rules_version integer not null,
    last_full_scan_at text not null
);
```

USN values are stored as invariant-culture decimal text so unsigned journal identifiers do not depend on SQLite signed integer range. Code parses them to native numeric types before comparison.

## Error Handling

- Unsupported USN record versions fail the NTFS provider for that run. They must not look like a successful empty scan.
- Malformed helper output marks NTFS failed and avoids prune.
- Fallback is safe only before any partial NTFS output has been accepted for that root.
- Root-level fallback access failures mark the run failed and preserve old records.
- Child-level inaccessible directories can be skipped, but the run status should mention permission-limited indexing.
- Network offline preserves old records and reports offline, not root missing.
- UAC cancellation reports canceled and does not trigger fallback or prune.
- Corrupt checkpoint data forces full rescan/reset and preserves old records until a complete scan succeeds.

## Verification

Automated tests:

- Two roots on the same NTFS volume maintain independent root-scoped checkpoints.
- `checkpoint.NextUsn > journal.NextUsn` forces full rescan/reset.
- Volume identity mismatch does not reuse an old checkpoint.
- Full-scan `HighUsn` to prune boundary is protected by catch-up or prune skip.
- Busy-volume catch-up can read a planned range even if the live `NextUsn` advances beyond the planned end.
- Ambiguous directory rename/move schedules full rescan and preserves old records.
- Incomplete scan does not prune.
- Fallback root failure does not prune.
- Network drive classification selects fallback even when format reports `NTFS`.
- Filter-only searches are deterministic and skip expensive fuzzy/UDF passes.
- Unsupported syntax is treated as literal text.
- Query syntax V1 filters return expected result sets.
- Dialog Jump and Quick Switch have explicit `file:` behavior tests.

Manual checks:

- Local NTFS root with create, modify, delete, file rename, and directory rename.
- Two indexed roots on the same NTFS volume.
- exFAT or FAT USB root.
- UNC share online and offline.
- Mapped network drive while running elevated and non-elevated.
- Root containing an inaccessible child directory.
- Large-index short queries and filter-only queries with diagnostics enabled.

## Rollout

1. Ship P0 stabilization and diagnostics.
2. Review diagnostics on a large local index before adding heavier search structures.
3. Ship P1a root checkpoint hardening.
4. Ship P1b real USN catch-up integration.
5. Ship P1c query syntax stabilization and docs.
6. Revisit P2 parity only after P0/P1 metrics show the next bottleneck.

## Rollback And Recovery

- Schema/rules version bumps may rebuild `files`, but should preserve `usage`.
- Missing, invalid, corrupt, or mismatched checkpoints force full rescan and preserve old records until a complete scan succeeds.
- Catch-up failures must not advance checkpoints.
- Partial full scans must not prune.
- If a new syntax parser version misbehaves, unsupported tokens should continue to behave as literals rather than returning broad accidental matches.

## P2 Parking Lot

P2 is explicitly measurement-driven:

- ReFS USN support and `USN_RECORD_V3`.
- File reference number side table for identity across rename/move.
- Multiple read connections plus WAL for foreground search isolation.
- FTS5 or n-gram/prefix table for scalable filtering.
- Full query grammar: OR, grouping, wildcards, regex, size/date/attribute filters, macros, bookmarks, search history.
- Per-volume manager with offline retention policy.
- HTTP/ETP/FTP server, SDK, and IPC.
