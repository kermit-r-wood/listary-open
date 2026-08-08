using System.IO;
using System.Diagnostics;
using System.Runtime;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Search.NameTable;

/// <summary>
/// In-memory name-table search backend implementing the shipped <see cref="ISearchIndex"/> contract.
/// </summary>
public sealed class NameTableSearchIndex : ISearchIndex, IAsyncDisposable
{
    private readonly NameTableEngine _engine = new();
    private readonly LosnSnapshotStore _snapshotStore = new();
    private readonly string? _snapshotPath;
    private readonly object _usageGate = new();
    private readonly Dictionary<string, UsageRecord> _usage = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutatorGate = new(1, 1);
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private NameTableEngine? _buildingEngine;
    private string? _buildingRoot;
    private bool _disposed;

    public NameTableSearchIndex(string? snapshotPath = null)
    {
        _snapshotPath = snapshotPath;
    }

    public NameTableEngine Engine => _engine;

    public static async Task<NameTableSearchIndex> OpenAsync(
        string? snapshotPath,
        CancellationToken cancellationToken)
    {
        var index = new NameTableSearchIndex(snapshotPath);
        if (!string.IsNullOrWhiteSpace(snapshotPath)
            && (File.Exists(snapshotPath) || File.Exists(snapshotPath + ".bak")))
        {
            try
            {
                await index._snapshotStore
                    .LoadIntoAsync(snapshotPath, index._engine, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                // v1/suspect/mismatched snapshots cannot authorize USN resume.
                // Start empty so the coordinator performs a fail-closed MFT rebuild.
                Trace.TraceWarning("Ignoring unusable NameTable snapshot '{0}': {1}", snapshotPath, exception.Message);
            }
        }

        return index;
    }

    public async Task UpsertAsync(FileRecord record, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _engine.Upsert(record);
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    public async Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _engine.DeleteByPath(fullPath);
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    public async Task DeletePathAndDescendantsAsync(string fullPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _engine.DeletePathAndDescendants(fullPath);
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    public async Task BeginFullBuildRootAsync(string rootPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-entering after a failed/partial provider scan discards that
            // unpublished stage and starts clean for the fallback provider.
            _buildingRoot = normalized;
            _buildingEngine = new NameTableEngine();
            Trace.TraceInformation("NameTable staged root build started. Root={0}", normalized);
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    public async Task CommitFullBuildRootAsync(string rootPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_buildingEngine is null
                || !string.Equals(_buildingRoot, normalized, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"No staged full build exists for '{normalized}'.");
            }

            var stagedCount = _buildingEngine.LiveCount;
            var liveCountBefore = _engine.LiveCount;
            Trace.TraceInformation(
                "NameTable staged root commit started. Root={0}; Mode=replace; StagedCount={1}; LiveCountBefore={2}",
                normalized,
                stagedCount,
                liveCountBefore);
            _buildingEngine.Optimize();
            _engine.ReplaceRootFrom(normalized, _buildingEngine);
            Trace.TraceInformation(
                "NameTable staged root commit completed. Root={0}; Mode=replace; StagedCount={1}; LiveCountAfter={2}",
                normalized,
                stagedCount,
                _engine.LiveCount);
            _buildingEngine = null;
            _buildingRoot = null;
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    public async Task CommitFullBuildRootRetainingStaleAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_buildingEngine is null
                || !string.Equals(_buildingRoot, normalized, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"No staged full build exists for '{normalized}'.");
            }

            var stagedCount = _buildingEngine.LiveCount;
            var liveCountBefore = _engine.LiveCount;
            Trace.TraceInformation(
                "NameTable staged root commit started. Root={0}; Mode=merge-retaining-stale; StagedCount={1}; LiveCountBefore={2}",
                normalized,
                stagedCount,
                liveCountBefore);
            _buildingEngine.Optimize();
            _engine.MergeRootFrom(normalized, _buildingEngine);
            Trace.TraceInformation(
                "NameTable staged root commit completed. Root={0}; Mode=merge-retaining-stale; StagedCount={1}; LiveCountAfter={2}",
                normalized,
                stagedCount,
                _engine.LiveCount);
            _buildingEngine = null;
            _buildingRoot = null;
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    public async Task AbortFullBuildRootAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_buildingEngine is not null)
            {
                Trace.TraceWarning(
                    "NameTable staged root build aborted. Root={0}; StagedCount={1}",
                    _buildingRoot ?? "<unknown>",
                    _buildingEngine.LiveCount);
            }

            _buildingEngine = null;
            _buildingRoot = null;
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    public Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_usageGate)
        {
            var created = new UsageRecord(fullPath, 1, DateTimeOffset.UtcNow);
            if (_usage.TryGetValue(created.PathKey, out var existing))
            {
                _usage[created.PathKey] = new UsageRecord(
                    existing.FullPath,
                    existing.OpenCount + 1,
                    DateTimeOffset.UtcNow);
            }
            else
            {
                _usage[created.PathKey] = created;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchResult>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        }

        List<UsageRecord> usage;
        lock (_usageGate)
        {
            usage = _usage.Values
                .OrderByDescending(u => u.LastUsedAt)
                .Take(limit * 2)
                .ToList();
        }

        var results = new List<SearchResult>();
        foreach (var item in usage)
        {
            if (_engine.TryGetByPathKey(item.PathKey, out var record) && record is not null)
            {
                results.Add(new SearchResult(record, 1, "recent"));
                if (results.Count >= limit)
                {
                    break;
                }
            }
        }

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<FileRecord> candidates;
        using (PerformanceMetrics.MeasureStage("nametable.candidates"))
        {
            var candidateLimit = Math.Clamp(query.Limit * 20, 200, 5_000);
            candidates = _engine.CollectCandidates(query, candidateLimit);
        }

        IReadOnlyList<UsageRecord> usage;
        using (PerformanceMetrics.MeasureStage("nametable.usage"))
        {
            lock (_usageGate)
            {
                usage = candidates
                    .Select(c => _usage.TryGetValue(c.PathKey, out var u) ? u : null)
                    .Where(u => u is not null)
                    .Cast<UsageRecord>()
                    .ToArray();
            }
        }

        using (PerformanceMetrics.MeasureStage("nametable.rank"))
        {
            var ranked = ResultRanker.Rank(
                query,
                candidates,
                usage,
                Array.Empty<string>(),
                cancellationToken);
            return Task.FromResult(ranked);
        }
    }

    public async Task ApplyUsnMutationsAsync(
        IEnumerable<UsnJournalIndexChange> changes,
        CancellationToken cancellationToken)
        => await ApplyUsnMutationsAsync(changes, checkpoint: null, cancellationToken).ConfigureAwait(false);

    public async Task ApplyUsnMutationsAsync(
        IEnumerable<UsnJournalIndexChange> changes,
        UsnJournalCheckpoint? checkpoint,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = _buildingEngine ?? _engine;
            target.ApplyUsnMutations(changes);
            if (checkpoint is not null)
            {
                target.SetMemoryCheckpoint(checkpoint);
            }
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    /// <summary>
    /// Persists a durable LOSN image and updates the durable watermark.
    /// Returns false when no snapshot path is configured — memory-only watermarks
    /// are not durable and must not authorize SQLite checkpoint advancement (AC2/KD16).
    /// </summary>
    public async Task<bool> SaveDurableSnapshotAsync(
        UsnJournalCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (string.IsNullOrWhiteSpace(_snapshotPath))
        {
            return false;
        }

        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _snapshotStore
                .SaveAsync(_snapshotPath, _engine, checkpoint, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    public async Task UpdateMemoryCheckpointAsync(
        UsnJournalCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(checkpoint);
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _engine.SetMemoryCheckpoint(checkpoint);
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    public async Task<bool> FlushDurableSnapshotAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(_snapshotPath) || !_engine.HasPendingDurability)
        {
            return false;
        }

        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_engine.HasPendingDurability)
            {
                return false;
            }

            await _snapshotStore.SaveAsync(_snapshotPath, _engine, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    /// <summary>
    /// Releases large temporary build/snapshot arrays after the infrequent full
    /// reconciliation boundary. This is intentionally not used on query paths.
    /// </summary>
    public void TrimTransientBuildMemory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    public async Task UpsertManyAsync(IEnumerable<FileRecord> records, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _mutatorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (_buildingEngine ?? _engine).Upsert(record);
            }
        }
        finally
        {
            _mutatorGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _mutatorGate.Dispose();
        _snapshotGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
