# Search Maturity Design

## Goal

Improve ListaryOpen search maturity toward an Everything-like experience without trying to clone Everything in one pass.

The first implementation should make the index harder to corrupt or drift, keep search responsive under large indexes, and expose a small set of high-value query filters. Full Everything parity is a later goal.

## Confirmed Requirements

- Keep the current product shape: tray-first, keyboard-first, WPF app, SQLite-backed local index.
- Preserve the existing NTFS elevated helper and fallback provider architecture.
- Prioritize index correctness over instant update latency. A stale result is better than deleting valid records because a scan was partial.
- Use the smallest useful query syntax first: extension, file/folder mode, path text, quoted phrase, and exclusion.
- Avoid new dependencies for the P0 work.
- Do not implement ReFS parity, full Boolean grammar, regex, HTTP/ETP/FTP servers, SDK, or IPC as part of this spec.

## Current State

ListaryOpen already has useful search foundations:

- `NtfsUsnJournalReader` opens NTFS volumes and enumerates records with `FSCTL_ENUM_USN_DATA`.
- `NtfsUsnRecordProjector` resolves records into paths with default exclusions.
- `ElevatedIndexerClient` launches the helper and imports JSONL output incrementally.
- `IndexingCoordinator` batches records into `SqliteSearchIndex`.
- `SqliteSearchIndex` supports name/path matching, fuzzy matching, usage ranking, pinyin search text, and generation-based prune.
- `FallbackIndexProvider` recursively scans non-NTFS and unavailable NTFS paths.

The important gap is that this is still "fast full scan plus SQLite rebuild", not an Everything-style persistent USN Journal index.

## Current Problems

### Index Correctness

- NTFS full scan uses two `FSCTL_ENUM_USN_DATA` passes that each query journal state independently. If the filesystem changes between passes, the directory map and record stream may not share one snapshot boundary.
- Records whose paths cannot be resolved are skipped. If the scan otherwise completes, generation prune may delete old records that still exist.
- There is no persisted per-volume checkpoint for `UsnJournalId`, `LowestValidUsn`, or `NextUsn`.
- There is no `FSCTL_READ_USN_JOURNAL` catch-up path for create/delete/rename/modify events.
- Rename and move are treated as delete plus insert after the next full scan because identity is currently path-based.

### Query Performance And Syntax

- `SearchQuery` only models raw text, mode, and limit.
- SQLite candidate collection runs several overlapping passes: exact, fuzzy, usage, combined, and fallback.
- The main matching conditions are leading-wildcard `LIKE` patterns and ordered fuzzy `LIKE` patterns, so normal SQLite indexes provide little help.
- `listary_rank_score` runs as a SQLite UDF during ordering and can become expensive for short queries.
- `SearchAsync` holds the single connection gate while reading candidates, reading usage, and ranking.
- There is no user-visible syntax for extension, path, negation, quoted phrase, size/date, attributes, regex, or macros.

### Volumes And Permissions

- Local NTFS drive-letter paths prefer `NtfsIndexProvider` when the helper is available.
- ReFS, FAT, exFAT, UNC, and ordinary folders fall back to recursive enumeration.
- Mapped network drives may be misclassified if only drive format is considered.
- Fallback scanning swallows many file system errors. If a root-level permission or offline failure produces partial output, prune can remove good old records.
- Extended paths, mounted volumes, and volume GUID paths are not first-class.

## Design

### Phase P0: Prevent Drift And Keep Search Responsive

P0 is the smallest useful hardening pass.

#### Fixed NTFS Scan Watermark

`NtfsUsnJournalReader` should query the journal once at the start of a full scan and reuse the same `NextUsn` as `HighUsn` for both enumeration passes.

This does not create a true snapshot, but it removes the current avoidable mismatch where the directory pass and record pass can use different upper bounds.

#### Safe Prune Contract

Indexing should only prune stale records when the provider reports a complete scan.

The minimal implementation can keep the current `IAsyncEnumerable<FileRecord>` shape and treat unexpected provider exceptions as incomplete. For fallback root-level enumeration failures, return failure to `IndexingCoordinator` instead of silently yielding zero or partial records.

Provider behavior:

- Complete scan: upsert records, then prune records under the root for the current generation.
- Incomplete scan: upsert any records already flushed if they are valid, but do not prune the root.
- UAC canceled: do not fallback to a slow full scan and do not prune.
- Root offline or inaccessible: mark status failed and keep old records.

#### Network Drive Classification

Volume resolution should classify `DriveType.Network` as network before considering the file system name. A mapped `Z:\` drive that reports `NTFS` should use fallback folder indexing, not the NTFS volume helper.

UNC paths should continue to use fallback.

#### Query Gate And Short Query Guard

`SqliteSearchIndex.SearchAsync` should hold `_connectionGate` only while accessing SQLite. Candidate records and usage records can be copied out, then `ResultRanker.Rank` can run after releasing the gate.

Search should check cancellation between candidate passes.

For one-character queries, skip expensive fuzzy UDF candidate passes and rely on exact/prefix/usage candidates. This preserves responsiveness for ordinary typing and avoids scanning the whole index on the first keystroke.

### Phase P1: Minimal USN Catch-Up And Useful Query Syntax

P1 adds real incremental correctness, but keeps the rename model simple.

#### Volume Checkpoint

Persist a per-volume checkpoint:

- volume root
- file system kind
- `UsnJournalId`
- last applied `NextUsn`
- index rules/content version
- last successful full scan time

The simplest storage is a new SQLite table. `index_metadata` is too flat once there are multiple volumes and roots.

Checkpoint advancement rule:

- Start from the previous checkpoint.
- Read changes and apply them in a transaction.
- Advance checkpoint only after all upserts/deletes and any required prune/dirty marking commit.

#### USN Catch-Up

On startup or after a full scan, query the current journal.

- If `UsnJournalId` changed, do a full rescan.
- If checkpoint `NextUsn` is lower than `LowestValidUsn`, do a full rescan.
- Otherwise, read from checkpoint `NextUsn` to current `NextUsn` with `FSCTL_READ_USN_JOURNAL`.

Apply simple deltas:

- create or modify: upsert the current path metadata if resolvable and under an indexed root.
- delete: delete the path if the path is known.
- file rename: delete old path and upsert new path.
- directory rename or move: mark the affected indexed root dirty and schedule a full rescan.

Directory subtree diffs are deliberately deferred.

#### Query Syntax V1

Add a small parsed query model:

- bare terms: existing fuzzy/name/path behavior.
- quoted phrase: exact phrase in name/path search text.
- `ext:pdf`: extension filter without leading dot.
- `file:` and `folder:`: type filters.
- `path:src`: require path text.
- `!term` or `!ext:tmp`: exclusion.

Parsing should be simple and deterministic. No Boolean groups, OR, macros, regex, or date/size filters in this phase.

SQLite can apply cheap filters first, then feed candidates to the existing ranker.

### Phase P2: Larger Parity Work

P2 should wait for measurements and user pressure.

- ReFS USN support and `USN_RECORD_V3`.
- File reference number side table for identity across rename/move.
- Multiple read connections plus WAL for better foreground search isolation.
- FTS5 or a small n-gram/prefix table for real prefiltering.
- Full query grammar: OR, grouping, wildcards, regex, size/date/attribute filters, macros, bookmarks, search history.
- Per-volume manager with offline retention policy.
- HTTP/ETP/FTP server, SDK, and IPC.

## Data Model

### Files

Keep the current `files` table for P0.

P1 may add cheap filter columns if needed:

- `extension`
- `name_lower`

Do not add these until query syntax work needs them. SQLite can initially derive extension from `name` or use a narrow migration if performance requires it.

### Volume Checkpoints

P1 adds:

```sql
create table if not exists volume_checkpoints(
    volume_root text not null primary key,
    file_system_name text not null,
    usn_journal_id text,
    next_usn text,
    rules_version integer not null,
    last_full_scan_at text
);
```

USN values are stored as invariant-culture decimal text so unsigned journal identifiers do not depend on SQLite signed integer range. Code parses them to the native numeric type before comparison.

## Error Handling

- Unsupported USN record versions should fail the NTFS provider for that run. They must not look like a successful empty scan.
- Malformed helper output should mark NTFS failed and avoid prune. Fallback is only safe before any partial NTFS output has been accepted for that root.
- Root-level fallback access failures should mark the run failed and preserve old records.
- Child-level inaccessible directories can still be skipped, but the status should mention permission-limited indexing.
- Network offline should preserve old records and report offline, not root missing.
- UAC cancellation should be reported as canceled and should not trigger fallback or prune.

## Verification

Automated tests:

- NTFS fixed watermark: fake journal returns different current `NextUsn` values; both enum passes use the same initial `HighUsn`.
- Incomplete scan does not prune: provider yields one record then fails; old records under the root remain.
- Fallback root failure does not prune: root-level unauthorized/offline failure preserves old records and reports failed status.
- Network drive classification: `DriveType.Network` plus `DriveFormat = NTFS` selects fallback.
- Short query guard: one-character query does not call the expensive fuzzy UDF path.
- Query syntax V1: `ext:pdf invoice !archive`, `folder: report`, `path:src "search panel"` each returns the expected SQLite result set.
- USN checkpoint: normal catch-up advances checkpoint only after commit; journal reset or `LowestValidUsn` gap schedules full rescan.
- Rename handling: file rename removes old path and adds new path; directory rename marks root dirty.

Manual checks:

- Local NTFS root with creates, deletes, file rename, and directory rename.
- exFAT or FAT USB root.
- UNC share online and offline.
- Mapped network drive while running elevated and non-elevated.
- Root containing an inaccessible child directory.

## Out Of Scope

- Exact Everything search language compatibility.
- ReFS fast indexing.
- Persistent real-time watcher service.
- Network location live monitoring.
- Public SDK or remote search server.
- UI for editing advanced syntax, exclusions, bookmarks, or macros.

## Rollout

1. Ship P0 as a correctness and responsiveness hardening pass.
2. Measure large-index search latency and indexing status behavior.
3. Ship P1 USN catch-up and Query Syntax V1 behind the existing indexing/search paths.
4. Revisit P2 only after P0/P1 show real bottlenecks.
