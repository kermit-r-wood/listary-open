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
}
