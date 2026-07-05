using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing;
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
            Assert.Contains(statuses, status => status.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Completed, statuses[^1].State);
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
            Assert.Contains(statuses, status => status.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(IndexingRunState.Completed, statuses[^1].State);
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
