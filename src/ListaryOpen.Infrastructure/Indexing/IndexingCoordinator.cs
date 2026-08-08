using System.Diagnostics;
using System.IO;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using ListaryOpen.Infrastructure.Search;
using ListaryOpen.Infrastructure.Search.NameTable;

namespace ListaryOpen.Infrastructure.Indexing;

public enum IndexingRunState
{
    Idle,
    Indexing,
    Completed,
    Canceled,
    Failed
}

public sealed record IndexingStatus(
    IndexingRunState State,
    string Message,
    int IndexedCount,
    string? CurrentRoot = null,
    int CurrentRootNumber = 0,
    int TotalRoots = 0);

public sealed class IndexingCoordinator
{
    /// <summary>Larger batches reduce per-batch overhead on full scans.</summary>
    private const int DefaultBatchSize = 10_000;

    /// <summary>Yield to the scheduler every N flushed batches so UI stays responsive.</summary>
    private const int BatchesBetweenYield = 4;

    private static readonly TimeSpan BatchYieldDelay = TimeSpan.FromMilliseconds(1);

    private readonly SqliteSearchIndex? _sqlite;
    private readonly NameTableSearchIndex? _nameTable;
    private readonly VolumeIndexer _volumeIndexer;
    private readonly IIndexProvider _fallbackProvider;
    private readonly Func<IndexRoot, VolumeInfo> _volumeResolver;
    private readonly int _batchSize;
    private readonly Func<FileRecord, bool> _recordFilter;

    /// <summary>Production path: in-memory NameTable + LOSN durability (no SQLite).</summary>
    public IndexingCoordinator(
        NameTableSearchIndex nameTable,
        VolumeIndexer volumeIndexer,
        IIndexProvider fallbackProvider,
        Func<IndexRoot, VolumeInfo>? volumeResolver = null,
        int batchSize = DefaultBatchSize,
        Func<FileRecord, bool>? recordFilter = null)
        : this(
            sqlite: null,
            nameTable: nameTable,
            volumeIndexer,
            fallbackProvider,
            volumeResolver,
            batchSize,
            recordFilter)
    {
    }

    /// <summary>Unit tests and tooling that still exercise the SQLite index path.</summary>
    public IndexingCoordinator(
        SqliteSearchIndex index,
        VolumeIndexer volumeIndexer,
        IIndexProvider fallbackProvider,
        Func<IndexRoot, VolumeInfo>? volumeResolver = null,
        int batchSize = DefaultBatchSize,
        Func<FileRecord, bool>? recordFilter = null)
        : this(
            sqlite: index,
            nameTable: null,
            volumeIndexer,
            fallbackProvider,
            volumeResolver,
            batchSize,
            recordFilter)
    {
    }

    private IndexingCoordinator(
        SqliteSearchIndex? sqlite,
        NameTableSearchIndex? nameTable,
        VolumeIndexer volumeIndexer,
        IIndexProvider fallbackProvider,
        Func<IndexRoot, VolumeInfo>? volumeResolver,
        int batchSize,
        Func<FileRecord, bool>? recordFilter)
    {
        if (sqlite is null && nameTable is null)
        {
            throw new ArgumentException("Either sqlite or nameTable is required.");
        }

        ArgumentNullException.ThrowIfNull(volumeIndexer);
        ArgumentNullException.ThrowIfNull(fallbackProvider);

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }

        _sqlite = sqlite;
        _nameTable = nameTable;
        _volumeIndexer = volumeIndexer;
        _fallbackProvider = fallbackProvider;
        _volumeResolver = volumeResolver ?? ResolveVolume;
        _batchSize = batchSize;
        _recordFilter = recordFilter ?? (_ => true);
    }

    public event EventHandler<IndexingStatus>? StatusChanged;

    public async Task IndexRootsAsync(IReadOnlyList<IndexRoot> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var runStartedAt = Stopwatch.GetTimestamp();
        Trace.TraceInformation(
            "Indexing run started. RootCount={0}; Roots=[{1}]",
            roots.Count,
            string.Join(" | ", roots.Select(root => root.Path)));
        var indexedCount = 0;
        string? currentRoot = null;
        var currentRootNumber = 0;
        var hadFailures = false;
        var hadCancellations = false;
        var retainedStaleRecords = false;
        var compatibilityScanRootCount = 0;
        var failureMessages = new List<string>();
        long? indexGeneration = null;
        IndexingRunState? terminalState = null;
        string? terminalMessage = null;
        RaiseStatus(
            IndexingRunState.Indexing,
            roots.Count == 0 ? "No indexed roots configured." : "Preparing index...",
            indexedCount,
            totalRoots: roots.Count);

        var bulkStarted = false;
        async Task<long> EnsureFullScanContextAsync(CancellationToken token)
        {
            if (_sqlite is null)
            {
                // NameTable has no FTS bulk/generation context.
                return indexGeneration ??= 0;
            }

            if (!bulkStarted)
            {
                await _sqlite.BeginBulkIndexingAsync(token).ConfigureAwait(false);
                bulkStarted = true;
            }

            indexGeneration ??= await _sqlite.BeginIndexingRunAsync(token).ConfigureAwait(false);
            return indexGeneration.Value;
        }

        try
        {
            for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var root = roots[rootIndex];
                currentRoot = root.Path;
                currentRootNumber = rootIndex + 1;
                RaiseStatus(
                    IndexingRunState.Indexing,
                    "Preparing indexed location...",
                    indexedCount,
                    currentRoot,
                    currentRootNumber,
                    roots.Count);

                if (!Directory.Exists(root.Path))
                {
                    hadFailures = true;
                    failureMessages.Add($"Root missing: {root.Path}");
                    Trace.TraceWarning("Index root does not exist or is unavailable: {0}", root.Path);
                    RaiseStatus(
                        IndexingRunState.Failed,
                        $"Root missing: {root.Path}",
                        indexedCount,
                        currentRoot,
                        currentRootNumber,
                        roots.Count);
                    continue;
                }

                try
                {
                    var provider = SelectProvider(root);
                    Trace.TraceInformation(
                        "Index root provider selected. Root={0}; Provider={1}; RootNumber={2}; TotalRoots={3}",
                        root.Path,
                        provider.Name,
                        currentRootNumber,
                        roots.Count);
                    if (!IsNtfsProvider(provider))
                    {
                        compatibilityScanRootCount++;
                    }
                    RaiseStatus(
                        IndexingRunState.Indexing,
                        $"Scanning with {provider.Name}...",
                        indexedCount,
                        currentRoot,
                        currentRootNumber,
                        roots.Count);
                    var result = await IndexRootWithProviderAsync(
                        provider,
                        root,
                        indexedCount,
                        EnsureFullScanContextAsync,
                        currentRootNumber,
                        roots.Count,
                        cancellationToken).ConfigureAwait(false);
                    indexedCount += result.IndexedCount;
                    hadFailures |= result.HadFailure;
                    hadCancellations |= result.WasCanceled;
                    retainedStaleRecords |= result.RetainedStaleRecords;
                    if (result.UsedCompatibilityScanning)
                    {
                        compatibilityScanRootCount++;
                    }

                    Trace.TraceInformation(
                        "Index root finished. Root={0}; Provider={1}; IndexedCount={2}; HadFailure={3}; WasCanceled={4}; RetainedStaleRecords={5}; UsedCompatibilityScanning={6}",
                        root.Path,
                        provider.Name,
                        result.IndexedCount,
                        result.HadFailure,
                        result.WasCanceled,
                        result.RetainedStaleRecords,
                        result.UsedCompatibilityScanning);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    hadFailures = true;
                    failureMessages.Add($"{root.Path}: {exception.Message}");
                    Trace.TraceError(
                        "Index root failed. Root={0}; Exception={1}",
                        root.Path,
                        exception);
                    RaiseStatus(
                        IndexingRunState.Failed,
                        $"Indexing failed for {root.Path}: {exception.Message}",
                        indexedCount,
                        currentRoot,
                        currentRootNumber,
                        roots.Count);
                }
            }

            terminalState = hadFailures
                ? IndexingRunState.Failed
                : hadCancellations
                    ? IndexingRunState.Canceled
                    : IndexingRunState.Completed;
            terminalMessage = hadFailures
                ? CreateFailureSummary(failureMessages)
                : hadCancellations
                    ? "Indexing canceled."
                    : retainedStaleRecords
                        ? "Indexing completed; stale records retained because pruning was skipped."
                        : compatibilityScanRootCount > 0
                            ? $"Indexing completed using compatibility scanning for {compatibilityScanRootCount} location(s)."
                        : "Indexing completed.";

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Trace.TraceWarning(
                "Indexing run canceled. IndexedCount={0}; CurrentRoot={1}",
                indexedCount,
                currentRoot ?? "<none>");
            RaiseStatus(
                IndexingRunState.Canceled,
                "Indexing canceled.",
                indexedCount,
                currentRoot,
                currentRootNumber,
                roots.Count);
            throw;
        }
        finally
        {
            if (_nameTable is not null)
            {
                try
                {
                    // Successful roots commit explicitly. Any stage still present
                    // here is partial/canceled and must never replace the old epoch.
                    await _nameTable.AbortFullBuildRootAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Trace.TraceWarning("Failed to discard staged NameTable build: {0}", exception);
                }
            }

            if (bulkStarted && _sqlite is not null)
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Do not turn shutdown cancellation into a multi-minute
                        // FTS rebuild. Restore triggers and leave a durable marker
                        // so the next successful reconciliation rebuilds it once.
                        await _sqlite
                            .AbortBulkIndexingAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        RaiseStatus(
                            IndexingRunState.Indexing,
                            "Finalizing search index...",
                            indexedCount,
                            currentRoot,
                            currentRootNumber,
                            roots.Count);
                        await _sqlite
                            .EndBulkIndexingAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Trace.TraceWarning("Failed to finalize bulk FTS rebuild: {0}", exception);
                }
            }
            else if (_sqlite is not null
                && !cancellationToken.IsCancellationRequested
                && !hadFailures
                && !hadCancellations)
            {
                try
                {
                    // Recover a snapshot left incomplete by an earlier canceled
                    // scan only after journal reconciliation succeeds. OpenAsync
                    // deliberately does not race startup indexing with this work.
                    await _sqlite
                        .EnsureFtsReadyAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Trace.TraceWarning("Failed to recover FTS after reconciliation: {0}", exception);
                }
            }

            if (_nameTable is not null
                && !cancellationToken.IsCancellationRequested
                && !hadCancellations
                && _nameTable.Engine.HasPendingDurability)
            {
                try
                {
                    // Each full root is staged and only successful roots are
                    // committed to the live engine. A failure in another root
                    // must not keep those valid commits memory-only: persisting
                    // the live engine retains the previous epoch for failed
                    // roots while making successful roots durable.
                    Trace.TraceInformation(
                        "Saving NameTable snapshot after indexing. IndexedCount={0}; HadFailures={1}",
                        indexedCount,
                        hadFailures);
                    var saved = await _nameTable
                        .FlushDurableSnapshotAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    Trace.TraceInformation(
                        "NameTable snapshot save finished. Saved={0}; LiveCount={1}",
                        saved,
                        _nameTable.Engine.LiveCount);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Trace.TraceWarning("Failed to save NameTable LOSN after indexing: {0}", exception);
                }
            }

            if (_nameTable is not null)
            {
                _nameTable.TrimTransientBuildMemory();
            }

            if (terminalState is not null && terminalMessage is not null)
            {
                RaiseStatus(terminalState.Value, terminalMessage, indexedCount, totalRoots: roots.Count);
                Trace.TraceInformation(
                    "Indexing run finished. State={0}; IndexedCount={1}; Elapsed={2}; Failures=[{3}]",
                    terminalState.Value,
                    indexedCount,
                    Stopwatch.GetElapsedTime(runStartedAt),
                    string.Join(" | ", failureMessages));
            }
        }
    }

    private IIndexProvider SelectProvider(IndexRoot root)
    {
        try
        {
            return _volumeIndexer.SelectProvider(_volumeResolver(root));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Provider selection failed for {root.Path}.", exception);
        }
    }

    private async Task<IndexRootResult> IndexRootWithProviderAsync(
        IIndexProvider provider,
        IndexRoot root,
        int currentIndexedCount,
        Func<CancellationToken, Task<long>> ensureFullScanContextAsync,
        int currentRootNumber,
        int totalRoots,
        CancellationToken cancellationToken)
    {
        if (!IsNtfsProvider(provider))
        {
            var indexGeneration = await ensureFullScanContextAsync(cancellationToken).ConfigureAwait(false);
            await PrepareFullScanRootAsync(root, cancellationToken).ConfigureAwait(false);
            var count = await ScanAndUpsertAsync(
                provider,
                root,
                currentIndexedCount,
                indexGeneration,
                currentRootNumber,
                totalRoots,
                cancellationToken).ConfigureAwait(false);
            RaiseStatus(
                IndexingRunState.Indexing,
                "Finalizing indexed location...",
                currentIndexedCount + count,
                root.Path,
                currentRootNumber,
                totalRoots);
            await PruneStaleUnderRootAsync(root.Path, indexGeneration, cancellationToken).ConfigureAwait(false);
            await CommitFullScanRootAsync(root, cancellationToken).ConfigureAwait(false);
            return new IndexRootResult(count, HadFailure: false, WasCanceled: false);
        }

        UsnJournalState? preScanJournalState = null;
        try
        {
            RaiseStatus(
                IndexingRunState.Indexing,
                "Checking NTFS journal...",
                currentIndexedCount,
                root.Path,
                currentRootNumber,
                totalRoots);
            var catchUp = await TryCatchUpNtfsRootAsync(
                provider,
                root,
                cancellationToken).ConfigureAwait(false);
            preScanJournalState = catchUp.JournalState;
            if (catchUp.CompletedWithoutFullScan)
            {
                return new IndexRootResult(catchUp.IndexedCount, HadFailure: false, WasCanceled: false);
            }

            var indexGeneration = await ensureFullScanContextAsync(cancellationToken).ConfigureAwait(false);
            await PrepareFullScanRootAsync(root, cancellationToken).ConfigureAwait(false);
            var count = await ScanAndUpsertAsync(
                provider,
                root,
                currentIndexedCount,
                indexGeneration,
                currentRootNumber,
                totalRoots,
                cancellationToken).ConfigureAwait(false);
            RaiseStatus(
                IndexingRunState.Indexing,
                "Finalizing indexed location...",
                currentIndexedCount + count,
                root.Path,
                currentRootNumber,
                totalRoots);
            var postScanJournalState = await TryApplyPostScanNtfsChangesAsync(
                provider,
                root,
                preScanJournalState,
                indexGeneration,
                cancellationToken).ConfigureAwait(false);
            if (postScanJournalState is not null)
            {
                await PruneStaleUnderRootAsync(root.Path, indexGeneration, cancellationToken).ConfigureAwait(false);
                await CommitFullScanRootAsync(root, cancellationToken).ConfigureAwait(false);
                await SaveNtfsCheckpointAsync(root, postScanJournalState, cancellationToken).ConfigureAwait(false);
                return new IndexRootResult(count, HadFailure: false, WasCanceled: false);
            }

            if (count > 0)
            {
                await CommitFullScanRootRetainingStaleAsync(root, cancellationToken).ConfigureAwait(false);

                RaiseStatus(
                    IndexingRunState.Indexing,
                    $"NTFS prune skipped for {root.Path}: scan completeness could not be verified; scanned records were retained.",
                    currentIndexedCount + count,
                    root.Path,
                    currentRootNumber,
                    totalRoots);
                return new IndexRootResult(
                    count,
                    HadFailure: false,
                    WasCanceled: false,
                    RetainedStaleRecords: true);
            }

            RaiseStatus(
                IndexingRunState.Indexing,
                $"NTFS returned no records; using fallback for {root.Path}.",
                currentIndexedCount,
                root.Path,
                currentRootNumber,
                totalRoots);
            var fallbackCount = await ScanAndUpsertAsync(
                _fallbackProvider,
                root,
                currentIndexedCount,
                indexGeneration,
                currentRootNumber,
                totalRoots,
                cancellationToken).ConfigureAwait(false);
            await CommitFullScanRootRetainingStaleAsync(root, cancellationToken).ConfigureAwait(false);
            RaiseStatus(
                IndexingRunState.Indexing,
                $"NTFS prune skipped for {root.Path}: recovery scan has no journal completeness proof.",
                currentIndexedCount + fallbackCount,
                root.Path,
                currentRootNumber,
                totalRoots);
            return new IndexRootResult(
                fallbackCount,
                HadFailure: false,
                WasCanceled: false,
                RetainedStaleRecords: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IndexProviderScanException exception)
        {
            Trace.TraceWarning(
                "NTFS provider scan failed. Root={0}; PartialRecordsAccepted={1}; Exception={2}",
                root.Path,
                exception.PartialRecordsAccepted,
                exception);
            if (IsElevatedIndexerLaunchCanceled(exception))
            {
                if (_nameTable is not null)
                {
                    await _nameTable.AbortFullBuildRootAsync(CancellationToken.None).ConfigureAwait(false);
                }

                RaiseStatus(
                    IndexingRunState.Canceled,
                    $"NTFS scan canceled for {root.Path}.",
                    currentIndexedCount,
                    root.Path,
                    currentRootNumber,
                    totalRoots);
                return new IndexRootResult(0, HadFailure: false, WasCanceled: true);
            }

            if (exception.PartialRecordsAccepted)
            {
                var indexGeneration = await ensureFullScanContextAsync(cancellationToken).ConfigureAwait(false);
                await PrepareFullScanRootAsync(root, cancellationToken).ConfigureAwait(false);
                RaiseStatus(
                    IndexingRunState.Indexing,
                    $"NTFS scan failed after partial output for {root.Path}; restarting with fallback. {exception.Message}",
                    currentIndexedCount,
                    root.Path,
                    currentRootNumber,
                    totalRoots);
                var recoveredCount = await ScanAndUpsertAsync(
                    _fallbackProvider,
                    root,
                    currentIndexedCount,
                    indexGeneration,
                    currentRootNumber,
                    totalRoots,
                    cancellationToken).ConfigureAwait(false);
                RaiseStatus(
                    IndexingRunState.Indexing,
                    "Finalizing recovered indexed location...",
                    currentIndexedCount + recoveredCount,
                    root.Path,
                    currentRootNumber,
                    totalRoots);
                await PruneStaleUnderRootAsync(
                    root.Path,
                    indexGeneration,
                    cancellationToken).ConfigureAwait(false);
                await CommitFullScanRootAsync(root, cancellationToken).ConfigureAwait(false);
                return new IndexRootResult(
                    recoveredCount,
                    HadFailure: false,
                    WasCanceled: false,
                    RetainedStaleRecords: false,
                    UsedCompatibilityScanning: true);
            }

            RaiseStatus(
                IndexingRunState.Failed,
                $"NTFS scan failed for {root.Path}; using fallback. {exception.Message}",
                currentIndexedCount,
                root.Path,
                currentRootNumber,
                totalRoots);
            var fallbackGeneration = await ensureFullScanContextAsync(cancellationToken).ConfigureAwait(false);
            await PrepareFullScanRootAsync(root, cancellationToken).ConfigureAwait(false);
            var fallbackCount = await ScanAndUpsertAsync(
                _fallbackProvider,
                root,
                currentIndexedCount,
                fallbackGeneration,
                currentRootNumber,
                totalRoots,
                cancellationToken).ConfigureAwait(false);
            await CommitFullScanRootRetainingStaleAsync(root, cancellationToken).ConfigureAwait(false);
            RaiseStatus(
                IndexingRunState.Indexing,
                $"NTFS prune skipped for {root.Path}: recovery scan has no journal completeness proof.",
                currentIndexedCount + fallbackCount,
                root.Path,
                currentRootNumber,
                totalRoots);
            return new IndexRootResult(
                fallbackCount,
                HadFailure: false,
                WasCanceled: false,
                RetainedStaleRecords: true,
                UsedCompatibilityScanning: true);
        }
    }

    private async Task<NtfsCatchUpResult> TryCatchUpNtfsRootAsync(
        IIndexProvider provider,
        IndexRoot root,
        CancellationToken cancellationToken)
    {
        if (provider is not INtfsJournalProvider journalProvider)
        {
            Trace.TraceWarning(
                "NTFS completeness check unavailable. Root={0}; Provider={1}; Reason=provider does not expose journal state",
                root.Path,
                provider.Name);
            return NtfsCatchUpResult.NeedsFullScan(journalState: null);
        }

        UsnJournalState? journalState;
        try
        {
            journalState = await journalProvider
                .QueryJournalStateAsync(root, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsElevatedIndexerLaunchCanceled(exception))
        {
            throw new IndexProviderScanException(
                $"Provider {provider.Name} journal query was canceled for {root.Path}: {exception.Message}",
                exception);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("NTFS journal query failed for '{0}': {1}", root.Path, exception);
            return NtfsCatchUpResult.NeedsFullScan(journalState: null);
        }

        if (journalState is null)
        {
            Trace.TraceWarning(
                "NTFS journal query returned no state. Root={0}; Provider={1}; A full scan will run without a completeness watermark.",
                root.Path,
                provider.Name);
            return NtfsCatchUpResult.NeedsFullScan(journalState: null);
        }

        Trace.TraceInformation(
            "NTFS journal state read. Phase=pre-scan; Root={0}; JournalId={1}; LowestValidUsn={2}; NextUsn={3}",
            root.Path,
            journalState.UsnJournalId,
            journalState.LowestValidUsn,
            journalState.NextUsn);

        var checkpoint = await ReadVolumeCheckpointAsync(root.Path, cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint is null)
        {
            Trace.TraceInformation("NTFS durable checkpoint not found. Root={0}", root.Path);
        }
        else
        {
            Trace.TraceInformation(
                "NTFS durable checkpoint read. Root={0}; JournalId={1}; NextUsn={2}; RulesVersion={3}; CreatedUtc={4:O}",
                root.Path,
                checkpoint.UsnJournalId,
                checkpoint.NextUsn,
                checkpoint.RulesVersion,
                checkpoint.LastFullScanAt);
        }

        var plan = UsnJournalCatchUpPlanner.Plan(
            checkpoint,
            journalState,
            SqliteSearchIndex.CurrentIndexContentVersion);
        Trace.TraceInformation(
            "NTFS catch-up plan. Root={0}; Action={1}; StartUsn={2}; EndUsn={3}",
            root.Path,
            plan.Action,
            plan.StartUsn,
            plan.EndUsn);
        if (plan.Action == UsnCatchUpAction.FullRescan)
        {
            return NtfsCatchUpResult.NeedsFullScan(journalState);
        }

        if (plan.Action == UsnCatchUpAction.None)
        {
            return NtfsCatchUpResult.Completed(indexedCount: 0, journalState);
        }

        UsnJournalApplyResult applyResult;
        try
        {
            var nextCheckpoint = CreateCheckpoint(root, journalState, plan.EndUsn);
            var changes = journalProvider.ReadJournalChangesAsync(
                root,
                journalState.UsnJournalId,
                plan.StartUsn,
                plan.EndUsn,
                cancellationToken);
            applyResult = await UsnJournalChangeApplier
                .ApplyAsync(
                    _sqlite,
                    changes,
                    nextCheckpoint,
                    cancellationToken,
                    recordFilter: _recordFilter,
                    nameTable: _nameTable)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsElevatedIndexerLaunchCanceled(exception))
        {
            throw new IndexProviderScanException(
                $"Provider {provider.Name} journal catch-up was canceled for {root.Path}: {exception.Message}",
                exception);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("NTFS journal catch-up failed for '{0}': {1}", root.Path, exception);
            return NtfsCatchUpResult.NeedsFullScan(journalState);
        }

        Trace.TraceInformation(
            "NTFS catch-up applied. Root={0}; AppliedCount={1}; RequiresFullRescan={2}; NextUsn={3}",
            root.Path,
            applyResult.AppliedCount,
            applyResult.RequiresFullRescan,
            applyResult.NextUsn);
        if (applyResult.RequiresFullRescan)
        {
            Trace.TraceWarning(
                "NTFS catch-up requires a full scan. Root={0}; StartUsn={1}; EndUsn={2}",
                root.Path,
                plan.StartUsn,
                plan.EndUsn);
            return NtfsCatchUpResult.NeedsFullScan(journalState);
        }

        return NtfsCatchUpResult.Completed(applyResult.AppliedCount, journalState);
    }

    private async Task SaveNtfsCheckpointAsync(
        IndexRoot root,
        UsnJournalState? journalState,
        CancellationToken cancellationToken)
    {
        if (journalState is null)
        {
            return;
        }

        var checkpoint = CreateCheckpoint(root, journalState, journalState.NextUsn);
        if (_nameTable is not null)
        {
            await _nameTable.UpdateMemoryCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _sqlite!
            .SaveVolumeCheckpointAsync(checkpoint, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<UsnJournalState?> TryApplyPostScanNtfsChangesAsync(
        IIndexProvider provider,
        IndexRoot root,
        UsnJournalState? preScanJournalState,
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        if (provider is not INtfsJournalProvider journalProvider)
        {
            Trace.TraceWarning(
                "NTFS post-scan verification skipped. Root={0}; Reason=provider does not expose journal state",
                root.Path);
            return null;
        }

        if (preScanJournalState is null)
        {
            Trace.TraceWarning(
                "NTFS post-scan verification skipped. Root={0}; Reason=pre-scan journal state unavailable",
                root.Path);
            return null;
        }

        try
        {
            var postScanJournalState = await journalProvider
                .QueryJournalStateAsync(root, cancellationToken)
                .ConfigureAwait(false);
            if (postScanJournalState is null)
            {
                Trace.TraceWarning(
                    "NTFS post-scan journal query returned no state. Root={0}",
                    root.Path);
                return null;
            }

            Trace.TraceInformation(
                "NTFS journal state read. Phase=post-scan; Root={0}; JournalId={1}; LowestValidUsn={2}; NextUsn={3}",
                root.Path,
                postScanJournalState.UsnJournalId,
                postScanJournalState.LowestValidUsn,
                postScanJournalState.NextUsn);

            if (postScanJournalState.UsnJournalId != preScanJournalState.UsnJournalId)
            {
                Trace.TraceWarning(
                    "NTFS post-scan verification failed. Root={0}; Reason=journal id changed; PreJournalId={1}; PostJournalId={2}",
                    root.Path,
                    preScanJournalState.UsnJournalId,
                    postScanJournalState.UsnJournalId);
                return null;
            }

            if (postScanJournalState.NextUsn < preScanJournalState.NextUsn)
            {
                Trace.TraceWarning(
                    "NTFS post-scan verification failed. Root={0}; Reason=journal moved backwards; PreNextUsn={1}; PostNextUsn={2}",
                    root.Path,
                    preScanJournalState.NextUsn,
                    postScanJournalState.NextUsn);
                return null;
            }

            if (postScanJournalState.NextUsn == preScanJournalState.NextUsn)
            {
                Trace.TraceInformation(
                    "NTFS post-scan verification completed without changes. Root={0}; NextUsn={1}",
                    root.Path,
                    postScanJournalState.NextUsn);
                return postScanJournalState;
            }

            var changes = journalProvider.ReadJournalChangesAsync(
                root,
                postScanJournalState.UsnJournalId,
                preScanJournalState.NextUsn,
                postScanJournalState.NextUsn,
                cancellationToken);
            var result = await UsnJournalChangeApplier.ApplyAsync(
                    _sqlite,
                    changes,
                    CreateCheckpoint(root, postScanJournalState, postScanJournalState.NextUsn),
                    cancellationToken,
                    indexGeneration,
                    _recordFilter,
                    nameTable: _nameTable)
                .ConfigureAwait(false);
            Trace.TraceInformation(
                "NTFS post-scan catch-up applied. Root={0}; AppliedCount={1}; RequiresFullRescan={2}; NextUsn={3}",
                root.Path,
                result.AppliedCount,
                result.RequiresFullRescan,
                result.NextUsn);
            if (result.RequiresFullRescan)
            {
                Trace.TraceWarning(
                    "NTFS post-scan verification could not prove completeness. Root={0}; Reason=journal changes require another full scan",
                    root.Path);
                return null;
            }

            return postScanJournalState;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("NTFS post-scan journal catch-up failed for '{0}': {1}", root.Path, exception);
            return null;
        }
    }

    private static UsnJournalCheckpoint CreateCheckpoint(
        IndexRoot root,
        UsnJournalState journalState,
        long nextUsn)
    {
        return new UsnJournalCheckpoint(
            root.Path,
            NtfsIndexProvider.ProviderName,
            journalState.UsnJournalId,
            nextUsn,
            SqliteSearchIndex.CurrentIndexContentVersion,
            DateTimeOffset.UtcNow);
    }

    private async Task<int> ScanAndUpsertAsync(
        IIndexProvider provider,
        IndexRoot root,
        int currentIndexedCount,
        long indexGeneration,
        int currentRootNumber,
        int totalRoots,
        CancellationToken cancellationToken)
    {
        var indexedCount = 0;
        var lastReportedCount = -1;
        var nextProgressReportAt = 0L;
        var flushedBatchCount = 0;
        var batch = new List<FileRecord>(_batchSize);
        IAsyncEnumerator<FileRecord> enumerator;

        try
        {
            enumerator = provider.ScanAsync(root, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new IndexProviderScanException($"Provider {provider.Name} failed to start scanning {root.Path}: {exception.Message}", exception);
        }

        try
        {
            while (true)
            {
                FileRecord record;

                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    record = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new IndexProviderScanException(
                        $"Provider {provider.Name} failed while scanning {root.Path}: {exception.Message}",
                        exception,
                        partialRecordsAccepted: indexedCount > 0 || batch.Count > 0);
                }

                if (!_recordFilter(record))
                {
                    continue;
                }

                batch.Add(record);

                if (batch.Count == _batchSize)
                {
                    var reportedWriting = ReportWritingIfDue(force: false);
                    indexedCount += await FlushBatchAsync(batch, indexGeneration, cancellationToken).ConfigureAwait(false);
                    ReportScanningIfDue(force: reportedWriting);
                    flushedBatchCount++;
                    if (flushedBatchCount % BatchesBetweenYield == 0)
                    {
                        // Brief pause so interactive threads can run during multi-million-file scans.
                        await Task.Delay(BatchYieldDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (batch.Count > 0)
        {
            ReportWritingIfDue(force: true);
        }

        indexedCount += await FlushBatchAsync(batch, indexGeneration, cancellationToken).ConfigureAwait(false);
        ReportScanningIfDue(force: true);
        return indexedCount;

        bool ReportWritingIfDue(bool force)
        {
            if (!force && !IsProgressReportDue())
            {
                return false;
            }

            RaiseStatus(
                IndexingRunState.Indexing,
                $"Writing {batch.Count:N0} indexed items...",
                currentIndexedCount + indexedCount,
                root.Path,
                currentRootNumber,
                totalRoots);
            return true;
        }

        void ReportScanningIfDue(bool force)
        {
            if (indexedCount == lastReportedCount)
            {
                return;
            }

            if (!force && !IsProgressReportDue())
            {
                return;
            }

            lastReportedCount = indexedCount;
            nextProgressReportAt = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.2);
            RaiseStatus(
                IndexingRunState.Indexing,
                $"Scanning with {provider.Name}...",
                currentIndexedCount + indexedCount,
                root.Path,
                currentRootNumber,
                totalRoots);
        }

        bool IsProgressReportDue()
        {
            return nextProgressReportAt == 0 || Stopwatch.GetTimestamp() >= nextProgressReportAt;
        }
    }

    private async Task<int> FlushBatchAsync(
        List<FileRecord> batch,
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        var count = batch.Count;
        var metricName = _nameTable is not null ? "indexing.nametable_batch" : "indexing.sqlite_batch";
        using var metrics = PerformanceMetrics.Begin(metricName);
        PerformanceMetrics.SetCounter("batch_size", count);
        PerformanceMetrics.SetCounter("index_generation", indexGeneration);
        try
        {
            if (_nameTable is not null)
            {
                using (PerformanceMetrics.MeasureStage("nametable.upsert"))
                {
                    await _nameTable.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                using (PerformanceMetrics.MeasureStage("sqlite.upsert"))
                {
                    await _sqlite!.UpsertManyAsync(batch, indexGeneration, cancellationToken).ConfigureAwait(false);
                }
            }

            metrics.Complete("success", count);
            batch.Clear();
            return count;
        }
        catch (OperationCanceledException)
        {
            metrics.Complete("canceled");
            throw;
        }
        catch
        {
            metrics.Complete("failed");
            throw;
        }
    }

    private void RaiseStatus(
        IndexingRunState state,
        string message,
        int indexedCount,
        string? currentRoot = null,
        int currentRootNumber = 0,
        int totalRoots = 0)
    {
        Trace.TraceInformation(
            "Indexing status. State={0}; IndexedCount={1}; CurrentRoot={2}; RootNumber={3}; TotalRoots={4}; Message={5}",
            state,
            indexedCount,
            currentRoot ?? "<none>",
            currentRootNumber,
            totalRoots,
            message);
        StatusChanged?.Invoke(
            this,
            new IndexingStatus(state, message, indexedCount, currentRoot, currentRootNumber, totalRoots));
    }

    /// <summary>NameTable scans build an isolated root epoch; SQLite uses generation pruning.</summary>
    private Task PrepareFullScanRootAsync(IndexRoot root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_nameTable is not null)
        {
            return _nameTable.BeginFullBuildRootAsync(root.Path, cancellationToken);
        }

        return Task.CompletedTask;
    }

    private Task CommitFullScanRootAsync(IndexRoot root, CancellationToken cancellationToken) =>
        _nameTable is null
            ? Task.CompletedTask
            : _nameTable.CommitFullBuildRootAsync(root.Path, cancellationToken);

    private Task CommitFullScanRootRetainingStaleAsync(
        IndexRoot root,
        CancellationToken cancellationToken) =>
        _nameTable is null
            ? Task.CompletedTask
            : _nameTable.CommitFullBuildRootRetainingStaleAsync(root.Path, cancellationToken);

    private async Task PruneStaleUnderRootAsync(
        string rootPath,
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        if (_sqlite is null)
        {
            // NameTable publishes the complete staged root atomically after this step.
            return;
        }

        await _sqlite.PruneStaleRecordsUnderRootAsync(rootPath, indexGeneration, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<UsnJournalCheckpoint?> ReadVolumeCheckpointAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        if (_nameTable is not null)
        {
            if (!_nameTable.Engine.TryGetDurableCheckpoint(rootPath, out var checkpoint)
                || checkpoint is null
                || checkpoint.NextUsn <= 0)
            {
                return null;
            }

            return checkpoint;
        }

        return await _sqlite!.ReadVolumeCheckpointAsync(rootPath, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsNtfsProvider(IIndexProvider provider)
    {
        return string.Equals(provider.Name, NtfsIndexProvider.ProviderName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsElevatedIndexerLaunchCanceled(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ElevatedIndexerLaunchCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    internal static VolumeInfo ResolveVolume(IndexRoot root)
    {
        var volumeRoot = Path.GetPathRoot(root.Path) ?? root.Path;

        if (IsUncRoot(volumeRoot))
        {
            return new VolumeInfo(volumeRoot, "Network", Directory.Exists(root.Path), DriveType.Network);
        }

        volumeRoot = NormalizeDriveRoot(volumeRoot);
        var drive = new DriveInfo(volumeRoot);
        return new VolumeInfo(volumeRoot, drive.DriveFormat, drive.IsReady, drive.DriveType);
    }

    private static bool IsUncRoot(string path)
    {
        return IsStandardUncRoot(path) || IsExtendedUncRoot(path);
    }

    private static bool IsStandardUncRoot(string path)
    {
        return path.StartsWith(@"\\", StringComparison.Ordinal)
            && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !path.StartsWith(@"\\.\", StringComparison.Ordinal);
    }

    private static bool IsExtendedUncRoot(string path)
    {
        return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDriveRoot(string path)
    {
        const string extendedPathPrefix = @"\\?\";

        if (path.StartsWith(extendedPathPrefix, StringComparison.Ordinal)
            && path.Length >= extendedPathPrefix.Length + 3
            && path[extendedPathPrefix.Length + 1] == ':')
        {
            return path[extendedPathPrefix.Length..];
        }

        return path;
    }

    private sealed class IndexProviderScanException : Exception
    {
        public IndexProviderScanException(
            string message,
            Exception innerException,
            bool partialRecordsAccepted = false)
            : base(message, innerException)
        {
            PartialRecordsAccepted = partialRecordsAccepted;
        }

        public bool PartialRecordsAccepted { get; }
    }

    private sealed record IndexRootResult(
        int IndexedCount,
        bool HadFailure,
        bool WasCanceled,
        bool RetainedStaleRecords = false,
        bool UsedCompatibilityScanning = false);

    private static string CreateFailureSummary(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
        {
            return "Indexing completed with errors.";
        }

        var suffix = failures.Count == 1 ? string.Empty : $" (+{failures.Count - 1} more)";
        return $"Indexing completed with errors: {failures[0]}{suffix}";
    }

    private sealed record NtfsCatchUpResult(
        bool CompletedWithoutFullScan,
        int IndexedCount,
        UsnJournalState? JournalState)
    {
        public static NtfsCatchUpResult Completed(int indexedCount, UsnJournalState journalState)
            => new(CompletedWithoutFullScan: true, indexedCount, journalState);

        public static NtfsCatchUpResult NeedsFullScan(UsnJournalState? journalState)
            => new(CompletedWithoutFullScan: false, IndexedCount: 0, journalState);
    }
}
