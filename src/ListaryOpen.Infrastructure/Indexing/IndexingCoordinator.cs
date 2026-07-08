using System.Diagnostics;
using System.IO;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Indexing;

public enum IndexingRunState
{
    Idle,
    Indexing,
    Completed,
    Canceled,
    Failed
}

public sealed record IndexingStatus(IndexingRunState State, string Message, int IndexedCount);

public sealed class IndexingCoordinator
{
    private const int DefaultBatchSize = 500;

    private readonly SqliteSearchIndex _index;
    private readonly VolumeIndexer _volumeIndexer;
    private readonly IIndexProvider _fallbackProvider;
    private readonly Func<IndexRoot, VolumeInfo> _volumeResolver;
    private readonly int _batchSize;

    public IndexingCoordinator(
        SqliteSearchIndex index,
        VolumeIndexer volumeIndexer,
        IIndexProvider fallbackProvider,
        Func<IndexRoot, VolumeInfo>? volumeResolver = null,
        int batchSize = DefaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(volumeIndexer);
        ArgumentNullException.ThrowIfNull(fallbackProvider);

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be positive.");
        }

        _index = index;
        _volumeIndexer = volumeIndexer;
        _fallbackProvider = fallbackProvider;
        _volumeResolver = volumeResolver ?? ResolveVolume;
        _batchSize = batchSize;
    }

    public event EventHandler<IndexingStatus>? StatusChanged;

    public async Task IndexRootsAsync(IReadOnlyList<IndexRoot> roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var indexedCount = 0;
        var hadFailures = false;
        var hadCancellations = false;
        long? indexGeneration = null;
        RaiseStatus(IndexingRunState.Indexing, "Indexing started.", indexedCount);

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(root.Path))
            {
                hadFailures = true;
                RaiseStatus(IndexingRunState.Failed, $"Root missing: {root.Path}", indexedCount);
                continue;
            }

            try
            {
                indexGeneration ??= await _index.BeginIndexingRunAsync(cancellationToken).ConfigureAwait(false);
                var provider = SelectProvider(root);
                var result = await IndexRootWithProviderAsync(
                    provider,
                    root,
                    indexedCount,
                    indexGeneration.Value,
                    cancellationToken).ConfigureAwait(false);
                indexedCount += result.IndexedCount;
                hadFailures |= result.HadFailure;
                hadCancellations |= result.WasCanceled;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                hadFailures = true;
                RaiseStatus(IndexingRunState.Failed, $"Indexing failed for {root.Path}: {exception.Message}", indexedCount);
            }
        }

        var finalState = hadFailures
            ? IndexingRunState.Failed
            : hadCancellations
                ? IndexingRunState.Canceled
                : IndexingRunState.Completed;
        var finalMessage = hadFailures
            ? "Indexing completed with errors."
            : hadCancellations
                ? "Indexing canceled."
                : "Indexing completed.";

        RaiseStatus(
            finalState,
            finalMessage,
            indexedCount);
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
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        if (!IsNtfsProvider(provider))
        {
            var count = await ScanAndUpsertAsync(provider, root, indexGeneration, cancellationToken).ConfigureAwait(false);
            await _index.PruneStaleRecordsUnderRootAsync(root.Path, indexGeneration, cancellationToken).ConfigureAwait(false);
            return new IndexRootResult(count, HadFailure: false, WasCanceled: false);
        }

        try
        {
            var catchUp = await TryCatchUpNtfsRootAsync(
                provider,
                root,
                indexGeneration,
                cancellationToken).ConfigureAwait(false);
            if (catchUp.CompletedWithoutFullScan)
            {
                return new IndexRootResult(catchUp.IndexedCount, HadFailure: false, WasCanceled: false);
            }

            var count = await ScanAndUpsertAsync(provider, root, indexGeneration, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                await _index.PruneStaleRecordsUnderRootAsync(root.Path, indexGeneration, cancellationToken).ConfigureAwait(false);
                await SaveNtfsCheckpointAsync(provider, root, catchUp.JournalState, cancellationToken).ConfigureAwait(false);
                return new IndexRootResult(count, HadFailure: false, WasCanceled: false);
            }

            RaiseStatus(IndexingRunState.Indexing, $"NTFS returned no records; using fallback for {root.Path}.", currentIndexedCount);
            var fallbackCount = await ScanAndUpsertAsync(_fallbackProvider, root, indexGeneration, cancellationToken).ConfigureAwait(false);
            await _index.PruneStaleRecordsUnderRootAsync(root.Path, indexGeneration, cancellationToken).ConfigureAwait(false);
            return new IndexRootResult(fallbackCount, HadFailure: false, WasCanceled: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IndexProviderScanException exception)
        {
            if (IsElevatedIndexerLaunchCanceled(exception))
            {
                RaiseStatus(IndexingRunState.Canceled, $"NTFS scan canceled for {root.Path}.", currentIndexedCount);
                return new IndexRootResult(0, HadFailure: false, WasCanceled: true);
            }

            if (exception.PartialRecordsAccepted)
            {
                RaiseStatus(
                    IndexingRunState.Failed,
                    $"NTFS scan failed after partial output for {root.Path}; preserving existing index. {exception.Message}",
                    currentIndexedCount);
                return new IndexRootResult(0, HadFailure: true, WasCanceled: false);
            }

            RaiseStatus(IndexingRunState.Failed, $"NTFS scan failed for {root.Path}; using fallback. {exception.Message}", currentIndexedCount);
            var fallbackCount = await ScanAndUpsertAsync(_fallbackProvider, root, indexGeneration, cancellationToken).ConfigureAwait(false);
            await _index.PruneStaleRecordsUnderRootAsync(root.Path, indexGeneration, cancellationToken).ConfigureAwait(false);
            return new IndexRootResult(fallbackCount, HadFailure: true, WasCanceled: false);
        }
    }

    private async Task<NtfsCatchUpResult> TryCatchUpNtfsRootAsync(
        IIndexProvider provider,
        IndexRoot root,
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        if (provider is not INtfsJournalProvider journalProvider)
        {
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
            Trace.TraceWarning("NTFS journal query failed for '{0}': {1}", root.Path, exception.Message);
            return NtfsCatchUpResult.NeedsFullScan(journalState: null);
        }

        if (journalState is null)
        {
            return NtfsCatchUpResult.NeedsFullScan(journalState: null);
        }

        var checkpoint = await _index
            .ReadVolumeCheckpointAsync(root.Path, cancellationToken)
            .ConfigureAwait(false);
        var plan = UsnJournalCatchUpPlanner.Plan(
            checkpoint,
            journalState,
            SqliteSearchIndex.CurrentIndexContentVersion);
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
            var changes = journalProvider.ReadJournalChangesAsync(root, plan.StartUsn, plan.EndUsn, cancellationToken);
            applyResult = await UsnJournalChangeApplier
                .ApplyAsync(_index, changes, nextCheckpoint, cancellationToken)
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
            Trace.TraceWarning("NTFS journal catch-up failed for '{0}': {1}", root.Path, exception.Message);
            return NtfsCatchUpResult.NeedsFullScan(journalState);
        }

        if (applyResult.RequiresFullRescan)
        {
            return NtfsCatchUpResult.NeedsFullScan(journalState);
        }

        return NtfsCatchUpResult.Completed(applyResult.AppliedCount, journalState);
    }

    private async Task SaveNtfsCheckpointAsync(
        IIndexProvider provider,
        IndexRoot root,
        UsnJournalState? journalState,
        CancellationToken cancellationToken)
    {
        if (provider is not INtfsJournalProvider journalProvider)
        {
            return;
        }

        journalState ??= await journalProvider
            .QueryJournalStateAsync(root, cancellationToken)
            .ConfigureAwait(false);
        if (journalState is null)
        {
            return;
        }

        await _index
            .SaveVolumeCheckpointAsync(CreateCheckpoint(root, journalState, journalState.NextUsn), cancellationToken)
            .ConfigureAwait(false);
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
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        var indexedCount = 0;
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

                batch.Add(record);

                if (batch.Count == _batchSize)
                {
                    indexedCount += await FlushBatchAsync(batch, indexGeneration, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        indexedCount += await FlushBatchAsync(batch, indexGeneration, cancellationToken).ConfigureAwait(false);
        return indexedCount;
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

        await _index.UpsertManyAsync(batch, indexGeneration, cancellationToken).ConfigureAwait(false);
        var count = batch.Count;
        batch.Clear();
        return count;
    }

    private void RaiseStatus(IndexingRunState state, string message, int indexedCount)
    {
        StatusChanged?.Invoke(this, new IndexingStatus(state, message, indexedCount));
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

    private sealed record IndexRootResult(int IndexedCount, bool HadFailure, bool WasCanceled);

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
