# Search Latency and Ranking Design

## Objective

Make search responsive on the observed 1.5 million-record index and guarantee that textual similarity is the primary ordering rule. Search starts only after 200 milliseconds without new input. Usage and recency may break true textual ties but may never promote a weaker textual match above a stronger one.

## Current problems

The current database is approximately 1.67 GB. Search performs several leading-wildcard `LIKE` scans and invokes managed ranking functions while SQLite orders matching rows. The existing `search_text` B-tree cannot serve these predicates. The current name index also does not reliably serve prefix `LIKE` because its collation does not match the query semantics. All reads and writes share one connection gate, and the database uses delete journaling.

The current additive score also mixes textual relevance with usage, recency, and pinned-folder boosts. A frequently used weak match can therefore outrank a stronger textual match. Contiguous occurrences receive nearly identical fuzzy scores, so exact, prefix, and interior substring matches are not represented as distinct relevance classes.

## Interaction and concurrency

Every query-text change restarts a 200-millisecond idle timer. No database request starts before the timer completes. New input cancels the timer or in-flight search and assigns a monotonically increasing generation. Only the latest generation may publish results, even if an older provider ignores cancellation and completes later.

Typing does not clear or rebuild the current result collection. Database retrieval and final ranking run on a worker search executor. The dispatcher receives one immutable result snapshot and applies it as one batched UI update. UI-bound properties and collections are changed only on the dispatcher.

Cancellation ownership is exchanged under a short lock, but cancellation callbacks execute after leaving the lock. Cancellation limits wasted work; bounded, indexable queries remain the primary responsiveness mechanism.

## Database architecture

The writable connection remains serialized. Initialization switches the database to WAL mode, configures a finite busy timeout, and deliberately selects the synchronous policy appropriate for a regenerable index. A separate serialized read connection opens only after schema initialization and migration completes. Read commands and transactions remain short so they do not pin WAL checkpoints.

The `files` table gains normalized search keys produced by the same C# normalization used for ranking. At minimum this includes `name_key`; other indexed keys are added only when required by an implemented query. Exact lookup uses equality, and prefix lookup uses an indexable range over the normalized key. Tests and `EXPLAIN QUERY PLAN` must demonstrate index seeks.

An FTS5 trigram index supplies candidates for contiguous name substrings, paths, and indexed pinyin text of three or more characters. User text is passed through a safe MATCH-query builder; search operators are parsed before FTS construction and are never concatenated directly into MATCH syntax.

One- and two-character queries do not run arbitrary full-database infix scans. They use exact name, name prefix, and a bounded set of high-value candidates. This limitation is explicit because trigram indexes cannot represent terms shorter than three characters.

FTS is candidate retrieval, not the source of final order. Retrieval uses a union of relevance-specific sources rather than a single arbitrary FTS window. Exact and prefix tiers are recall-complete through B-tree indexes. Contiguous substring tiers are supplied by trigram matches. Any abbreviation, subsequence, or typo tier included in the product must have a defined recall strategy and parity tests; it cannot rely on an unproven capped candidate set.

## FTS lifecycle and migration

FTS construction uses explicit `building` and `ready` metadata. Existing records are backfilled before the index becomes searchable. A crash or content/version mismatch causes a safe rebuild. The previous usable search path remains available during a background build, or the regenerable index is rebuilt into a versioned database and atomically switched when ready; startup must not synchronously rebuild 1.5 million rows.

All mutation paths remain consistent: single and batch upsert, delete, rename, USN journal changes, stale-generation pruning, and content-version reset. FTS updates occur only when indexed text changes. A generation-only upsert must not delete and reinsert FTS tokens. The implementation measures FTS size, WAL growth, full-build duration, and indexing throughput before choosing maintenance or optimize scheduling.

The shipped runtime must verify FTS5 support. Unsupported runtimes retain exact and prefix search and expose a diagnostic rather than silently reverting to repeated full scans.

## Text ranking model

Ranking uses a structured, lexicographically compared text-match key, not one additive double. The primary tiers are:

1. Exact full filename.
2. Filename prefix.
3. Contiguous filename substring.
4. Native filename word-boundary or abbreviation match.
5. Native filename fuzzy or bounded typo match.
6. Pinyin name match, with exact/prefix/substring/fuzzy subtiers defined below direct native text.
7. Parent-path component match.

All filename tiers outrank all path-only tiers. Case is Windows-insensitive and normalization is consistent across persistence, retrieval, and ranking. The filename field is not counted again through `FullPath`; path matching operates on the parent path.

Within a tier, the key compares complete positive-term coverage, worst per-term tier, the ordered per-term tier vector, boundary quality, compactness/gap cost, edit cost where applicable, and length difference. Usage count and recency are consulted only after the complete textual key is equal. Deterministic final tie-breakers are name using ordinal-ignore-case then ordinal comparison, followed by full path using the same pair.

Multiple bare terms are matched independently and all must be present. A quoted phrase requires normalized contiguous occurrence. Query operators are filters and do not add ranking text. `path:` terms match paths only; `ext:` remains filter-only. Phrases and terms retain their input order rather than being regrouped before ranking.

Match reason remains textual. Usage or pinned state is separate metadata and does not overwrite the reason shown for a result.

## Error handling and fallback

Schema or FTS build failures do not corrupt the base file index. The build is left non-ready and can be retried. Read contention is bounded by busy timeout and reported through diagnostics. A canceled or stale search does not publish an error state. A current-generation failure preserves the previous result snapshot and shows a concise status.

No fallback may silently execute the existing repeated full scans on the UI thread. When FTS is unavailable, exact and prefix search remain functional and bounded.

## Verification

Unit tests cover the 200-millisecond debounce with fake time, rapid typing producing one query, cancellation outside locks, a token-ignoring stale query completing last, dispatcher affinity, and one batched result publication.

Ranking tests prove exact above prefix, prefix above substring, substring above native fuzzy, native name above pinyin, and every filename match above path-only matches. Extreme usage and recency cannot cross textual keys. Equal textual keys may be reordered by usage. Tests also cover extensions, folders, case and Unicode normalization, Chinese and pinyin, multi-term order and coverage, phrases, filters, short queries, typos, shuffled input, and deterministic ties.

Database tests cover schema migration and interrupted FTS builds; all insert, update, delete, rename, prune, and generation-only paths; safe MATCH escaping; short-query routing; query-plan index use; separate-reader/writer progress under WAL; busy timeout; cancellation; and disposal.

Candidate retrieval is compared with exhaustive reference ranking on controlled corpora, including adversarial sets larger than every candidate window. A better match inserted after thousands of weak matches must still be returned. Any fuzzy tier without recall parity is excluded until it has an indexable retrieval strategy.

A dedicated benchmark uses a representative 1.5 million-row database and records warm and cold latency for exact, prefix, substring, pinyin, path, filter, common-term, and one- or two-character queries while 500-row indexing batches run. It records p50, p95, and p99 latency, cancellation latency, dispatcher heartbeat stalls, allocations, query plans, rows and UDF evaluations, database/FTS/WAL size, and indexing throughput. CI uses deterministic functional tests and only a broad smoke ceiling; detailed performance thresholds run in a controlled benchmark job.

## Delivery sequence

Implementation proceeds in independently verifiable stages: structured ranking semantics; UI debounce and stale-result safety; normalized B-tree exact/prefix retrieval; WAL and separate read connection; FTS lifecycle and substring retrieval; remaining indexed fuzzy/pinyin retrieval; background migration; and real-corpus performance verification. Each stage preserves a working bounded search path.
