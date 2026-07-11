# Search Latency and Relevance Implementation Plan

> **For agentic workers:** Execute tasks in order with test-driven development. Do not proceed past a red test that fails for an unexpected reason.

**Goal:** Deliver 200 ms idle-triggered, non-blocking search whose primary order is always textual similarity on the observed 1.5 million-row index.

**Architecture:** Replace additive relevance with a lexicographic match key; publish immutable result snapshots only after an idle delay; add normalized B-tree lookup, WAL-backed read/write separation, and FTS5 trigram candidate retrieval with crash-safe readiness metadata. Preserve a bounded exact/prefix path while FTS builds.

**Tech Stack:** .NET 8, WPF, Microsoft.Data.Sqlite 10, SQLite FTS5, xUnit

---

### Task 1: Structured textual relevance

**Files:**
- Create: `src/ListaryOpen.Core/Search/TextMatchKey.cs`
- Modify: `src/ListaryOpen.Core/Search/FuzzyMatcher.cs`
- Modify: `src/ListaryOpen.Core/Search/ResultRanker.cs`
- Modify: `src/ListaryOpen.Core/Search/SearchResult.cs`
- Modify: `src/ListaryOpen.Core/Search/ParsedSearchQuery.cs`
- Modify: `tests/ListaryOpen.Core.Tests/Search/FuzzyMatcherTests.cs`
- Modify: `tests/ListaryOpen.Core.Tests/Search/ResultRankerTests.cs`
- Modify: `tests/ListaryOpen.Core.Tests/Search/SearchQueryTests.cs`

- [ ] Add failing tests for exact > prefix > substring > boundary/subsequence > pinyin > parent path.
- [ ] Add failing tests proving extreme usage, recency, and pinned state cannot cross the full textual key but can break exact textual ties.
- [ ] Add failing multi-term, phrase, filter neutrality, extension, Unicode/case, folder, parent-path-only, and deterministic tie tests.
- [ ] Run `dotnet test tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj --filter "FullyQualifiedName~Search"` and verify RED.
- [ ] Implement a lexicographic `TextMatchKey`, independent per-term matching, input-order preservation, and separate usage tie-breaks.
- [ ] Keep compatibility score APIs only where needed by other components; remove additive usage/pinned relevance.
- [ ] Run the focused Core search suite and verify GREEN.

### Task 2: Idle debounce and latest-result publication

**Files:**
- Modify: `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`
- Add or modify a range/snapshot collection under `src/ListaryOpen.App/ViewModels/`
- Modify: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs`

- [ ] Add failing tests for a 200 ms default idle delay and rapid typing producing exactly one search.
- [ ] Add a token-ignoring fake whose old request completes last; assert it cannot change results or status.
- [ ] Assert typing during the delay preserves the current result snapshot and does not emit collection changes.
- [ ] Assert cancellation occurs outside the cancellation ownership lock and final publication is one batched change on the captured UI context.
- [ ] Run the focused view-model tests and verify RED.
- [ ] Change the default delay to 200 ms, atomically assign generations, exchange cancellation ownership under lock, cancel outside the lock, and run retrieval/ranking through an injected worker executor.
- [ ] Publish one immutable snapshot after the generation check; retain previous results while waiting or searching.
- [ ] Run the focused tests and verify GREEN.

### Task 3: Normalized exact and prefix database retrieval

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Modify: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] Add failing migration tests for `name_key` backfill and idempotent reopening.
- [ ] Add failing query tests for exact and prefix recall, case/Unicode normalization, short queries, and filter behavior.
- [ ] Add an `EXPLAIN QUERY PLAN` assertion that exact and prefix retrieval seek the normalized B-tree index.
- [ ] Verify RED with focused SQLite tests.
- [ ] Add the normalized column/index and use equality plus binary range bounds rather than `LIKE` for prefix lookup.
- [ ] Remove leading-wildcard fallback from the short-query path; keep bounded usage candidates only as textual tie candidates.
- [ ] Verify focused tests GREEN and record query plans against `artifacts/ListaryOpen/data/index.db`.

### Task 4: WAL and separate reader/writer connections

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Modify: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] Add failing tests that verify WAL and finite busy timeout.
- [ ] Add concurrency tests: reader completes while a writer transaction exists, writer commits while a reader is active, cancellation while queued/in-flight, and both connections dispose safely.
- [ ] Verify RED.
- [ ] Configure WAL before opening a serialized read connection; keep the existing serialized writer and add a separate reader gate.
- [ ] Register required functions per connection, keep reads short, handle `SQLITE_BUSY`, and dispose connections in a safe order.
- [ ] Verify concurrency tests GREEN.

### Task 5: FTS5 trigram lifecycle and synchronization

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Create focused FTS helpers under `src/ListaryOpen.Infrastructure/Search/` if needed to keep lifecycle/query building isolated.
- Modify: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] Add a runtime FTS5 capability test and failing migration tests for `building`/`ready`, interrupted build recovery, and existing-row backfill.
- [ ] Add failing mutation parity tests for insert, indexed-text update, generation-only update, delete, rename, USN changes, stale prune, and content reset.
- [ ] Add safe MATCH escaping tests and three-character substring/path/pinyin retrieval tests.
- [ ] Verify RED.
- [ ] Implement the trigram table and explicit synchronization that avoids token rewrites when indexed text is unchanged.
- [ ] Build existing content outside the critical startup path and mark ready only after a complete build; retain bounded exact/prefix search when not ready or unsupported.
- [ ] Verify focused tests GREEN and measure build/database/WAL size on a disposable representative database.

### Task 6: Recall-complete candidate union

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Modify: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] Add exhaustive-reference parity tests for exact, prefix, substring, path, pinyin, multi-term, and filters.
- [ ] Add adversarial corpora larger than 5,000 rows with the strongest match inserted after weak matches.
- [ ] Verify RED against the existing capped candidate windows.
- [ ] Replace the repeated wildcard/UDF scans with a union of recall-defined B-tree and FTS sources; rank the bounded materialized set in Core.
- [ ] Exclude abbreviation/typo tiers that lack a recall-complete retrieval source; do not silently scan all rows.
- [ ] Verify parity tests GREEN and assert no ranking UDF is evaluated over an unbounded scan.

### Task 7: Background migration and application integration

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Modify relevant app/startup tests under `tests/ListaryOpen.Infrastructure.Tests/App/`

- [ ] Add failing tests that startup remains usable while FTS is building, a failed build retains exact/prefix search, and a completed build is atomically selected.
- [ ] Verify RED.
- [ ] Integrate background readiness/build reporting without blocking the dispatcher or corrupting the base index.
- [ ] Ensure shutdown cancels and observes migration/search tasks.
- [ ] Verify focused startup/shutdown tests GREEN.

### Task 8: Real-corpus verification and delivery

**Files:**
- Add a repeatable benchmark/diagnostic script under `tools/` if the repository has no suitable harness.
- Update `docs/manual-test-checklist.md` with search responsiveness and ranking checks.

- [ ] Run `dotnet test ListaryOpen.sln -c Release --no-restore --nologo --maxcpucount:1`.
- [ ] Run `git diff --check`.
- [ ] Publish to a fresh directory, then replace `artifacts/ListaryOpen` only after package verification.
- [ ] Build/search a disposable representative 1.5 million-row index and capture exact, prefix, substring, pinyin, path, and one/two-character warm/cold p50/p95/p99 latency.
- [ ] Run searches while 500-row indexing batches execute; record maximum search delay, cancellation latency, dispatcher heartbeat stall, WAL growth, FTS size, and indexing throughput.
- [ ] Use `EXPLAIN QUERY PLAN` to confirm exact/prefix index seeks and FTS virtual-table retrieval with no repeated wildcard full scans.
- [ ] Manually verify rapid typing triggers one request after 200 ms and the most similar item remains first despite extreme usage history.
- [ ] Review the final diff for unrelated changes and preserve the earlier uncommitted NTFS/package fixes.
