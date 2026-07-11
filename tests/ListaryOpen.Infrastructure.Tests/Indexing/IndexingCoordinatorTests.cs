using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class IndexingCoordinatorTests
{
    [Fact]
    public async Task IndexRootsAsyncFallsBackFromEmptyNtfsScanAndUpsertsFilesAndFolders()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            Directory.CreateDirectory(Path.Combine(rootPath, "CoordinatorFolder"));
            await File.WriteAllTextAsync(Path.Combine(rootPath, "CoordinatorFile.txt"), "indexed");

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new EmptyNtfsProvider() }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, NtfsIndexProvider.ProviderName, true),
                batchSize: 1);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var fileResults = await index.SearchAsync(new SearchQuery("CoordinatorFile", SearchMode.FilesAndFolders), CancellationToken.None);
            var folderResults = await index.SearchAsync(new SearchQuery("CoordinatorFolder", SearchMode.FilesAndFolders), CancellationToken.None);

            Assert.Equal("CoordinatorFile.txt", Assert.Single(fileResults).Record.Name);
            Assert.Equal("CoordinatorFolder", Assert.Single(folderResults).Record.Name);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncReportsFallbackWhenNtfsProviderYieldsNoRecords()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "FallbackKnownRecord.txt"), "indexed");

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new EmptyNtfsProvider() }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, NtfsIndexProvider.ProviderName, true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var results = await index.SearchAsync(new SearchQuery("FallbackKnownRecord", SearchMode.FilesAndFolders), CancellationToken.None);

            Assert.Equal("FallbackKnownRecord.txt", Assert.Single(results).Record.Name);
            Assert.Contains(statuses, status => status.Message.Contains("fallback", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncKeepsFinalFailedStatusWhenNtfsScanFailsAndFallbackSucceeds()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "FallbackAfterFailure.txt"), "indexed");

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new ThrowingNtfsProvider() }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, NtfsIndexProvider.ProviderName, true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var results = await index.SearchAsync(new SearchQuery("FallbackAfterFailure", SearchMode.FilesAndFolders), CancellationToken.None);

            Assert.Equal("FallbackAfterFailure.txt", Assert.Single(results).Record.Name);
            Assert.Contains(statuses, status => status.State == IndexingRunState.Failed && status.Message.Contains("fallback", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Failed, statuses[^1].State);
            Assert.Contains("errors", statuses[^1].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncKeepsFinalFailedStatusWhenElevatedHelperFailsAndFallbackSucceeds()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "FallbackAfterHelperFailure.txt"), "indexed");
            var helperPath = CreateUsableHelperBundle(rootPath);

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) => new CompletedElevatedIndexerProcess(stdout: string.Empty, stderr: "Access denied.", exitCode: 5));
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new NtfsIndexProvider(client) }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, NtfsIndexProvider.ProviderName, true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var results = await index.SearchAsync(new SearchQuery("FallbackAfterHelperFailure", SearchMode.FilesAndFolders), CancellationToken.None);

            Assert.Equal("FallbackAfterHelperFailure.txt", Assert.Single(results).Record.Name);
            Assert.Contains(statuses, status => status.State == IndexingRunState.Failed && status.Message.Contains("helper", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Failed, statuses[^1].State);
            Assert.Contains("errors", statuses[^1].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncUsesFallbackWithoutFailedStatusWhenElevatedClientIsUnavailable()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "FallbackWhenElevatedUnavailable.txt"), "indexed");
            var helperPath = CreateUsableHelperBundle(rootPath);
            var helperProcessCreated = false;

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) =>
                {
                    helperProcessCreated = true;
                    throw new InvalidOperationException("Helper process should not be created.");
                },
                () => false);
            var fallbackProvider = new FallbackIndexProvider();
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new NtfsIndexProvider(client), fallbackProvider }),
                fallbackProvider,
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, NtfsIndexProvider.ProviderName, true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var results = await index.SearchAsync(new SearchQuery("FallbackWhenElevatedUnavailable", SearchMode.FilesAndFolders), CancellationToken.None);

            Assert.Equal("FallbackWhenElevatedUnavailable.txt", Assert.Single(results).Record.Name);
            Assert.False(helperProcessCreated);
            Assert.DoesNotContain(statuses, status => status.State == IndexingRunState.Failed);
            Assert.DoesNotContain(statuses, status => status.Message.Contains("NTFS scan failed", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Completed, statuses[^1].State);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotUseFallbackWhenUacElevationIsCanceled()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "ShouldNotFallbackAfterUacCancel.txt"), "indexed");
            var helperPath = CreateUsableHelperBundle(rootPath);

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var fallbackProvider = new CountingFallbackProvider();
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) => throw new InvalidOperationException("Redirected helper should not be created."),
                () => false,
                () => true,
                (_, _, _, _) => new StartThrowingElevatedIndexerProcess(
                    new Win32Exception(1223, "The operation was canceled by the user.")),
                Path.Combine(rootPath, "data", "tmp"));
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new NtfsIndexProvider(client), fallbackProvider }),
                fallbackProvider,
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, NtfsIndexProvider.ProviderName, true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            Assert.Equal(0, fallbackProvider.ScanCount);
            Assert.Contains(statuses, status =>
                status.State == IndexingRunState.Canceled
                && status.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Canceled, statuses[^1].State);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotUseFallbackWhenUacElevationStartReturnsFalse()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "ShouldNotFallbackAfterUacStartFalse.txt"), "indexed");
            var helperPath = CreateUsableHelperBundle(rootPath);

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var fallbackProvider = new CountingFallbackProvider();
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) => throw new InvalidOperationException("Redirected helper should not be created."),
                () => false,
                () => true,
                (_, _, _, _) => new StartFalseElevatedIndexerProcess(),
                Path.Combine(rootPath, "data", "tmp"));
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new NtfsIndexProvider(client), fallbackProvider }),
                fallbackProvider,
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, NtfsIndexProvider.ProviderName, true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            Assert.Equal(0, fallbackProvider.ScanCount);
            Assert.Contains(statuses, status =>
                status.State == IndexingRunState.Canceled
                && status.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Canceled, statuses[^1].State);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncRemovesRecordsNotSeenInSuccessfulRootScan()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var currentRecord = FileRecord.Create(
                Path.Combine(rootPath, "CurrentRecord.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            var staleRecord = FileRecord.Create(
                Path.Combine(rootPath, "StaleRecord.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.UpsertManyAsync(new[] { currentRecord, staleRecord }, CancellationToken.None);

            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new AsynchronousSingleRecordProvider(currentRecord) }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, "AsyncProvider", true),
                batchSize: 1);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var currentResults = await index.SearchAsync(new SearchQuery("CurrentRecord", SearchMode.FilesAndFolders), CancellationToken.None);
            var staleResults = await index.SearchAsync(new SearchQuery("StaleRecord", SearchMode.FilesAndFolders), CancellationToken.None);

            Assert.Contains(currentResults, result => result.Record.PathKey == currentRecord.PathKey);
            Assert.DoesNotContain(staleResults, result => result.Record.PathKey == staleRecord.PathKey);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotPruneWhenProviderFailsAfterPartialOutput()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var currentRecord = FileRecord.Create(
                Path.Combine(rootPath, "CurrentRecord.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            var staleRecord = FileRecord.Create(
                Path.Combine(rootPath, "KeepBecauseScanIncomplete.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.UpsertManyAsync(new[] { currentRecord, staleRecord }, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new PartialThenThrowingProvider(currentRecord) }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, NtfsIndexProvider.ProviderName, true),
                batchSize: 1);
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var currentResults = await index.SearchAsync(new SearchQuery("CurrentRecord", SearchMode.FilesAndFolders), CancellationToken.None);
            var staleResults = await index.SearchAsync(new SearchQuery("KeepBecauseScanIncomplete", SearchMode.FilesAndFolders), CancellationToken.None);

            Assert.Contains(currentResults, result => result.Record.PathKey == currentRecord.PathKey);
            Assert.Contains(staleResults, result => result.Record.PathKey == staleRecord.PathKey);
            Assert.Equal(IndexingRunState.Failed, statuses[^1].State);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncReportsFailedStatusForMissingRootAndContinues()
    {
        var existingRootPath = CreateTempDirectory();
        var missingRootPath = Path.Combine(Path.GetTempPath(), "listary-open-missing-" + Guid.NewGuid());
        var dbPath = CreateTempDbPath();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(existingRootPath, "StillIndexed.txt"), "indexed");

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new EmptyNtfsProvider() }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(existingRootPath)!, NtfsIndexProvider.ProviderName, true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(
                new[] { new IndexRoot(missingRootPath), new IndexRoot(existingRootPath) },
                CancellationToken.None);

            var results = await index.SearchAsync(new SearchQuery("StillIndexed", SearchMode.FilesAndFolders), CancellationToken.None);

            Assert.Equal("StillIndexed.txt", Assert.Single(results).Record.Name);
            Assert.Contains(statuses, status => status.State == IndexingRunState.Failed && status.Message.Contains("missing", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Failed, statuses[^1].State);
            Assert.Contains("errors", statuses[^1].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfExists(existingRootPath);
            DeleteDirectoryIfExists(missingRootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncReportsFailedStatusWhenProviderSelectionFails()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(Array.Empty<IIndexProvider>()),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, "UNKNOWN", true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            var exception = await Record.ExceptionAsync(() =>
                coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None));

            Assert.Null(exception);
            Assert.Contains(statuses, status => status.State == IndexingRunState.Failed && status.Message.Contains("provider", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Failed, statuses[^1].State);
            Assert.Contains("errors", statuses[^1].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncKeepsCumulativeCountWhenReportingFallback()
    {
        var firstRootPath = CreateTempDirectory();
        var secondRootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(firstRootPath, "FirstIndexed.txt"), "indexed");

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var statuses = new List<IndexingStatus>();
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { new EmptyNtfsProvider() }),
                new FallbackIndexProvider(),
                root => new VolumeInfo(Path.GetPathRoot(root.Path)!, NtfsIndexProvider.ProviderName, true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(
                new[] { new IndexRoot(firstRootPath), new IndexRoot(secondRootPath) },
                CancellationToken.None);

            var secondFallbackStatus = statuses.Single(status =>
                status.Message.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                && status.Message.Contains(secondRootPath, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(1, secondFallbackStatus.IndexedCount);
        }
        finally
        {
            DeleteDirectoryIfExists(firstRootPath);
            DeleteDirectoryIfExists(secondRootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotUseFallbackWhenNtfsUpsertFails()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "FallbackWouldSeeThis.txt"), "indexed");

            var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.DisposeAsync();
            var statuses = new List<IndexingStatus>();
            var provider = new SingleRecordNtfsProvider(FileRecord.Create(
                Path.Combine(rootPath, "PrimaryRecord.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow));
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, NtfsIndexProvider.ProviderName, true));
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            var exception = await Record.ExceptionAsync(() =>
                coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None));

            Assert.Null(exception);
            Assert.DoesNotContain(statuses, status => status.Message.Contains("fallback", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Failed, statuses[^1].State);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotPostContinuationToCallingSynchronizationContext()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var provider = new AsynchronousSingleRecordProvider(FileRecord.Create(
                Path.Combine(rootPath, "ContextFreeIndex.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow));
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, provider.Name, true),
                batchSize: 1);

            var previousContext = SynchronizationContext.Current;
            var context = new RecordingSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            Task indexingTask;
            try
            {
                indexingTask = coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }

            await indexingTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Theory]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\server\share\folder", false)]
    public void ResolveVolumeClassifiesUncRootsAsNonNtfsWithoutThrowing(string path, bool expectedReady)
    {
        var volume = IndexingCoordinator.ResolveVolume(new IndexRoot(path));

        Assert.StartsWith(@"\\server\share", volume.RootPath, StringComparison.Ordinal);
        Assert.NotEqual(NtfsIndexProvider.ProviderName, volume.FileSystemName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expectedReady, volume.IsReady);
    }

    [Fact]
    public void ResolveVolumeClassifiesExtendedUncRootsAsNonNtfsWithoutThrowing()
    {
        var path = @"\\?\UNC\server\share\folder";

        var volume = IndexingCoordinator.ResolveVolume(new IndexRoot(path));

        Assert.StartsWith(@"\\?\UNC\server\share", volume.RootPath, StringComparison.Ordinal);
        Assert.NotEqual(NtfsIndexProvider.ProviderName, volume.FileSystemName, StringComparer.OrdinalIgnoreCase);
        Assert.False(volume.IsReady);
    }

    [Fact]
    public void ResolveVolumeDoesNotClassifyExtendedLocalDriveRootsAsNetwork()
    {
        var driveRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var path = @"\\?\" + Path.Combine(driveRoot, "listary-open-missing-" + Guid.NewGuid());

        var volume = IndexingCoordinator.ResolveVolume(new IndexRoot(path));

        Assert.Equal(driveRoot, volume.RootPath);
        Assert.NotEqual("Network", volume.FileSystemName, StringComparer.OrdinalIgnoreCase);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateTempDbPath()
    {
        return Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncSkipsNtfsFullScanWhenCheckpointAlreadyReachedJournalTip()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.SaveVolumeCheckpointAsync(
                new UsnJournalCheckpoint(
                    rootPath,
                    NtfsIndexProvider.ProviderName,
                    9,
                    200,
                    SqliteSearchIndex.CurrentIndexContentVersion,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(
                new UsnJournalState(9, LowestValidUsn: 100, NextUsn: 200));
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            Assert.Equal(0, provider.ScanCount);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncSavesNtfsCheckpointAfterSuccessfulFullScan()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var record = FileRecord.Create(
                Path.Combine(rootPath, "IndexedAfterFullScan.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(
                new UsnJournalState(9, LowestValidUsn: 100, NextUsn: 200),
                records: new[] { record });
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var checkpoint = await index.ReadVolumeCheckpointAsync(rootPath, CancellationToken.None);
            Assert.NotNull(checkpoint);
            Assert.Equal(9ul, checkpoint!.UsnJournalId);
            Assert.Equal(200, checkpoint.NextUsn);
            Assert.Equal(SqliteSearchIndex.CurrentIndexContentVersion, checkpoint.RulesVersion);
            Assert.Equal(1, provider.ScanCount);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncCatchesUpChangesWrittenDuringNtfsFullScanBeforePruning()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var scannedRecord = FileRecord.Create(
                Path.Combine(rootPath, "ScannedBeforePostWatermark.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            var changedRecord = FileRecord.Create(
                Path.Combine(rootPath, "ChangedDuringFullScan.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(
                journalStates: new UsnJournalState?[]
                {
                    new(9, LowestValidUsn: 100, NextUsn: 200),
                    new(9, LowestValidUsn: 100, NextUsn: 205)
                },
                records: new[] { scannedRecord },
                changes: new[] { UsnJournalChange.Upsert(changedRecord) });
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var checkpoint = await index.ReadVolumeCheckpointAsync(rootPath, CancellationToken.None);
            var results = await index.SearchAsync(
                new SearchQuery("ChangedDuringFullScan", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Equal(205, checkpoint!.NextUsn);
            Assert.Contains(results, result => result.Record.PathKey == changedRecord.PathKey);
            Assert.Equal(2, provider.QueryJournalStateCount);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotPruneOrSaveNtfsCheckpointAfterFullScanWithoutPreScanJournalState()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var record = FileRecord.Create(
                Path.Combine(rootPath, "IndexedWithoutJournalState.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            var staleRecord = FileRecord.Create(
                Path.Combine(rootPath, "PreservedWithoutJournalState.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.UpsertAsync(staleRecord, CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(
                journalStates: new UsnJournalState?[]
                {
                    null,
                    new(9, LowestValidUsn: 100, NextUsn: 200)
                },
                records: new[] { record });
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));
            var statuses = new List<IndexingStatus>();
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var checkpoint = await index.ReadVolumeCheckpointAsync(rootPath, CancellationToken.None);
            var staleResults = await index.SearchAsync(
                new SearchQuery("PreservedWithoutJournalState", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Null(checkpoint);
            Assert.Contains(staleResults, result => result.Record.PathKey == staleRecord.PathKey);
            Assert.Equal(1, provider.QueryJournalStateCount);
            Assert.Contains(statuses, status =>
                status.Message.Contains("prune", StringComparison.OrdinalIgnoreCase)
                && status.Message.Contains("completeness", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Completed, statuses[^1].State);
            Assert.Contains("stale records retained", statuses[^1].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotPruneAfterJournalQueryFailure()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var record = FileRecord.Create(
                Path.Combine(rootPath, "IndexedAfterJournalQueryFailure.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            var staleRecord = FileRecord.Create(
                Path.Combine(rootPath, "PreservedAfterJournalQueryFailure.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.UpsertAsync(staleRecord, CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(
                journalState: null,
                records: new[] { record },
                journalQueryException: new IOException("Journal query failed."));
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var staleResults = await index.SearchAsync(
                new SearchQuery("PreservedAfterJournalQueryFailure", SearchMode.FilesAndFolders),
                CancellationToken.None);
            var checkpoint = await index.ReadVolumeCheckpointAsync(rootPath, CancellationToken.None);
            Assert.Contains(staleResults, result => result.Record.PathKey == staleRecord.PathKey);
            Assert.Null(checkpoint);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotPruneFallbackAfterEmptyNtfsScanWithoutPreScanJournalState()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var fallbackRecord = FileRecord.Create(
                Path.Combine(rootPath, "IndexedByFallback.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            var staleRecord = FileRecord.Create(
                Path.Combine(rootPath, "PreservedAfterFallback.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.UpsertAsync(staleRecord, CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(journalState: null);
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new AsynchronousSingleRecordProvider(fallbackRecord),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));
            var statuses = new List<IndexingStatus>();
            coordinator.StatusChanged += (_, status) => statuses.Add(status);

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var fallbackResults = await index.SearchAsync(
                new SearchQuery("IndexedByFallback", SearchMode.FilesAndFolders),
                CancellationToken.None);
            var staleResults = await index.SearchAsync(
                new SearchQuery("PreservedAfterFallback", SearchMode.FilesAndFolders),
                CancellationToken.None);
            var checkpoint = await index.ReadVolumeCheckpointAsync(rootPath, CancellationToken.None);
            Assert.Contains(fallbackResults, result => result.Record.PathKey == fallbackRecord.PathKey);
            Assert.Contains(staleResults, result => result.Record.PathKey == staleRecord.PathKey);
            Assert.Null(checkpoint);
            Assert.Equal(IndexingRunState.Completed, statuses[^1].State);
            Assert.Contains("stale records retained", statuses[^1].Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotPruneFallbackAfterEmptyNtfsScanWithPreScanJournalState()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var fallbackRecord = FileRecord.Create(
                Path.Combine(rootPath, "IndexedByProvenanceLimitedFallback.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            var staleRecord = FileRecord.Create(
                Path.Combine(rootPath, "PreservedAfterProvenanceLimitedFallback.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.UpsertAsync(staleRecord, CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(new UsnJournalState(9, 100, 200));
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new AsynchronousSingleRecordProvider(fallbackRecord),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var staleResults = await index.SearchAsync(
                new SearchQuery("PreservedAfterProvenanceLimitedFallback", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Contains(staleResults, result => result.Record.PathKey == staleRecord.PathKey);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncDoesNotPruneFallbackAfterNtfsFailureWithoutPreScanJournalState()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var fallbackRecord = FileRecord.Create(
                Path.Combine(rootPath, "IndexedAfterNtfsFailure.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            var staleRecord = FileRecord.Create(
                Path.Combine(rootPath, "PreservedAfterNtfsFailure.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.UpsertAsync(staleRecord, CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(
                journalState: null,
                scanException: new IOException("NTFS scan failed."));
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new AsynchronousSingleRecordProvider(fallbackRecord),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var staleResults = await index.SearchAsync(
                new SearchQuery("PreservedAfterNtfsFailure", SearchMode.FilesAndFolders),
                CancellationToken.None);
            var checkpoint = await index.ReadVolumeCheckpointAsync(rootPath, CancellationToken.None);
            Assert.Contains(staleResults, result => result.Record.PathKey == staleRecord.PathKey);
            Assert.Null(checkpoint);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncAppliesNtfsJournalChangesWhenCheckpointCanCatchUp()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var oldRecord = FileRecord.Create(
                Path.Combine(rootPath, "OldName.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);
            var newRecord = FileRecord.Create(
                Path.Combine(rootPath, "NewName.txt"),
                isDirectory: false,
                sizeBytes: 2,
                DateTimeOffset.UtcNow);

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.UpsertAsync(oldRecord, CancellationToken.None);
            await index.SaveVolumeCheckpointAsync(
                new UsnJournalCheckpoint(
                    rootPath,
                    NtfsIndexProvider.ProviderName,
                    9,
                    100,
                    SqliteSearchIndex.CurrentIndexContentVersion,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(
                new UsnJournalState(9, LowestValidUsn: 50, NextUsn: 200),
                changes: new[]
                {
                    UsnJournalChange.Delete(oldRecord.FullPath),
                    UsnJournalChange.Upsert(newRecord)
                });
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            Assert.Equal(1, provider.ReadJournalChangesCount);
            Assert.Equal(9ul, provider.LastReadExpectedUsnJournalId);
            var oldResults = await index.SearchAsync(new SearchQuery("OldName", SearchMode.FilesAndFolders), CancellationToken.None);
            var newResults = await index.SearchAsync(new SearchQuery("NewName", SearchMode.FilesAndFolders), CancellationToken.None);
            var checkpoint = await index.ReadVolumeCheckpointAsync(rootPath, CancellationToken.None);
            Assert.DoesNotContain(oldResults, result => result.Record.PathKey == oldRecord.PathKey);
            Assert.Equal("NewName.txt", Assert.Single(newResults).Record.Name);
            Assert.Equal(200, checkpoint!.NextUsn);
            Assert.Equal(0, provider.ScanCount);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    [Fact]
    public async Task IndexRootsAsyncFullRescansWhenNtfsJournalChangeRequiresFullRescan()
    {
        var rootPath = CreateTempDirectory();
        var dbPath = CreateTempDbPath();

        try
        {
            var currentRecord = FileRecord.Create(
                Path.Combine(rootPath, "CurrentAfterDirectoryRename.txt"),
                isDirectory: false,
                sizeBytes: 1,
                DateTimeOffset.UtcNow);

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.SaveVolumeCheckpointAsync(
                new UsnJournalCheckpoint(
                    rootPath,
                    NtfsIndexProvider.ProviderName,
                    9,
                    100,
                    SqliteSearchIndex.CurrentIndexContentVersion,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
            var provider = new JournalAwareNtfsProvider(
                new UsnJournalState(9, LowestValidUsn: 50, NextUsn: 200),
                records: new[] { currentRecord },
                changes: new[] { UsnJournalChange.DirectoryRenameOrMove() });
            var coordinator = new IndexingCoordinator(
                index,
                new VolumeIndexer(new IIndexProvider[] { provider }),
                new FallbackIndexProvider(),
                _ => new VolumeInfo(rootPath, NtfsIndexProvider.ProviderName, true));

            await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

            var results = await index.SearchAsync(new SearchQuery("CurrentAfterDirectoryRename", SearchMode.FilesAndFolders), CancellationToken.None);
            var checkpoint = await index.ReadVolumeCheckpointAsync(rootPath, CancellationToken.None);
            Assert.Equal("CurrentAfterDirectoryRename.txt", Assert.Single(results).Record.Name);
            Assert.Equal(1, provider.ScanCount);
            Assert.Equal(200, checkpoint!.NextUsn);
        }
        finally
        {
            DeleteDirectoryIfExists(rootPath);
            DeleteFileIfExists(dbPath);
        }
    }

    private static string CreateUsableHelperBundle(string directory)
    {
        var helperPath = Path.Combine(directory, "ListaryOpen.Indexer.Elevated.exe");
        File.WriteAllText(helperPath, "placeholder");
        File.WriteAllText(Path.Combine(directory, "ListaryOpen.Indexer.Elevated.dll"), "placeholder");
        File.WriteAllText(Path.Combine(directory, "ListaryOpen.Indexer.Elevated.deps.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "ListaryOpen.Indexer.Elevated.runtimeconfig.json"), "{}");
        return helperPath;
    }

    private sealed class EmptyNtfsProvider : IIndexProvider
    {
        public string Name => NtfsIndexProvider.ProviderName;

        public bool CanIndex(VolumeInfo volume) => volume.IsReady;

        public async IAsyncEnumerable<FileRecord> ScanAsync(
            IndexRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CountingFallbackProvider : IIndexProvider
    {
        public int ScanCount { get; private set; }

        public string Name => "Fallback";

        public bool CanIndex(VolumeInfo volume) => volume.IsReady;

        public async IAsyncEnumerable<FileRecord> ScanAsync(
            IndexRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ScanCount++;
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowingNtfsProvider : IIndexProvider
    {
        public string Name => NtfsIndexProvider.ProviderName;

        public bool CanIndex(VolumeInfo volume) => volume.IsReady;

        public async IAsyncEnumerable<FileRecord> ScanAsync(
            IndexRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.FromException(new IOException("NTFS scan failed."));
            yield break;
        }
    }

    private sealed class SingleRecordNtfsProvider : IIndexProvider
    {
        private readonly FileRecord _record;

        public SingleRecordNtfsProvider(FileRecord record)
        {
            _record = record;
        }

        public string Name => NtfsIndexProvider.ProviderName;

        public bool CanIndex(VolumeInfo volume) => volume.IsReady;

        public async IAsyncEnumerable<FileRecord> ScanAsync(
            IndexRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield return _record;
        }
    }

    private sealed class PartialThenThrowingProvider : IIndexProvider
    {
        private readonly FileRecord _record;

        public PartialThenThrowingProvider(FileRecord record)
        {
            _record = record;
        }

        public string Name => NtfsIndexProvider.ProviderName;

        public bool CanIndex(VolumeInfo volume) => volume.IsReady;

        public async IAsyncEnumerable<FileRecord> ScanAsync(
            IndexRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return _record;
            await Task.FromException(new IOException("Scan failed after partial output."));
        }
    }

    private sealed class AsynchronousSingleRecordProvider : IIndexProvider
    {
        private readonly FileRecord _record;

        public AsynchronousSingleRecordProvider(FileRecord record)
        {
            _record = record;
        }

        public string Name => "AsyncProvider";

        public bool CanIndex(VolumeInfo volume) => volume.IsReady;

        public IAsyncEnumerable<FileRecord> ScanAsync(IndexRoot root, CancellationToken cancellationToken)
        {
            return new AsynchronousSingleRecordEnumerable(_record, cancellationToken);
        }
    }

    private sealed class JournalAwareNtfsProvider : IIndexProvider, INtfsJournalProvider
    {
        private readonly UsnJournalState? _journalState;
        private readonly Queue<UsnJournalState?> _journalStates;
        private readonly IReadOnlyList<FileRecord> _records;
        private readonly IReadOnlyList<UsnJournalChange> _changes;
        private readonly Exception? _scanException;
        private readonly Exception? _journalQueryException;

        public JournalAwareNtfsProvider(
            UsnJournalState? journalState,
            IReadOnlyList<FileRecord>? records = null,
            IReadOnlyList<UsnJournalChange>? changes = null,
            Exception? scanException = null,
            Exception? journalQueryException = null)
            : this(
                new[] { journalState },
                records,
                changes,
                scanException,
                journalQueryException)
        {
        }

        public JournalAwareNtfsProvider(
            IEnumerable<UsnJournalState?> journalStates,
            IReadOnlyList<FileRecord>? records = null,
            IReadOnlyList<UsnJournalChange>? changes = null,
            Exception? scanException = null,
            Exception? journalQueryException = null)
        {
            _journalStates = new Queue<UsnJournalState?>(journalStates);
            _journalState = _journalStates.Count > 0 ? _journalStates.Peek() : null;
            _records = records ?? Array.Empty<FileRecord>();
            _changes = changes ?? Array.Empty<UsnJournalChange>();
            _scanException = scanException;
            _journalQueryException = journalQueryException;
        }

        public int ScanCount { get; private set; }

        public int QueryJournalStateCount { get; private set; }

        public int ReadJournalChangesCount { get; private set; }

        public ulong? LastReadExpectedUsnJournalId { get; private set; }

        public string Name => NtfsIndexProvider.ProviderName;

        public bool CanIndex(VolumeInfo volume) => volume.IsReady;

        public async IAsyncEnumerable<FileRecord> ScanAsync(
            IndexRoot root,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ScanCount++;
            foreach (var record in _records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return record;
            }

            if (_scanException is not null)
            {
                await Task.FromException(_scanException);
            }
        }

        public Task<UsnJournalState?> QueryJournalStateAsync(IndexRoot root, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryJournalStateCount++;
            if (_journalQueryException is not null)
            {
                return Task.FromException<UsnJournalState?>(_journalQueryException);
            }

            return Task.FromResult(_journalStates.Count > 0 ? _journalStates.Dequeue() : _journalState);
        }

        public async IAsyncEnumerable<UsnJournalChange> ReadJournalChangesAsync(
            IndexRoot root,
            ulong expectedUsnJournalId,
            long startUsn,
            long endUsn,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadJournalChangesCount++;
            LastReadExpectedUsnJournalId = expectedUsnJournalId;
            foreach (var change in _changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return change;
            }
        }
    }

    private sealed class AsynchronousSingleRecordEnumerable : IAsyncEnumerable<FileRecord>
    {
        private readonly CancellationToken _cancellationToken;
        private readonly FileRecord _record;

        public AsynchronousSingleRecordEnumerable(FileRecord record, CancellationToken cancellationToken)
        {
            _record = record;
            _cancellationToken = cancellationToken;
        }

        public IAsyncEnumerator<FileRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new AsynchronousSingleRecordEnumerator(
                _record,
                CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken));
        }
    }

    private sealed class AsynchronousSingleRecordEnumerator : IAsyncEnumerator<FileRecord>
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly FileRecord _record;
        private bool _hasReturnedRecord;

        public AsynchronousSingleRecordEnumerator(FileRecord record, CancellationTokenSource cancellation)
        {
            _record = record;
            _cancellation = cancellation;
        }

        public FileRecord Current { get; private set; } = null!;

        public async ValueTask<bool> MoveNextAsync()
        {
            await Task.Delay(1, _cancellation.Token).ConfigureAwait(false);
            if (_hasReturnedRecord)
            {
                return false;
            }

            _hasReturnedRecord = true;
            Current = _record;
            return true;
        }

        public ValueTask DisposeAsync()
        {
            _cancellation.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StartFalseElevatedIndexerProcess : IElevatedIndexerProcess
    {
        public int ExitCode => -1;

        public bool HasExited => true;

        public bool Start() => false;

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => _postCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }

    private sealed class CompletedElevatedIndexerProcess : IElevatedIndexerProcess
    {
        private readonly string _stdout;
        private readonly string _stderr;

        public CompletedElevatedIndexerProcess(string stdout, string stderr, int exitCode)
        {
            _stdout = stdout;
            _stderr = stderr;
            ExitCode = exitCode;
        }

        public int ExitCode { get; }

        public bool HasExited => true;

        public bool Start() => true;

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(_stdout);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(_stderr);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StartThrowingElevatedIndexerProcess : IElevatedIndexerProcess
    {
        private readonly Exception _exception;

        public StartThrowingElevatedIndexerProcess(Exception exception)
        {
            _exception = exception;
        }

        public int ExitCode => -1;

        public bool HasExited => true;

        public bool Start() => throw _exception;

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }
}
