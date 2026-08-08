using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Search.NameTable;

/// <summary>
/// Compact Everything-style name table.  Records and indexes are primitive arrays;
/// full paths are reconstructed from parent ids only for the bounded candidate set.
/// </summary>
public sealed class NameTableEngine
{
    private const int InitialEntryCapacity = 4_096;
    private const int MaximumDeltaEntries = 50_000;
    private const long PlaceholderSize = long.MinValue;
    private const ulong FnvOffset = 14_695_981_039_346_656_037UL;
    private const ulong FnvPrime = 1_099_511_628_211UL;

    private readonly object _gate = new();
    private CompactFileEntry[] _entries = new CompactFileEntry[InitialEntryCapacity];
    private int _entryCount;
    private int _liveCount;
    private int _tombstoneCount;
    private CompactNameStore _names = new();

    // During the initial build these compact open-address maps avoid managed
    // string keys.  They are discarded after sorted immutable orders are built.
    private CompactHashLookup? _buildPathLookup = new();
    private CompactHashLookup? _buildNameLookup = new();

    private int[] _nameOrder = Array.Empty<int>();
    private int[] _shortSearchNameIds = Array.Empty<int>();
    private int[] _rootIds = Array.Empty<int>();
    private int[] _frnOrder = Array.Empty<int>();
    private int[] _nameRecordOffsets = Array.Empty<int>();
    private int[] _nameRecordIds = Array.Empty<int>();
    private int[] _nameRecordIdsById = Array.Empty<int>();
    private int[] _childOffsets = Array.Empty<int>();
    private int[] _childIds = Array.Empty<int>();
    private CompactTrigramIndex _trigramIndex = CompactTrigramIndex.Empty;
    private CompactTrigramIndex _shortAliasIndex = CompactTrigramIndex.Empty;
    private bool _ordersPublished;

    // Mutations after publication stay bounded.  The next Optimize merges them
    // into the sorted base and releases these small temporary indexes.
    private CompactHashLookup? _deltaPathLookup;
    private CompactHashLookup? _deltaNameLookup;
    private readonly List<int> _deltaRecordIds = new();
    private readonly List<int> _deltaNameIds = new();
    private readonly HashSet<int> _deltaRecordSet = new();
    private readonly HashSet<int> _deltaNameSet = new();

    private readonly Dictionary<string, NameTableCheckpointState> _memoryCheckpoints =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NameTableCheckpointState> _durableCheckpoints =
        new(StringComparer.OrdinalIgnoreCase);
    private long _generation;
    private long _durableGeneration;

    private long _durableWatermarkUsn;
    private string? _durableWatermarkVolume;
    private ulong _durableWatermarkJournalId;
    private long _durableWatermarkRulesVersion;
    private bool _diverged;
    private int _failNextApplyUsnMutations;

    public int LiveCount
    {
        get
        {
            lock (_gate)
            {
                return _liveCount;
            }
        }
    }

    public int TombstoneCount
    {
        get
        {
            lock (_gate)
            {
                return _tombstoneCount;
            }
        }
    }

    public bool IsDiverged
    {
        get
        {
            lock (_gate)
            {
                return _diverged;
            }
        }
    }

    public long DurableWatermarkUsn
    {
        get
        {
            lock (_gate)
            {
                return _durableWatermarkUsn;
            }
        }
    }

    public string? DurableWatermarkVolume
    {
        get
        {
            lock (_gate)
            {
                return _durableWatermarkVolume;
            }
        }
    }

    public ulong DurableWatermarkJournalId
    {
        get
        {
            lock (_gate)
            {
                return _durableWatermarkJournalId;
            }
        }
    }

    public long DurableWatermarkRulesVersion
    {
        get
        {
            lock (_gate)
            {
                return _durableWatermarkRulesVersion;
            }
        }
    }

    public bool HasPendingDurability
    {
        get
        {
            lock (_gate)
            {
                return _generation > _durableGeneration;
            }
        }
    }

    public double TombstoneRatio
    {
        get
        {
            lock (_gate)
            {
                var total = _liveCount + _tombstoneCount;
                return total == 0 ? 0 : (double)_tombstoneCount / total;
            }
        }
    }

    public void MarkDiverged()
    {
        lock (_gate)
        {
            _diverged = true;
        }
    }

    public void ClearDiverged()
    {
        lock (_gate)
        {
            _diverged = false;
        }
    }

    /// <summary>Test hook for the dual-write failure path.</summary>
    public void FailNextApplyUsnMutationsForTests(int count = 1)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (_gate)
        {
            _failNextApplyUsnMutations = count;
        }
    }

    public void SetDurableWatermark(
        string volumeRoot,
        long nextUsn,
        ulong usnJournalId = 0,
        long rulesVersion = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        lock (_gate)
        {
            var state = new NameTableCheckpointState(
                NormalizePath(volumeRoot),
                "NTFS",
                usnJournalId,
                nextUsn,
                rulesVersion,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _memoryCheckpoints[CheckpointKey(state.VolumeRoot)] = state;
            _durableCheckpoints[CheckpointKey(state.VolumeRoot)] = state;
            _durableWatermarkVolume = volumeRoot;
            _durableWatermarkUsn = nextUsn;
            _durableWatermarkJournalId = usnJournalId;
            _durableWatermarkRulesVersion = rulesVersion;
            _generation++;
        }
    }

    public void SetMemoryCheckpoint(UsnJournalCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (_gate)
        {
            SetMemoryCheckpointUnlocked(checkpoint);
        }
    }

    public bool TryGetDurableCheckpoint(string volumeRoot, out UsnJournalCheckpoint? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);
        lock (_gate)
        {
            if (_durableCheckpoints.TryGetValue(CheckpointKey(volumeRoot), out var state))
            {
                checkpoint = ToCheckpoint(state);
                return true;
            }

            checkpoint = null;
            return false;
        }
    }

    public int Upsert(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            return UpsertUnlocked(record);
        }
    }

    public bool TryGetByPathKey(string pathKey, out FileRecord? record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathKey);
        lock (_gate)
        {
            if (TryFindPathUnlocked(pathKey, out var id) && _entries[id].IsLive)
            {
                record = MaterializeRecordUnlocked(id);
                return true;
            }

            record = null;
            return false;
        }
    }

    public void DeleteByPath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        lock (_gate)
        {
            DeleteByPathUnlocked(fullPath);
        }
    }

    public void DeletePathAndDescendants(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        lock (_gate)
        {
            if (!TryFindPathUnlocked(fullPath, out var ancestorId))
            {
                return;
            }

            for (var id = 0; id < _entryCount; id++)
            {
                if (_entries[id].IsLive && IsSelfOrDescendantUnlocked(id, ancestorId))
                {
                    MarkDeletedUnlocked(id);
                }
            }
        }
    }

    public void HardLinkResync(ulong fileReferenceNumber, IReadOnlyList<FileRecord> liveRecords)
    {
        if (fileReferenceNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileReferenceNumber));
        }

        ArgumentNullException.ThrowIfNull(liveRecords);
        lock (_gate)
        {
            HardLinkResyncUnlocked(fileReferenceNumber, liveRecords);
        }
    }

    public void ApplyUsnMutations(IEnumerable<UsnJournalIndexChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        lock (_gate)
        {
            if (_failNextApplyUsnMutations > 0)
            {
                _failNextApplyUsnMutations--;
                throw new IOException("Injected NameTable USN mutation failure for tests.");
            }

            if (_diverged)
            {
                throw new InvalidOperationException("NameTable is diverged; USN mutations are frozen.");
            }

            foreach (var change in changes)
            {
                switch (change.Kind)
                {
                    case UsnJournalIndexChangeKind.Upsert:
                        UpsertUnlocked(change.Record!);
                        break;
                    case UsnJournalIndexChangeKind.Delete:
                        DeleteByPathUnlocked(change.FullPath!);
                        break;
                    case UsnJournalIndexChangeKind.HardLinkResync:
                        HardLinkResyncUnlocked(
                            change.FileReferenceNumber,
                            change.LiveHardLinkRecords ?? Array.Empty<FileRecord>());
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported USN index change kind: {change.Kind}.");
                }
            }
        }
    }

    /// <summary>
    /// Publishes compact sorted orders and releases the two large build maps.
    /// Call this at a full-build boundary; CollectCandidates also calls it lazily.
    /// </summary>
    public void Optimize()
    {
        lock (_gate)
        {
            OptimizeUnlocked();
        }
    }

    internal void ReplaceRootFrom(string rootPath, NameTableEngine source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(this, source))
        {
            throw new ArgumentException("A NameTable engine cannot replace a root from itself.", nameof(source));
        }

        var normalizedRoot = NormalizePath(rootPath);
        lock (source._gate)
        {
            lock (_gate)
            {
                if (TryFindPathUnlocked(normalizedRoot, out var ancestorId))
                {
                    for (var id = 0; id < _entryCount; id++)
                    {
                        if (_entries[id].IsLive && IsSelfOrDescendantUnlocked(id, ancestorId))
                        {
                            MarkDeletedUnlocked(id);
                        }
                    }
                }

                for (var id = 0; id < source._entryCount; id++)
                {
                    if (!source._entries[id].IsLive)
                    {
                        continue;
                    }

                    var record = source.MaterializeRecordUnlocked(id);
                    if (!IsSameOrDescendant(record.FullPath, normalizedRoot))
                    {
                        throw new InvalidDataException(
                            $"Staged NameTable record '{record.FullPath}' escapes root '{normalizedRoot}'.");
                    }

                    UpsertUnlocked(record);
                }

                OptimizeUnlocked();
            }
        }
    }

    internal void MergeRootFrom(string rootPath, NameTableEngine source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(this, source))
        {
            throw new ArgumentException("A NameTable engine cannot merge a root from itself.", nameof(source));
        }

        var normalizedRoot = NormalizePath(rootPath);
        lock (source._gate)
        {
            lock (_gate)
            {
                for (var id = 0; id < source._entryCount; id++)
                {
                    if (!source._entries[id].IsLive)
                    {
                        continue;
                    }

                    var record = source.MaterializeRecordUnlocked(id);
                    if (!IsSameOrDescendant(record.FullPath, normalizedRoot))
                    {
                        throw new InvalidDataException(
                            $"Staged NameTable record '{record.FullPath}' escapes root '{normalizedRoot}'.");
                    }

                    UpsertUnlocked(record);
                }

                OptimizeUnlocked();
            }
        }
    }

    public NameTableMemoryStats GetMemoryStats()
    {
        lock (_gate)
        {
            var entryBytes = (long)_entries.LongLength * Marshal.SizeOf<CompactFileEntry>();
            var sortedBytes = ((long)_nameOrder.LongLength
                + _shortSearchNameIds.LongLength
                + _rootIds.LongLength
                + _frnOrder.LongLength
                + _nameRecordOffsets.LongLength
                + _nameRecordIds.LongLength
                + _nameRecordIdsById.LongLength
                + _childOffsets.LongLength
                + _childIds.LongLength) * sizeof(int);
            sortedBytes += _trigramIndex.CapacityBytes + _shortAliasIndex.CapacityBytes;
            var buildBytes = (_buildPathLookup?.CapacityBytes ?? 0)
                + (_buildNameLookup?.CapacityBytes ?? 0)
                + (_deltaPathLookup?.CapacityBytes ?? 0)
                + (_deltaNameLookup?.CapacityBytes ?? 0)
                + ((long)_deltaRecordIds.Capacity + _deltaNameIds.Capacity) * sizeof(int);
            var retained = entryBytes + _names.CapacityBytes + sortedBytes + buildBytes;
            return new NameTableMemoryStats(
                _liveCount,
                _entries.Length,
                _names.Count,
                _names.OffsetCapacity,
                _names.CapacityBytes,
                sortedBytes,
                buildBytes,
                retained);
        }
    }

    internal NameTableCompactSnapshot CaptureCompactSnapshot(UsnJournalCheckpoint? checkpoint = null)
    {
        lock (_gate)
        {
            if (checkpoint is not null)
            {
                SetMemoryCheckpointUnlocked(checkpoint);
            }
            OptimizeUnlocked();

            var entries = new CompactFileEntry[_entryCount];
            _entries.AsSpan(0, _entryCount).CopyTo(entries);
            var checkpoints = _memoryCheckpoints.Values
                .OrderBy(item => item.VolumeRoot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.VolumeRoot, StringComparer.Ordinal)
                .ToArray();
            return new NameTableCompactSnapshot(
                _generation,
                _entryCount,
                _liveCount,
                _tombstoneCount,
                entries,
                _names.CaptureCopy(),
                (int[])_nameOrder.Clone(),
                (int[])_shortSearchNameIds.Clone(),
                (int[])_rootIds.Clone(),
                (int[])_frnOrder.Clone(),
                (int[])_nameRecordOffsets.Clone(),
                (int[])_nameRecordIds.Clone(),
                (int[])_nameRecordIdsById.Clone(),
                (int[])_childOffsets.Clone(),
                (int[])_childIds.Clone(),
                _trigramIndex.CaptureCopy(),
                _shortAliasIndex.CaptureCopy(),
                checkpoints);
        }
    }

    internal void MarkSnapshotDurable(NameTableCompactSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (snapshot.Generation < _durableGeneration)
            {
                return;
            }

            _durableCheckpoints.Clear();
            foreach (var checkpoint in snapshot.Checkpoints)
            {
                _durableCheckpoints[CheckpointKey(checkpoint.VolumeRoot)] = checkpoint;
            }

            _durableGeneration = snapshot.Generation;
            SetCompatibilityWatermarkUnlocked(snapshot.Checkpoints.LastOrDefault());
        }
    }

    internal void RestoreCompactSnapshot(NameTableCompactSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateCompactSnapshot(snapshot);
        var names = CompactNameStore.Restore(
            snapshot.Names.Heap,
            snapshot.Names.HeapLength,
            snapshot.Names.Offsets,
            snapshot.Names.Count);
        var trigrams = CompactTrigramIndex.Restore(snapshot.Trigrams);
        var shortAliases = CompactTrigramIndex.Restore(snapshot.ShortAliases);

        lock (_gate)
        {
            _entries = snapshot.Entries;
            _entryCount = snapshot.EntryCount;
            _liveCount = snapshot.LiveCount;
            _tombstoneCount = snapshot.TombstoneCount;
            _names = names;
            _nameOrder = snapshot.NameOrder;
            _shortSearchNameIds = snapshot.ShortSearchNameIds;
            _rootIds = snapshot.RootIds;
            _frnOrder = snapshot.FrnOrder;
            _nameRecordOffsets = snapshot.NameRecordOffsets;
            _nameRecordIds = snapshot.NameRecordIds;
            _nameRecordIdsById = snapshot.NameRecordIdsById;
            _childOffsets = snapshot.ChildOffsets;
            _childIds = snapshot.ChildIds;
            _trigramIndex = trigrams;
            _shortAliasIndex = shortAliases;
            _ordersPublished = true;
            _buildPathLookup = null;
            _buildNameLookup = null;
            ClearDeltaUnlocked();
            _generation = snapshot.Generation;
            _durableGeneration = snapshot.Generation;
            _memoryCheckpoints.Clear();
            _durableCheckpoints.Clear();
            foreach (var checkpoint in snapshot.Checkpoints)
            {
                var key = CheckpointKey(checkpoint.VolumeRoot);
                _memoryCheckpoints[key] = checkpoint;
                _durableCheckpoints[key] = checkpoint;
            }

            SetCompatibilityWatermarkUnlocked(snapshot.Checkpoints.LastOrDefault());
        }
    }

    public IReadOnlyList<FileRecord> CollectCandidates(SearchQuery query, int limit)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (_gate)
        {
            if (_liveCount == 0)
            {
                return Array.Empty<FileRecord>();
            }

            if (!_ordersPublished || _deltaRecordIds.Count > MaximumDeltaEntries)
            {
                OptimizeUnlocked();
            }

            var context = new CandidateContext(query);
            var candidateBudget = context.IsShortQuery
                ? Math.Min(10_000, checked(limit * 2))
                : limit;
            var results = new List<FileRecord>(Math.Min(candidateBudget, 512));
            var resultIds = new HashSet<int>();

            // Preserve a lane for direct children of the preferred root before a
            // high-frequency global name range can consume the candidate cap.
            if (!string.IsNullOrWhiteSpace(query.PreferredRoot)
                && TryFindPathUnlocked(query.PreferredRoot, out var preferredParentId))
            {
                if ((uint)(preferredParentId + 1) < (uint)_childOffsets.Length)
                {
                    for (var i = _childOffsets[preferredParentId];
                         i < _childOffsets[preferredParentId + 1] && results.Count < candidateBudget;
                         i++)
                    {
                        var id = _childIds[i];
                        if (_entries[id].IsLive && _entries[id].ParentId == preferredParentId)
                        {
                            TryAddCandidateUnlocked(id, context, results, resultIds, requirePositiveMatch: true);
                        }
                    }
                }

                foreach (var id in _deltaRecordIds)
                {
                    if (results.Count >= candidateBudget)
                    {
                        break;
                    }

                    if (_entries[id].IsLive && _entries[id].ParentId == preferredParentId)
                    {
                        TryAddCandidateUnlocked(id, context, results, resultIds, requirePositiveMatch: true);
                    }
                }
            }

            if (context.PositiveTerms.Length == 0)
            {
                for (var id = 0; id < _entryCount && results.Count < limit; id++)
                {
                    if (context.PathTermBytes.Length == 0
                        || CompactRecordCouldMatchUnlocked(id, context))
                    {
                        TryAddCandidateUnlocked(id, context, results, resultIds, requirePositiveMatch: false);
                    }
                }

                return results;
            }

            var visitedNames = new HashSet<int>();
            var matchedNames = new HashSet<int>();
            var usedNameIndex = _trigramIndex.TryGetRarestCandidatePositions(
                context.PositiveTerms,
                out var candidateNamePositions);

            if (context.IsShortQuery)
            {
                AddShortAliasCandidatesUnlocked(
                    context,
                    visitedNames,
                    matchedNames,
                    results,
                    resultIds,
                    Math.Min(candidateBudget, results.Count + limit));
            }

            // Exact/prefix lanes are deterministic ranges over the deduplicated
            // folded-name table.  Each required term gets a chance to seed the
            // union; all terms are verified before a record is accepted.
            foreach (var prefix in context.PrefixSeeds)
            {
                AddPrefixNameCandidatesUnlocked(
                    prefix,
                    context,
                    visitedNames,
                    matchedNames,
                    results,
                    resultIds,
                    candidateBudget,
                    context.PositiveTerms.Length > 1 && usedNameIndex
                        ? candidateNamePositions
                        : null);
                if (results.Count >= candidateBudget)
                {
                    return results;
                }
            }

            if (context.IsSingleCharacter)
            {
                if (results.Count < candidateBudget && matchedNames.Count > 0)
                {
                    AddDescendantCandidatesUnlocked(
                        matchedNames,
                        context,
                        results,
                        resultIds,
                        candidateBudget);
                }

                if (results.Count < query.Limit)
                {
                    AddDeterministicFallbackUnlocked(
                        context,
                        results,
                        resultIds,
                        Math.Min(candidateBudget, results.Count + 200));
                }

                return results;
            }

            // Match SQLite's bounded fuzzy-expansion rule: when a preferred-root
            // or exact/prefix lane already produced useful hits, add at most the
            // 200-row minimum fuzzy lane instead of materializing the full 1,000+
            // candidate budget.  With no earlier hit, retain the full budget so
            // substring-only queries keep their recall.
            var fuzzyLaneLimit = Math.Min(candidateBudget, Math.Max(200, checked(query.Limit * 2)));
            var fuzzyCandidateBudget = results.Count == 0
                ? candidateBudget
                : Math.Min(
                    candidateBudget,
                    checked(results.Count + fuzzyLaneLimit));

            // For 3+ byte queries, compressed trigram postings turn substring
            // misses and rare hits into an indexed lookup.  One/two-byte queries
            // retain the bounded unique-name scan below.
            if (usedNameIndex)
            {
                foreach (var position in candidateNamePositions)
                {
                    if (results.Count >= fuzzyCandidateBudget)
                    {
                        break;
                    }

                    AddIndexedNameCandidateUnlocked(
                        _nameOrder[position],
                        context,
                        visitedNames,
                        matchedNames,
                        results,
                        resultIds,
                        fuzzyCandidateBudget);
                }
            }
            else if (!context.IsShortQuery)
            {
                foreach (var nameId in _nameOrder)
                {
                    if (results.Count >= fuzzyCandidateBudget)
                    {
                        break;
                    }

                    AddIndexedNameCandidateUnlocked(
                        nameId,
                        context,
                        visitedNames,
                        matchedNames,
                        results,
                        resultIds,
                        fuzzyCandidateBudget);
                }
            }

            foreach (var nameId in _deltaNameIds)
            {
                if (results.Count >= fuzzyCandidateBudget)
                {
                    break;
                }

                if (!visitedNames.Contains(nameId) && NameMatchesAllUnlocked(nameId, context))
                {
                    visitedNames.Add(nameId);
                    matchedNames.Add(nameId);
                    AddDeltaRecordsForNameUnlocked(nameId, context, results, resultIds, fuzzyCandidateBudget);
                }
            }

            // A one-character query deliberately stops once it can fill the UI;
            // fuzzy expansion of millions of q...x names is neither useful nor
            // compatible with the SQLite short-query lane.
            // A name hit on a directory also makes its descendants path-tier
            // candidates.  Traverse the compact children CSR instead of scanning
            // every record/parent chain for rare hits and misses.
            if (results.Count < fuzzyCandidateBudget && matchedNames.Count > 0)
            {
                AddDescendantCandidatesUnlocked(
                    matchedNames,
                    context,
                    results,
                    resultIds,
                    fuzzyCandidateBudget);
            }

            // SQLite's quoted trigram FTS lane is contiguous.  If that lane is
            // still short of the display limit, it samples 200 deterministic
            // fallback rows and lets ResultRanker apply fuzzy/path semantics.
            // Mirroring that boundary avoids promoting every subsequence match
            // that happens to share the rarest gram (for example 2026 vs 2025).
            if (results.Count < query.Limit
                && context.PathTermBytes.Length == 0
                && context.PhraseBytes.Length == 0)
            {
                AddDeterministicFallbackUnlocked(
                    context,
                    results,
                    resultIds,
                    Math.Min(candidateBudget, checked(results.Count + 200)));
            }

            return results;
        }
    }

    public IReadOnlyList<FileRecord> SnapshotAllRecords()
    {
        lock (_gate)
        {
            var records = new FileRecord[_liveCount];
            var cursor = 0;
            for (var id = 0; id < _entryCount; id++)
            {
                if (_entries[id].IsLive)
                {
                    records[cursor++] = MaterializeRecordUnlocked(id);
                }
            }

            return records;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = new CompactFileEntry[InitialEntryCapacity];
            _entryCount = 0;
            _liveCount = 0;
            _tombstoneCount = 0;
            _names = new CompactNameStore();
            _buildPathLookup = new CompactHashLookup();
            _buildNameLookup = new CompactHashLookup();
            _nameOrder = Array.Empty<int>();
            _shortSearchNameIds = Array.Empty<int>();
            _rootIds = Array.Empty<int>();
            _frnOrder = Array.Empty<int>();
            _nameRecordOffsets = Array.Empty<int>();
            _nameRecordIds = Array.Empty<int>();
            _nameRecordIdsById = Array.Empty<int>();
            _childOffsets = Array.Empty<int>();
            _childIds = Array.Empty<int>();
            _trigramIndex = CompactTrigramIndex.Empty;
            _shortAliasIndex = CompactTrigramIndex.Empty;
            _ordersPublished = false;
            ClearDeltaUnlocked();
            _generation++;
        }
    }

    private int UpsertUnlocked(FileRecord record)
    {
        // FileRecord guarantees an absolute normalized FullPath.  Avoid another
        // Path.GetFullPath call for every MFT row; it dominates child-before-parent
        // scans with millions of records.
        var fullPath = Path.TrimEndingDirectorySeparator(record.FullPath);
        if (TryFindNormalizedPathUnlocked(fullPath, out var existingId))
        {
            var wasLive = _entries[existingId].IsLive;
            var wasPlaceholder = IsPlaceholder(_entries[existingId]);
            var parentId = _entries[existingId].ParentId;
            var recordName = record.Name;
            var nameId = _names.OriginalEquals(_entries[existingId].NameId, recordName)
                ? _entries[existingId].NameId
                : GetOrAddNameUnlocked(recordName);
            ref var existing = ref _entries[existingId];
            existing.NameId = nameId;
            existing.SizeBytes = record.SizeBytes;
            existing.SetLastWriteAndPathAlias(
                record.LastWriteTime.UtcDateTime.Ticks,
                NameOrParentHasAliasUnlocked(nameId, parentId));
            existing.FileReferenceNumber = record.FileReferenceNumber;
            existing.SetParentAndFlags(parentId, record.IsDirectory, isLive: true);
            if (!wasLive)
            {
                _liveCount++;
                if (!wasPlaceholder && _tombstoneCount > 0)
                {
                    _tombstoneCount--;
                }
            }

            MarkRecordDeltaUnlocked(existingId);
            if (_ordersPublished)
            {
                (_deltaPathLookup ??= new CompactHashLookup()).Add(HashPath(fullPath), existingId);
            }
            _generation++;
            return existingId;
        }

        var newParentId = EnsureStructuralPathUnlocked(record.ParentPath);
        var newNameId = GetOrAddNameUnlocked(record.Name);
        var id = AppendEntryUnlocked(
            fullPath,
            newParentId,
            newNameId,
            record.IsDirectory,
            isLive: true,
            record.SizeBytes,
            record.LastWriteTime.UtcDateTime.Ticks,
            record.FileReferenceNumber);
        _liveCount++;
        _generation++;
        return id;
    }

    private int EnsureStructuralPathUnlocked(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return -1;
        }

        var normalized = Path.TrimEndingDirectorySeparator(fullPath);
        if (TryFindNormalizedPathUnlocked(normalized, out var existingId))
        {
            return existingId;
        }

        var parentId = EnsureStructuralPathUnlocked(GetParentPath(normalized));
        var nameId = GetOrAddNameUnlocked(GetName(normalized));
        return AppendEntryUnlocked(
            normalized,
            parentId,
            nameId,
            isDirectory: true,
            isLive: false,
            sizeBytes: PlaceholderSize,
            lastWriteUtcTicks: 0,
            fileReferenceNumber: 0);
    }

    private int AppendEntryUnlocked(
        string fullPath,
        int parentId,
        int nameId,
        bool isDirectory,
        bool isLive,
        long sizeBytes,
        long lastWriteUtcTicks,
        ulong fileReferenceNumber)
    {
        EnsureEntryCapacityUnlocked(_entryCount + 1);
        var id = _entryCount++;
        ref var entry = ref _entries[id];
        entry.NameId = nameId;
        entry.SizeBytes = sizeBytes;
        entry.SetLastWriteAndPathAlias(
            lastWriteUtcTicks,
            NameOrParentHasAliasUnlocked(nameId, parentId));
        entry.FileReferenceNumber = fileReferenceNumber;
        entry.SetParentAndFlags(parentId, isDirectory, isLive);

        if (_ordersPublished)
        {
            (_deltaPathLookup ??= new CompactHashLookup()).Add(HashPath(fullPath), id);
            MarkRecordDeltaUnlocked(id);
        }
        else
        {
            _buildPathLookup!.Add(HashPath(fullPath), id);
        }

        return id;
    }

    private int GetOrAddNameUnlocked(string name)
    {
        var hash = HashOrdinal(name);
        if (!_ordersPublished)
        {
            if (_buildNameLookup!.TryFind(hash, id => _names.OriginalEquals(id, name), out var existing))
            {
                return existing;
            }
        }
        else
        {
            if (TryFindPublishedNameUnlocked(name, out var existing))
            {
                return existing;
            }

            if (_deltaNameLookup is not null
                && _deltaNameLookup.TryFind(hash, id => _names.OriginalEquals(id, name), out existing))
            {
                return existing;
            }
        }

        var nameId = _names.Add(name);
        if (_ordersPublished)
        {
            (_deltaNameLookup ??= new CompactHashLookup()).Add(hash, nameId);
            if (_deltaNameSet.Add(nameId))
            {
                _deltaNameIds.Add(nameId);
            }
        }
        else
        {
            _buildNameLookup!.Add(hash, nameId);
        }

        return nameId;
    }

    private void DeleteByPathUnlocked(string fullPath)
    {
        if (TryFindPathUnlocked(fullPath, out var id) && _entries[id].IsLive)
        {
            MarkDeletedUnlocked(id);
        }
    }

    private void MarkDeletedUnlocked(int id)
    {
        ref var entry = ref _entries[id];
        if (!entry.IsLive)
        {
            return;
        }

        entry.MarkDeleted();
        _liveCount--;
        _tombstoneCount++;
        MarkRecordDeltaUnlocked(id);
        _generation++;
    }

    private void HardLinkResyncUnlocked(ulong fileReferenceNumber, IReadOnlyList<FileRecord> liveRecords)
    {
        var ids = FindLiveFrnIdsUnlocked(fileReferenceNumber);
        foreach (var id in ids)
        {
            MarkDeletedUnlocked(id);
        }

        foreach (var record in liveRecords)
        {
            UpsertUnlocked(record.WithFileReferenceNumber(fileReferenceNumber));
        }
    }

    private IReadOnlyList<int> FindLiveFrnIdsUnlocked(ulong fileReferenceNumber)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        if (_ordersPublished)
        {
            var start = LowerBoundFrnUnlocked(fileReferenceNumber);
            for (var i = start; i < _frnOrder.Length; i++)
            {
                var id = _frnOrder[i];
                var entry = _entries[id];
                if (entry.FileReferenceNumber != fileReferenceNumber)
                {
                    break;
                }

                if (entry.IsLive && seen.Add(id))
                {
                    result.Add(id);
                }
            }

            foreach (var id in _deltaRecordIds)
            {
                var entry = _entries[id];
                if (entry.IsLive && entry.FileReferenceNumber == fileReferenceNumber && seen.Add(id))
                {
                    result.Add(id);
                }
            }
        }
        else
        {
            for (var id = 0; id < _entryCount; id++)
            {
                if (_entries[id].IsLive && _entries[id].FileReferenceNumber == fileReferenceNumber)
                {
                    result.Add(id);
                }
            }
        }

        return result;
    }

    private bool TryFindPathUnlocked(string path, out int id)
    {
        var normalized = NormalizePath(path);
        return TryFindNormalizedPathUnlocked(normalized, out id);
    }

    private bool TryFindNormalizedPathUnlocked(string normalized, out int id)
    {
        var hash = HashPath(normalized);
        if (!_ordersPublished)
        {
            return _buildPathLookup!.TryFind(
                hash,
                candidate => PathEqualsUnlocked(candidate, normalized),
                out id);
        }

        if (_deltaPathLookup is not null
            && _deltaPathLookup.TryFind(
                hash,
                candidate => PathEqualsUnlocked(candidate, normalized),
                out id))
        {
            return true;
        }

        var root = Path.GetPathRoot(normalized) ?? string.Empty;
        var rootName = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!TryFindRootUnlocked(rootName, out var current))
        {
            id = -1;
            return false;
        }

        var remaining = normalized.AsSpan(Math.Min(root.Length, normalized.Length));
        while (!remaining.IsEmpty)
        {
            while (!remaining.IsEmpty
                && (remaining[0] == Path.DirectorySeparatorChar
                    || remaining[0] == Path.AltDirectorySeparatorChar))
            {
                remaining = remaining[1..];
            }

            if (remaining.IsEmpty)
            {
                break;
            }

            var separator = remaining.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var component = separator < 0 ? remaining : remaining[..separator];
            var folded = Encoding.UTF8.GetBytes(component.ToString().ToLowerInvariant());
            if (!TryFindChildUnlocked(current, folded, out current))
            {
                id = -1;
                return false;
            }

            remaining = separator < 0 ? ReadOnlySpan<char>.Empty : remaining[(separator + 1)..];
        }

        if (PathEqualsUnlocked(current, normalized))
        {
            id = current;
            return true;
        }

        id = -1;
        return false;
    }

    private bool TryFindRootUnlocked(string rootName, out int id)
    {
        var folded = Encoding.UTF8.GetBytes(rootName.ToLowerInvariant());
        var low = 0;
        var high = _rootIds.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_names.GetFolded(_entries[_rootIds[middle]].NameId).SequenceCompareTo(folded) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var i = low; i < _rootIds.Length; i++)
        {
            var candidate = _rootIds[i];
            var comparison = _names.GetFolded(_entries[candidate].NameId).SequenceCompareTo(folded);
            if (comparison != 0)
            {
                break;
            }

            if (_entries[candidate].ParentId < 0)
            {
                id = candidate;
                return true;
            }
        }

        id = -1;
        return false;
    }

    private bool TryFindChildUnlocked(int parentId, ReadOnlySpan<byte> folded, out int id)
    {
        if ((uint)(parentId + 1) >= (uint)_childOffsets.Length)
        {
            id = -1;
            return false;
        }

        var low = _childOffsets[parentId];
        var high = _childOffsets[parentId + 1];
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            var candidate = _childIds[middle];
            if (_names.GetFolded(_entries[candidate].NameId).SequenceCompareTo(folded) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        var end = _childOffsets[parentId + 1];
        for (var i = low; i < end; i++)
        {
            var candidate = _childIds[i];
            var comparison = _names.GetFolded(_entries[candidate].NameId).SequenceCompareTo(folded);
            if (comparison != 0)
            {
                break;
            }

            if (_entries[candidate].ParentId == parentId)
            {
                id = candidate;
                return true;
            }
        }

        id = -1;
        return false;
    }

    private bool TryFindPublishedNameUnlocked(string name, out int nameId)
    {
        if (_nameOrder.Length == 0)
        {
            nameId = -1;
            return false;
        }

        var folded = Encoding.UTF8.GetBytes(name.ToLowerInvariant());
        var start = LowerBoundNameUnlocked(folded);
        for (var i = start; i < _nameOrder.Length; i++)
        {
            var candidate = _nameOrder[i];
            if (!_names.GetFolded(candidate).SequenceEqual(folded))
            {
                break;
            }

            if (_names.OriginalEquals(candidate, name))
            {
                nameId = candidate;
                return true;
            }
        }

        nameId = -1;
        return false;
    }

    private void OptimizeUnlocked()
    {
        var nameOrder = Enumerable.Range(0, _names.Count).ToArray();
        Array.Sort(nameOrder, CompareNameIdsUnlocked);

        var rootIds = Enumerable.Range(0, _entryCount)
            .Where(id => _entries[id].ParentId < 0)
            .ToArray();
        Array.Sort(rootIds, CompareEntryNameIdsUnlocked);

        var frnOrder = Enumerable.Range(0, _entryCount)
            .Where(id => _entries[id].IsLive && _entries[id].FileReferenceNumber != 0)
            .ToArray();
        Array.Sort(frnOrder, CompareFrnIdsUnlocked);

        var offsets = new int[_names.Count + 1];
        for (var id = 0; id < _entryCount; id++)
        {
            offsets[_entries[id].NameId + 1]++;
        }

        for (var i = 1; i < offsets.Length; i++)
        {
            offsets[i] = checked(offsets[i] + offsets[i - 1]);
        }

        var recordIds = new int[_entryCount];
        var cursors = (int[])offsets.Clone();
        for (var id = 0; id < _entryCount; id++)
        {
            var nameId = _entries[id].NameId;
            recordIds[cursors[nameId]++] = id;
        }
        var recordIdsById = (int[])recordIds.Clone();

        // Match SQLite's deterministic exact/prefix lane within a basename:
        // length(full_path), then UTF-8 BINARY full_path.  The temporary length
        // vector is released after the CSR slices are sorted.
        var nameCharLengths = new int[_names.Count];
        for (var nameId = 0; nameId < _names.Count; nameId++)
        {
            nameCharLengths[nameId] = Encoding.UTF8.GetCharCount(_names.GetOriginal(nameId));
        }

        var pathCharLengths = new int[_entryCount];
        for (var id = 0; id < _entryCount; id++)
        {
            var entry = _entries[id];
            var nameLength = nameCharLengths[entry.NameId];
            pathCharLengths[id] = entry.ParentId < 0
                ? nameLength + (IsDriveRootName(_names.GetOriginal(entry.NameId)) ? 1 : 0)
                : checked(pathCharLengths[entry.ParentId] + 1 + nameLength);
        }

        var pathComparer = Comparer<int>.Create((left, right) =>
        {
            var length = pathCharLengths[left].CompareTo(pathCharLengths[right]);
            return length != 0 ? length : CompareFullPathUtf8Unlocked(left, right);
        });
        for (var nameId = 0; nameId < _names.Count; nameId++)
        {
            var start = offsets[nameId];
            var count = offsets[nameId + 1] - start;
            if (count > 1)
            {
                Array.Sort(recordIds, start, count, pathComparer);
            }
        }

        var pathAliasNames = new bool[_names.Count];
        for (var id = 0; id < _entryCount; id++)
        {
            if (_entries[id].IsLive && _entries[id].PathHasAlias)
            {
                pathAliasNames[_entries[id].NameId] = true;
            }
        }

        var shortSearchNameIds = nameOrder
            .Where(nameId => !_names.GetAlias(nameId).IsEmpty
                || pathAliasNames[nameId])
            .ToArray();

        var childOffsets = new int[_entryCount + 1];
        var childCount = 0;
        for (var id = 0; id < _entryCount; id++)
        {
            var parentId = _entries[id].ParentId;
            if (parentId >= 0)
            {
                childOffsets[parentId + 1]++;
                childCount++;
            }
        }

        for (var i = 1; i < childOffsets.Length; i++)
        {
            childOffsets[i] = checked(childOffsets[i] + childOffsets[i - 1]);
        }

        var childIds = new int[childCount];
        var childCursors = (int[])childOffsets.Clone();
        for (var id = 0; id < _entryCount; id++)
        {
            var parentId = _entries[id].ParentId;
            if (parentId >= 0)
            {
                childIds[childCursors[parentId]++] = id;
            }
        }


        var childNameComparer = Comparer<int>.Create(CompareEntryNameIdsUnlocked);
        for (var parentId = 0; parentId < _entryCount; parentId++)
        {
            var start = childOffsets[parentId];
            var count = childOffsets[parentId + 1] - start;
            if (count > 1)
            {
                Array.Sort(childIds, start, count, childNameComparer);
            }
        }

        _nameOrder = nameOrder;
        _shortSearchNameIds = shortSearchNameIds;
        _rootIds = rootIds;
        _frnOrder = frnOrder;
        _nameRecordOffsets = offsets;
        _nameRecordIds = recordIds;
        _nameRecordIdsById = recordIdsById;
        _childOffsets = childOffsets;
        _childIds = childIds;
        _trigramIndex = CompactTrigramIndex.Build(_names, nameOrder);
        _shortAliasIndex = CompactTrigramIndex.BuildShortAliases(_names, shortSearchNameIds);
        _ordersPublished = true;
        _buildPathLookup = null;
        _buildNameLookup = null;
        ClearDeltaUnlocked();

        // Avoid retaining geometric growth slack after a multi-million-record build.
        var spareEntries = Math.Min(MaximumDeltaEntries, Math.Max(4_096, _entryCount / 100));
        var desiredCapacity = checked(_entryCount + spareEntries);
        if (_entries.Length > desiredCapacity)
        {
            Array.Resize(ref _entries, Math.Max(desiredCapacity, 1));
        }

        _names.TrimExcess();
    }

    private void ClearDeltaUnlocked()
    {
        _deltaPathLookup = null;
        _deltaNameLookup = null;
        _deltaRecordIds.Clear();
        _deltaNameIds.Clear();
        _deltaRecordSet.Clear();
        _deltaNameSet.Clear();
    }

    private void MarkRecordDeltaUnlocked(int id)
    {
        if (_ordersPublished && _deltaRecordSet.Add(id))
        {
            _deltaRecordIds.Add(id);
        }
    }

    private void AddShortAliasCandidatesUnlocked(
        CandidateContext context,
        HashSet<int> visitedNames,
        HashSet<int> matchedNames,
        List<FileRecord> results,
        HashSet<int> resultIds,
        int targetCount)
    {
        if (context.PhraseBytes.Length > 0)
        {
            return;
        }

        _shortAliasIndex.TryGetRarestCandidatePositions(
            context.TermBytes,
            out var positions,
            minimumPatternLength: 1,
            maximumGramLength: 2);
        var buckets = new SortedDictionary<int, List<ShortNameCandidate>>();
        foreach (var position in positions)
        {
            var nameId = _shortSearchNameIds[position];
            var alias = _names.GetAlias(nameId);
            var aliasMatch = !alias.IsEmpty && ContainsAll(alias, context.TermBytes);
            var foldedMatch = ContainsAll(_names.GetFolded(nameId), context.TermBytes);
            if (!aliasMatch && !foldedMatch)
            {
                continue;
            }

            var length = Encoding.UTF8.GetCharCount(_names.GetOriginal(nameId));
            if (!buckets.TryGetValue(length, out var bucket))
            {
                bucket = new List<ShortNameCandidate>();
                buckets.Add(length, bucket);
            }

            bucket.Add(new ShortNameCandidate(nameId, aliasMatch || !context.IsSingleCharacter));
        }

        foreach (var bucket in buckets.Values)
        {
            foreach (var candidate in bucket)
            {
                if (results.Count >= targetCount)
                {
                    return;
                }

                var before = results.Count;
                AddShortAliasRecordsForNameUnlocked(
                    candidate.NameId,
                    context,
                    results,
                    resultIds,
                    targetCount,
                    candidate.QualifyAllRecords);
                if (results.Count > before || !_names.GetAlias(candidate.NameId).IsEmpty)
                {
                    visitedNames.Add(candidate.NameId);
                    matchedNames.Add(candidate.NameId);
                }
            }
        }

        foreach (var nameId in _deltaNameIds)
        {
            if (results.Count >= targetCount)
            {
                break;
            }

            var alias = _names.GetAlias(nameId);
            var aliasMatch = !alias.IsEmpty && ContainsAll(alias, context.TermBytes);
            var foldedMatch = ContainsAll(_names.GetFolded(nameId), context.TermBytes);
            if (aliasMatch || foldedMatch)
            {
                var before = results.Count;
                AddShortAliasRecordsForNameUnlocked(
                    nameId,
                    context,
                    results,
                    resultIds,
                    targetCount,
                    aliasMatch || !context.IsSingleCharacter);
                if (results.Count > before || aliasMatch)
                {
                    visitedNames.Add(nameId);
                    matchedNames.Add(nameId);
                }
            }
        }
    }

    private void AddShortAliasRecordsForNameUnlocked(
        int nameId,
        CandidateContext context,
        List<FileRecord> results,
        HashSet<int> resultIds,
        int targetCount,
        bool qualifyAllRecords)
    {
        if ((uint)(nameId + 1) < (uint)_nameRecordOffsets.Length)
        {
            for (var i = _nameRecordOffsets[nameId];
                 i < _nameRecordOffsets[nameId + 1] && results.Count < targetCount;
                 i++)
            {
                var id = _nameRecordIds[i];
                if (qualifyAllRecords || _entries[id].PathHasAlias)
                {
                    TryAddCandidateUnlocked(id, context, results, resultIds, requirePositiveMatch: true);
                }
            }
        }

        foreach (var id in _deltaRecordIds)
        {
            if (results.Count >= targetCount)
            {
                return;
            }

            if (_entries[id].NameId == nameId
                && (qualifyAllRecords || _entries[id].PathHasAlias))
            {
                TryAddCandidateUnlocked(id, context, results, resultIds, requirePositiveMatch: true);
            }
        }
    }

    private bool NameOrParentHasAliasUnlocked(int nameId, int parentId) =>
        !_names.GetAlias(nameId).IsEmpty
        || (parentId >= 0 && _entries[parentId].PathHasAlias);

    private void AddDeterministicFallbackUnlocked(
        CandidateContext context,
        List<FileRecord> results,
        HashSet<int> resultIds,
        int targetCount)
    {
        // SQLite samples at most 200 deterministic fallback rows and only then
        // applies the expensive fuzzy scorer.  For the common term-only case,
        // preserve that row boundary while rejecting obvious non-matches against
        // the compact parent/name representation.  This avoids materializing and
        // ranking hundreds of full paths for a rare hit without changing which
        // rows SQLite's fallback lane was allowed to inspect.
        var useBoundedCompactPrefilter = context.PhraseBytes.Length == 0
            && context.PathTermBytes.Length == 0
            && context.Query.Parsed.ExcludedTerms.Count == 0
            && !context.Query.Parsed.ApplicationsOnly;
        var remainingSampleRows = Math.Max(0, targetCount - results.Count);
        var sampledRows = 0;
        foreach (var nameId in _nameOrder)
        {
            if (results.Count >= targetCount
                || (useBoundedCompactPrefilter && sampledRows >= remainingSampleRows))
            {
                break;
            }

            if ((uint)(nameId + 1) >= (uint)_nameRecordOffsets.Length)
            {
                continue;
            }

            for (var i = _nameRecordOffsets[nameId];
                 i < _nameRecordOffsets[nameId + 1] && results.Count < targetCount;
                 i++)
            {
                var id = _nameRecordIds[i];
                if (useBoundedCompactPrefilter)
                {
                    ref readonly var entry = ref _entries[id];
                    if (!entry.IsLive || !PassesCompactFiltersUnlocked(entry, context))
                    {
                        continue;
                    }

                    sampledRows++;
                    if (resultIds.Contains(id) || !CompactRecordCouldMatchUnlocked(id, context))
                    {
                        if (sampledRows >= remainingSampleRows)
                        {
                            break;
                        }

                        continue;
                    }
                }

                if ((context.PathTermBytes.Length > 0 || context.PhraseBytes.Length > 0)
                    && !CompactRecordCouldMatchUnlocked(id, context))
                {
                    continue;
                }

                TryAddCandidateUnlocked(
                    id,
                    context,
                    results,
                    resultIds,
                    requirePositiveMatch: false);
            }
        }
    }

    private void AddPrefixNameCandidatesUnlocked(
        byte[] prefix,
        CandidateContext context,
        HashSet<int> visitedNames,
        HashSet<int> matchedNames,
        List<FileRecord> results,
        HashSet<int> resultIds,
        int limit,
        CompactTrigramIndex.PostingList? narrowingPositions)
    {
        var start = LowerBoundNameUnlocked(prefix);
        if (narrowingPositions is null)
        {
            for (var i = start; i < _nameOrder.Length && results.Count < limit; i++)
            {
                var nameId = _nameOrder[i];
                if (!_names.GetFolded(nameId).StartsWith(prefix))
                {
                    break;
                }

                AddPrefixNameCandidateUnlocked(
                    nameId,
                    prefix,
                    context,
                    visitedNames,
                    matchedNames,
                    results,
                    resultIds,
                    limit);
            }
        }
        else
        {
            foreach (var position in narrowingPositions.Value)
            {
                if (position < start)
                {
                    continue;
                }

                var nameId = _nameOrder[position];
                if (!_names.GetFolded(nameId).StartsWith(prefix))
                {
                    // All names with a prefix form one contiguous lexical range.
                    break;
                }

                AddPrefixNameCandidateUnlocked(
                    nameId,
                    prefix,
                    context,
                    visitedNames,
                    matchedNames,
                    results,
                    resultIds,
                    limit);
                if (results.Count >= limit)
                {
                    break;
                }
            }
        }

        foreach (var nameId in _deltaNameIds)
        {
            if (results.Count >= limit)
            {
                break;
            }

            if (_names.GetFolded(nameId).StartsWith(prefix)
                && !visitedNames.Contains(nameId)
                && NameMatchesPrefixLaneUnlocked(nameId, prefix, context))
            {
                visitedNames.Add(nameId);
                matchedNames.Add(nameId);
                AddDeltaRecordsForNameUnlocked(nameId, context, results, resultIds, limit);
            }
        }
    }

    private void AddIndexedNameCandidateUnlocked(
        int nameId,
        CandidateContext context,
        HashSet<int> visitedNames,
        HashSet<int> matchedNames,
        List<FileRecord> results,
        HashSet<int> resultIds,
        int limit)
    {
        if (visitedNames.Contains(nameId) || !NameMatchesAllUnlocked(nameId, context))
        {
            return;
        }

        visitedNames.Add(nameId);
        matchedNames.Add(nameId);
        AddRecordsForNameUnlocked(
            nameId,
            context,
            results,
            resultIds,
            limit,
            useInsertionOrder: true);
    }

    private void AddPrefixNameCandidateUnlocked(
        int nameId,
        ReadOnlySpan<byte> prefix,
        CandidateContext context,
        HashSet<int> visitedNames,
        HashSet<int> matchedNames,
        List<FileRecord> results,
        HashSet<int> resultIds,
        int limit)
    {
        if (visitedNames.Contains(nameId)
            || !NameMatchesPrefixLaneUnlocked(nameId, prefix, context))
        {
            return;
        }

        visitedNames.Add(nameId);
        matchedNames.Add(nameId);
        AddRecordsForNameUnlocked(nameId, context, results, resultIds, limit);
    }

    private void AddRecordsForNameUnlocked(
        int nameId,
        CandidateContext context,
        List<FileRecord> results,
        HashSet<int> resultIds,
        int limit,
        bool useInsertionOrder = false)
    {
        if ((uint)(nameId + 1) >= (uint)_nameRecordOffsets.Length)
        {
            return;
        }

        var start = _nameRecordOffsets[nameId];
        var end = _nameRecordOffsets[nameId + 1];
        var recordIds = useInsertionOrder ? _nameRecordIdsById : _nameRecordIds;
        for (var i = start; i < end && results.Count < limit; i++)
        {
            var id = recordIds[i];
            if (_entries[id].NameId == nameId)
            {
                TryAddCandidateUnlocked(id, context, results, resultIds, requirePositiveMatch: true);
            }
        }

        AddDeltaRecordsForNameUnlocked(nameId, context, results, resultIds, limit);
    }

    private void AddDeltaRecordsForNameUnlocked(
        int nameId,
        CandidateContext context,
        List<FileRecord> results,
        HashSet<int> resultIds,
        int limit)
    {
        foreach (var id in _deltaRecordIds)
        {
            if (results.Count >= limit)
            {
                break;
            }

            if (_entries[id].NameId == nameId)
            {
                TryAddCandidateUnlocked(id, context, results, resultIds, requirePositiveMatch: true);
            }
        }
    }

    private void AddDescendantCandidatesUnlocked(
        IReadOnlySet<int> matchingNameIds,
        CandidateContext context,
        List<FileRecord> results,
        HashSet<int> resultIds,
        int limit)
    {
        const int maximumVisitedNodes = 100_000;
        var stack = new Stack<int>();
        var traversedDirectories = new HashSet<int>();

        foreach (var nameId in matchingNameIds)
        {
            if ((uint)(nameId + 1) < (uint)_nameRecordOffsets.Length)
            {
                for (var i = _nameRecordOffsets[nameId]; i < _nameRecordOffsets[nameId + 1]; i++)
                {
                    var id = _nameRecordIds[i];
                    if (_entries[id].IsDirectory
                        && (_entries[id].IsLive || IsPlaceholder(_entries[id]))
                        && _entries[id].NameId == nameId
                        && traversedDirectories.Add(id))
                    {
                        stack.Push(id);
                    }
                }
            }

            foreach (var id in _deltaRecordIds)
            {
                if (_entries[id].IsDirectory
                    && (_entries[id].IsLive || IsPlaceholder(_entries[id]))
                    && _entries[id].NameId == nameId
                    && traversedDirectories.Add(id))
                {
                    stack.Push(id);
                }
            }
        }

        var visitedNodes = 0;
        while (stack.Count > 0
            && results.Count < limit
            && visitedNodes < maximumVisitedNodes)
        {
            var parentId = stack.Pop();
            if ((uint)(parentId + 1) < (uint)_childOffsets.Length)
            {
                for (var i = _childOffsets[parentId];
                     i < _childOffsets[parentId + 1]
                     && results.Count < limit
                     && visitedNodes < maximumVisitedNodes;
                     i++)
                {
                    var childId = _childIds[i];
                    if (_entries[childId].ParentId != parentId)
                    {
                        continue;
                    }

                    visitedNodes++;
                    if (_entries[childId].IsLive
                        && CompactRecordCouldMatchUnlocked(childId, context))
                    {
                        TryAddCandidateUnlocked(
                            childId,
                            context,
                            results,
                            resultIds,
                            requirePositiveMatch: true);
                    }

                    if (_entries[childId].IsDirectory && traversedDirectories.Add(childId))
                    {
                        stack.Push(childId);
                    }
                }
            }

            // The base CSR is immutable.  A bounded post-publish delta is checked
            // separately so newly-created descendants are visible immediately.
            foreach (var childId in _deltaRecordIds)
            {
                if (results.Count >= limit || visitedNodes >= maximumVisitedNodes)
                {
                    break;
                }

                if (_entries[childId].ParentId != parentId)
                {
                    continue;
                }

                visitedNodes++;
                if (_entries[childId].IsLive
                    && CompactRecordCouldMatchUnlocked(childId, context))
                {
                    TryAddCandidateUnlocked(
                        childId,
                        context,
                        results,
                        resultIds,
                        requirePositiveMatch: true);
                }

                if (_entries[childId].IsDirectory && traversedDirectories.Add(childId))
                {
                    stack.Push(childId);
                }
            }
        }
    }

    private bool TryAddCandidateUnlocked(
        int id,
        CandidateContext context,
        List<FileRecord> results,
        HashSet<int> resultIds,
        bool requirePositiveMatch)
    {
        if (!resultIds.Add(id))
        {
            return false;
        }

        ref readonly var entry = ref _entries[id];
        if (!entry.IsLive || !PassesCompactFiltersUnlocked(entry, context))
        {
            resultIds.Remove(id);
            return false;
        }

        var record = MaterializeRecordUnlocked(id);
        if (!PassesParsedFilters(context.Query, record)
            || (requirePositiveMatch && !RecordMatchesPositive(context.Query, record)))
        {
            resultIds.Remove(id);
            return false;
        }

        results.Add(record);
        return true;
    }

    private bool PassesCompactFiltersUnlocked(in CompactFileEntry entry, CandidateContext context)
    {
        if (context.Query.IsDirectory is not null
            && entry.IsDirectory != context.Query.IsDirectory.Value)
        {
            return false;
        }

        if (context.Query.Parsed.FileOnly && entry.IsDirectory)
        {
            return false;
        }

        if (context.Query.EffectiveMode == SearchMode.FoldersOnly && !entry.IsDirectory)
        {
            return false;
        }

        if (context.ModifiedAfterTicks is not null
            && entry.LastWriteUtcTicks < context.ModifiedAfterTicks.Value)
        {
            return false;
        }

        var name = _names.GetFolded(entry.NameId);
        if (context.RequiredExtensions.Length > 0
            && (entry.IsDirectory || !EndsWithAnyExtension(name, context.RequiredExtensions)))
        {
            return false;
        }

        if (context.IncludedExtensions.Length > 0
            && !EndsWithAnyExtension(name, context.IncludedExtensions))
        {
            return false;
        }

        if (context.ExcludedExtensions.Length > 0
            && EndsWithAnyExtension(name, context.ExcludedExtensions))
        {
            return false;
        }

        if (context.Query.Parsed.ApplicationsOnly
            && (entry.IsDirectory || !LooksLikeApplicationExtension(name)))
        {
            return false;
        }

        return true;
    }

    private bool NameMatchesAllUnlocked(int nameId, CandidateContext context)
    {
        var folded = _names.GetFolded(nameId);
        var alias = _names.GetAlias(nameId);
        foreach (var phrase in context.PhraseBytes)
        {
            if (folded.IndexOf(phrase) < 0)
            {
                return false;
            }
        }

        foreach (var term in context.TermBytes)
        {
            var foldedMatch = folded.IndexOf(term) >= 0;
            var aliasMatch = !alias.IsEmpty && alias.IndexOf(term) >= 0;
            if (!foldedMatch && !aliasMatch)
            {
                return false;
            }
        }

        return true;
    }

    private bool NameMatchesPrefixLaneUnlocked(
        int nameId,
        ReadOnlySpan<byte> prefix,
        CandidateContext context)
    {
        var folded = _names.GetFolded(nameId);
        if (!folded.StartsWith(prefix))
        {
            return false;
        }

        // A casing-derived snake/kebab seed deliberately stands in for its one
        // original token (ListaryOpen -> listary_open).
        if (context.PositiveTerms.Length == 1
            && !context.PositiveTerms[0].AsSpan().SequenceEqual(prefix))
        {
            return true;
        }

        var alias = _names.GetAlias(nameId);
        foreach (var phrase in context.PhraseBytes)
        {
            if (folded.IndexOf(phrase) < 0)
            {
                return false;
            }
        }

        foreach (var term in context.TermBytes)
        {
            if (folded.IndexOf(term) < 0 && (alias.IsEmpty || alias.IndexOf(term) < 0))
            {
                return false;
            }
        }

        return true;
    }

    private bool CompactRecordCouldMatchUnlocked(int id, CandidateContext context)
    {
        ref readonly var entry = ref _entries[id];
        if (!entry.IsLive || !PassesCompactFiltersUnlocked(entry, context))
        {
            return false;
        }

        var folded = _names.GetFolded(entry.NameId);
        var alias = _names.GetAlias(entry.NameId);
        foreach (var phrase in context.PhraseBytes)
        {
            if (folded.IndexOf(phrase) < 0
                && !(ContainsPathSeparator(phrase) && FuzzyPathMatchUnlocked(id, phrase)))
            {
                return false;
            }
        }

        foreach (var term in context.TermBytes)
        {
            if (!FuzzyContains(folded, term)
                && (alias.IsEmpty || !FuzzyContains(alias, term))
                && !(ContainsPathSeparator(term) && FuzzyPathMatchUnlocked(id, term)))
            {
                return false;
            }
        }

        foreach (var pathTerm in context.PathTermBytes)
        {
            if (!FuzzyPathMatchUnlocked(id, pathTerm))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsPathSeparator(ReadOnlySpan<byte> value) =>
        value.IndexOf((byte)'\\') >= 0 || value.IndexOf((byte)'/') >= 0;

    private bool FuzzyPathMatchUnlocked(int id, ReadOnlySpan<byte> query)
    {
        if (query.IsEmpty)
        {
            return false;
        }

        Span<int> stack = stackalloc int[256];
        var depth = FillAncestorStackUnlocked(id, stack);
        if (depth < 0)
        {
            // Paths deeper than the Windows practical limit are exceptionally
            // rare.  Materialize only this record rather than allocating per scan.
            var path = MaterializeFullPathUnlocked(id).ToLowerInvariant();
            return FuzzyMatcher.Score(Encoding.UTF8.GetString(query), path) > 0;
        }

        var queryIndex = 0;
        for (var componentIndex = depth - 1; componentIndex >= 0 && queryIndex < query.Length; componentIndex--)
        {
            if (componentIndex != depth - 1 && query[queryIndex] == (byte)'\\')
            {
                queryIndex++;
            }

            var component = _names.GetFolded(_entries[stack[componentIndex]].NameId);
            foreach (var value in component)
            {
                if (queryIndex < query.Length && value == query[queryIndex])
                {
                    queryIndex++;
                }
            }
        }

        return queryIndex == query.Length;
    }

    private int FillAncestorStackUnlocked(int id, Span<int> destination)
    {
        var count = 0;
        var current = id;
        while (current >= 0)
        {
            if (count >= destination.Length)
            {
                return -1;
            }

            destination[count++] = current;
            current = _entries[current].ParentId;
        }

        return count;
    }

    private FileRecord MaterializeRecordUnlocked(int id)
    {
        ref readonly var entry = ref _entries[id];
        var fullPath = MaterializeFullPathUnlocked(id);
        return FileRecord.CreateFromNormalizedPath(
            fullPath,
            entry.IsDirectory,
            entry.SizeBytes,
            new DateTimeOffset(new DateTime(entry.LastWriteUtcTicks, DateTimeKind.Utc)),
            entry.FileReferenceNumber);
    }

    private string MaterializeFullPathUnlocked(int id)
    {
        Span<int> stack = stackalloc int[256];
        var depth = FillAncestorStackUnlocked(id, stack);
        if (depth < 0)
        {
            throw new InvalidDataException("A compact path exceeds the supported parent depth.");
        }

        var builder = new StringBuilder(depth * 12);
        for (var i = depth - 1; i >= 0; i--)
        {
            var component = _names.MaterializeOriginal(_entries[stack[i]].NameId);
            if (builder.Length > 0 && builder[^1] != Path.DirectorySeparatorChar)
            {
                builder.Append(Path.DirectorySeparatorChar);
            }

            builder.Append(component);
        }

        if (depth == 1 && builder.Length == 2 && builder[1] == ':')
        {
            builder.Append(Path.DirectorySeparatorChar);
        }

        return builder.ToString();
    }

    private bool PathEqualsUnlocked(int id, string normalizedPath)
    {
        var remaining = normalizedPath.AsSpan();
        while (!remaining.IsEmpty && IsDirectorySeparator(remaining[^1]))
        {
            remaining = remaining[..^1];
        }

        var current = id;
        while (current >= 0)
        {
            ref readonly var entry = ref _entries[current];
            if (entry.ParentId < 0)
            {
                return FoldedNameEqualsComponentUnlocked(entry.NameId, remaining);
            }

            var separator = remaining.LastIndexOfAny(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var component = separator < 0 ? remaining : remaining[(separator + 1)..];
            if (!FoldedNameEqualsComponentUnlocked(entry.NameId, component))
            {
                return false;
            }

            remaining = separator < 0 ? ReadOnlySpan<char>.Empty : remaining[..separator];
            while (!remaining.IsEmpty && IsDirectorySeparator(remaining[^1]))
            {
                remaining = remaining[..^1];
            }

            current = entry.ParentId;
        }

        return remaining.IsEmpty;
    }

    private bool FoldedNameEqualsComponentUnlocked(int nameId, ReadOnlySpan<char> component)
    {
        var folded = _names.GetFolded(nameId);
        var ascii = true;
        for (var i = 0; i < component.Length; i++)
        {
            if (component[i] > 0x7f)
            {
                ascii = false;
                break;
            }
        }

        if (ascii)
        {
            if (folded.Length != component.Length)
            {
                return false;
            }

            for (var i = 0; i < component.Length; i++)
            {
                if (folded[i] != (byte)char.ToLowerInvariant(component[i]))
                {
                    return false;
                }
            }

            return true;
        }

        var normalized = component.ToString().ToLowerInvariant();
        return folded.SequenceEqual(Encoding.UTF8.GetBytes(normalized));
    }

    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

    private bool IsSelfOrDescendantUnlocked(int id, int ancestorId)
    {
        var current = id;
        while (current >= 0)
        {
            if (current == ancestorId)
            {
                return true;
            }

            current = _entries[current].ParentId;
        }

        return false;
    }

    private int LowerBoundNameUnlocked(ReadOnlySpan<byte> folded)
    {
        var low = 0;
        var high = _nameOrder.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_names.GetFolded(_nameOrder[middle]).SequenceCompareTo(folded) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private int LowerBoundFrnUnlocked(ulong frn)
    {
        var low = 0;
        var high = _frnOrder.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (_entries[_frnOrder[middle]].FileReferenceNumber < frn)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private int CompareNameIdsUnlocked(int left, int right)
    {
        var folded = _names.GetFolded(left).SequenceCompareTo(_names.GetFolded(right));
        if (folded != 0)
        {
            return folded;
        }

        var original = _names.GetOriginal(left).SequenceCompareTo(_names.GetOriginal(right));
        return original != 0 ? original : left.CompareTo(right);
    }

    private int CompareEntryNameIdsUnlocked(int left, int right)
    {
        var names = CompareNameIdsUnlocked(_entries[left].NameId, _entries[right].NameId);
        return names != 0 ? names : left.CompareTo(right);
    }

    private int CompareFullPathUtf8Unlocked(int left, int right)
    {
        Span<int> leftStack = stackalloc int[256];
        Span<int> rightStack = stackalloc int[256];
        var leftDepth = FillAncestorStackUnlocked(left, leftStack);
        var rightDepth = FillAncestorStackUnlocked(right, rightStack);
        if (leftDepth < 0 || rightDepth < 0)
        {
            return string.CompareOrdinal(
                MaterializeFullPathUnlocked(left),
                MaterializeFullPathUnlocked(right));
        }

        var leftComponent = leftDepth - 1;
        var rightComponent = rightDepth - 1;
        var leftByteIndex = 0;
        var rightByteIndex = 0;
        var leftSeparator = false;
        var rightSeparator = false;
        while (true)
        {
            var hasLeft = TryReadNextPathByteUnlocked(
                leftStack,
                ref leftComponent,
                ref leftByteIndex,
                ref leftSeparator,
                out var leftByte);
            var hasRight = TryReadNextPathByteUnlocked(
                rightStack,
                ref rightComponent,
                ref rightByteIndex,
                ref rightSeparator,
                out var rightByte);
            if (!hasLeft || !hasRight)
            {
                if (hasLeft != hasRight)
                {
                    return hasLeft ? 1 : -1;
                }

                return left.CompareTo(right);
            }

            var comparison = leftByte.CompareTo(rightByte);
            if (comparison != 0)
            {
                return comparison;
            }
        }
    }

    private bool TryReadNextPathByteUnlocked(
        ReadOnlySpan<int> stack,
        ref int component,
        ref int byteIndex,
        ref bool emitSeparator,
        out byte value)
    {
        while (component >= 0)
        {
            if (emitSeparator)
            {
                emitSeparator = false;
                value = (byte)'\\';
                return true;
            }

            var name = _names.GetOriginal(_entries[stack[component]].NameId);
            if (byteIndex < name.Length)
            {
                value = name[byteIndex++];
                return true;
            }

            component--;
            byteIndex = 0;
            emitSeparator = component >= 0;
        }

        value = 0;
        return false;
    }

    private int CompareFrnIdsUnlocked(int left, int right)
    {
        var frn = _entries[left].FileReferenceNumber.CompareTo(_entries[right].FileReferenceNumber);
        return frn != 0 ? frn : left.CompareTo(right);
    }

    private void EnsureEntryCapacityUnlocked(int required)
    {
        if (required <= _entries.Length)
        {
            return;
        }

        var capacity = _entries.Length;
        while (capacity < required)
        {
            capacity = checked(capacity < 1_000_000
                ? capacity * 2
                : capacity + Math.Min(capacity / 2, 65_536));
        }

        Array.Resize(ref _entries, capacity);
    }

    private static bool PassesParsedFilters(SearchQuery query, FileRecord record)
    {
        if (query.Parsed.ApplicationsOnly
            && !ApplicationPath.IsApplication(record.FullPath, record.IsDirectory))
        {
            return false;
        }

        foreach (var term in query.Parsed.PathTerms)
        {
            if (!PathSegmentMatcher.Matches(record.FullPath, term))
            {
                return false;
            }
        }

        foreach (var phrase in query.Parsed.Phrases)
        {
            if (!record.FullPath.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        foreach (var term in query.Parsed.ExcludedTerms)
        {
            if (record.FullPath.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RecordMatchesPositive(SearchQuery query, FileRecord record)
    {
        var name = record.Name.Trim().ToLowerInvariant();
        foreach (var phrase in query.Parsed.Phrases)
        {
            if (!name.Contains(phrase, StringComparison.Ordinal)
                && !(phrase.IndexOfAny(['\\', '/']) >= 0
                    && PathSegmentMatcher.Score(record.FullPath, phrase) > 0))
            {
                return false;
            }
        }

        foreach (var term in query.Parsed.Terms)
        {
            if (name.Contains(term, StringComparison.Ordinal))
            {
                continue;
            }

            if (FuzzyMatcher.Score(term, name) > 0)
            {
                continue;
            }

            if (PinyinMatcher.Score(term, record.Name) > 0
                || (term.IndexOfAny(['\\', '/']) >= 0
                    && PathSegmentMatcher.Score(record.FullPath, term) > 0))
            {
                continue;
            }

            return false;
        }

        return query.Parsed.Phrases.Count + query.Parsed.Terms.Count > 0;
    }

    private static bool EndsWithAnyExtension(ReadOnlySpan<byte> foldedName, byte[][] extensions)
    {
        foreach (var extension in extensions)
        {
            if (foldedName.Length > extension.Length
                && foldedName[foldedName.Length - extension.Length - 1] == (byte)'.'
                && foldedName.EndsWith(extension))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeApplicationExtension(ReadOnlySpan<byte> foldedName) =>
        EndsWithAscii(foldedName, ".lnk"u8)
        || EndsWithAscii(foldedName, ".exe"u8)
        || EndsWithAscii(foldedName, ".appref-ms"u8)
        || EndsWithAscii(foldedName, ".msc"u8);

    private static bool EndsWithAscii(ReadOnlySpan<byte> value, ReadOnlySpan<byte> suffix) =>
        value.EndsWith(suffix);

    private static bool FuzzyContains(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> query)
    {
        if (query.IsEmpty || candidate.IsEmpty)
        {
            return false;
        }

        if (candidate.IndexOf(query) >= 0)
        {
            return true;
        }

        var queryIndex = 0;
        foreach (var value in candidate)
        {
            if (value == query[queryIndex] && ++queryIndex == query.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAll(ReadOnlySpan<byte> candidate, byte[][] terms)
    {
        foreach (var term in terms)
        {
            if (candidate.IndexOf(term) < 0)
            {
                return false;
            }
        }

        return terms.Length > 0;
    }

    private static bool IsPlaceholder(in CompactFileEntry entry) =>
        !entry.IsLive && entry.SizeBytes == PlaceholderSize;

    private static bool IsDriveRootName(ReadOnlySpan<byte> name) =>
        name.Length == 2 && name[1] == (byte)':';

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        return Path.TrimEndingDirectorySeparator(normalized);
    }

    private static string GetName(string normalizedPath)
    {
        var root = Path.GetPathRoot(normalizedPath) ?? string.Empty;
        return string.Equals(
                Path.TrimEndingDirectorySeparator(normalizedPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase)
            ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetFileName(normalizedPath);
    }

    private static string GetParentPath(string normalizedPath)
    {
        var root = Path.GetPathRoot(normalizedPath) ?? string.Empty;
        return string.Equals(
                Path.TrimEndingDirectorySeparator(normalizedPath),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Path.GetDirectoryName(normalizedPath) ?? string.Empty;
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            || normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static ulong HashPath(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(path);
        var hash = FnvOffset;
        foreach (var source in normalized)
        {
            var value = source == Path.AltDirectorySeparatorChar
                ? Path.DirectorySeparatorChar
                : char.ToUpperInvariant(source);
            hash = (hash ^ (byte)value) * FnvPrime;
            hash = (hash ^ (byte)(value >> 8)) * FnvPrime;
        }

        return hash;
    }

    private static ulong HashOrdinal(string value)
    {
        var hash = FnvOffset;
        foreach (var ch in value)
        {
            hash = (hash ^ (byte)ch) * FnvPrime;
            hash = (hash ^ (byte)(ch >> 8)) * FnvPrime;
        }

        return hash;
    }

    private void SetMemoryCheckpointUnlocked(UsnJournalCheckpoint checkpoint)
    {
        var state = new NameTableCheckpointState(
            NormalizePath(checkpoint.VolumeRoot),
            checkpoint.FileSystemName,
            checkpoint.UsnJournalId,
            checkpoint.NextUsn,
            checkpoint.RulesVersion,
            checkpoint.LastFullScanAt.ToUnixTimeMilliseconds());
        _memoryCheckpoints[CheckpointKey(state.VolumeRoot)] = state;
        _generation++;
    }

    private void SetCompatibilityWatermarkUnlocked(NameTableCheckpointState? state)
    {
        if (state is null)
        {
            _durableWatermarkVolume = null;
            _durableWatermarkUsn = 0;
            _durableWatermarkJournalId = 0;
            _durableWatermarkRulesVersion = 0;
            return;
        }

        _durableWatermarkVolume = state.VolumeRoot;
        _durableWatermarkUsn = state.NextUsn;
        _durableWatermarkJournalId = state.UsnJournalId;
        _durableWatermarkRulesVersion = state.RulesVersion;
    }

    private static UsnJournalCheckpoint ToCheckpoint(NameTableCheckpointState state) => new(
        state.VolumeRoot,
        state.FileSystemName,
        state.UsnJournalId,
        state.NextUsn,
        state.RulesVersion,
        DateTimeOffset.FromUnixTimeMilliseconds(state.LastFullScanUnixMilliseconds));

    private static string CheckpointKey(string volumeRoot) =>
        NormalizePath(volumeRoot).ToUpperInvariant();

    private static void ValidateCompactSnapshot(NameTableCompactSnapshot snapshot)
    {
        if (snapshot.Generation < 0
            || snapshot.EntryCount < 0
            || snapshot.LiveCount < 0
            || snapshot.TombstoneCount < 0
            || snapshot.EntryCount != snapshot.Entries.Length
            || snapshot.LiveCount > snapshot.EntryCount
            || snapshot.Names.Count < 0
            || snapshot.Names.HeapLength != snapshot.Names.Heap.Length
            || snapshot.Names.Count != snapshot.Names.Offsets.Length
            || snapshot.NameOrder.Length != snapshot.Names.Count
            || snapshot.NameRecordOffsets.Length != snapshot.Names.Count + 1
            || snapshot.NameRecordIds.Length != snapshot.EntryCount
            || snapshot.NameRecordIdsById.Length != snapshot.EntryCount
            || snapshot.ChildOffsets.Length != snapshot.EntryCount + 1)
        {
            throw new InvalidDataException("Compact NameTable snapshot dimensions are invalid.");
        }

        var observedLive = 0;
        for (var id = 0; id < snapshot.EntryCount; id++)
        {
            var entry = snapshot.Entries[id];
            if ((uint)entry.NameId >= (uint)snapshot.Names.Count
                || entry.ParentId >= snapshot.EntryCount
                || entry.ParentId == id)
            {
                throw new InvalidDataException("Compact NameTable entry reference is invalid.");
            }

            if (entry.IsLive)
            {
                observedLive++;
            }
        }

        if (observedLive != snapshot.LiveCount)
        {
            throw new InvalidDataException("Compact NameTable live count does not match entries.");
        }

        ValidateIdArray(snapshot.NameOrder, snapshot.Names.Count, "name order");
        ValidateIdArray(snapshot.ShortSearchNameIds, snapshot.Names.Count, "short-search names");
        ValidateIdArray(snapshot.RootIds, snapshot.EntryCount, "root ids");
        ValidateIdArray(snapshot.FrnOrder, snapshot.EntryCount, "FRN order");
        ValidateIdArray(snapshot.NameRecordIds, snapshot.EntryCount, "name postings");
        ValidateIdArray(snapshot.NameRecordIdsById, snapshot.EntryCount, "name insertion postings");
        ValidateIdArray(snapshot.ChildIds, snapshot.EntryCount, "child postings");
        ValidateOffsets(snapshot.NameRecordOffsets, snapshot.NameRecordIds.Length, "name postings");
        ValidateOffsets(snapshot.ChildOffsets, snapshot.ChildIds.Length, "child postings");

        foreach (var checkpoint in snapshot.Checkpoints)
        {
            if (string.IsNullOrWhiteSpace(checkpoint.VolumeRoot)
                || string.IsNullOrWhiteSpace(checkpoint.FileSystemName)
                || checkpoint.NextUsn < 0)
            {
                throw new InvalidDataException("Compact NameTable checkpoint is invalid.");
            }
        }
    }

    private static void ValidateIdArray(int[] values, int upperExclusive, string section)
    {
        foreach (var value in values)
        {
            if ((uint)value >= (uint)upperExclusive)
            {
                throw new InvalidDataException($"Compact NameTable {section} contains an invalid id.");
            }
        }
    }

    private static void ValidateOffsets(int[] offsets, int finalLength, string section)
    {
        if (offsets.Length == 0 || offsets[0] != 0 || offsets[^1] != finalLength)
        {
            throw new InvalidDataException($"Compact NameTable {section} bounds are invalid.");
        }

        for (var i = 1; i < offsets.Length; i++)
        {
            if (offsets[i] < offsets[i - 1])
            {
                throw new InvalidDataException($"Compact NameTable {section} offsets are not monotonic.");
            }
        }
    }

    private static string CamelToSnake(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 3 || !value.Any(char.IsUpper))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i > 0 && char.IsUpper(c))
            {
                var previous = value[i - 1];
                var nextLower = i + 1 < value.Length && char.IsLower(value[i + 1]);
                if (char.IsLower(previous) || (char.IsUpper(previous) && nextLower))
                {
                    builder.Append('_');
                }
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        var snake = builder.ToString();
        return string.Equals(snake, value, StringComparison.OrdinalIgnoreCase) ? string.Empty : snake;
    }

    private readonly record struct ShortNameCandidate(int NameId, bool QualifyAllRecords);

    private sealed class CandidateContext
    {
        internal CandidateContext(SearchQuery query)
        {
            Query = query;
            TermBytes = Encode(query.Parsed.Terms);
            PhraseBytes = Encode(query.Parsed.Phrases);
            PathTermBytes = Encode(query.Parsed.PathTerms.Select(NormalizeSeparators));
            PositiveTerms = PhraseBytes.Concat(TermBytes).ToArray();
            RequiredExtensions = EncodeExtensions(query.RequiredExtensions);
            IncludedExtensions = EncodeExtensions(query.Parsed.Extensions);
            ExcludedExtensions = EncodeExtensions(query.Parsed.ExcludedExtensions);
            ModifiedAfterTicks = query.ModifiedAfter?.UtcDateTime.Ticks;
            IsSingleCharacter = query.NormalizedText.Trim().Length == 1;
            IsShortQuery = query.NormalizedText.Trim().Length is 1 or 2;

            var prefixSeeds = new List<byte[]>(PositiveTerms);
            var snake = CamelToSnake(query.Text.Trim());
            if (!string.IsNullOrEmpty(snake))
            {
                prefixSeeds.Add(Encoding.UTF8.GetBytes(snake));
            }

            PrefixSeeds = prefixSeeds
                .Where(value => value.Length > 0)
                .Distinct(ByteArrayComparer.Instance)
                .ToArray();
        }

        internal SearchQuery Query { get; }

        internal byte[][] TermBytes { get; }

        internal byte[][] PhraseBytes { get; }

        internal byte[][] PathTermBytes { get; }

        internal byte[][] PositiveTerms { get; }

        internal byte[][] PrefixSeeds { get; }

        internal byte[][] RequiredExtensions { get; }

        internal byte[][] IncludedExtensions { get; }

        internal byte[][] ExcludedExtensions { get; }

        internal long? ModifiedAfterTicks { get; }

        internal bool IsSingleCharacter { get; }

        internal bool IsShortQuery { get; }

        private static byte[][] Encode(IEnumerable<string> values) => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()))
            .ToArray();

        private static byte[][] EncodeExtensions(IEnumerable<string> values) => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Encoding.UTF8.GetBytes(value.Trim().TrimStart('.').ToLowerInvariant()))
            .ToArray();

        private static string NormalizeSeparators(string value) =>
            value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        internal static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? left, byte[]? right) =>
            ReferenceEquals(left, right)
            || (left is not null && right is not null && left.AsSpan().SequenceEqual(right));

        public int GetHashCode(byte[] value)
        {
            var hash = new HashCode();
            foreach (var item in value)
            {
                hash.Add(item);
            }

            return hash.ToHashCode();
        }
    }
}
