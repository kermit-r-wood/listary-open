using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ContinuousIndexingServiceTests
{
    [Fact]
    public async Task BurstsAreCoalescedAndNeverApplyConcurrently()
    {
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "coalesced.txt");
        await File.WriteAllTextAsync(path, "one");
        var sink = new BlockingSink();
        using var service = CreateService(sink, root, debounce: TimeSpan.FromMilliseconds(20));
        service.Start(baselineReady: true);

        Assert.True(service.TryEnqueueForTests(new FileChangeHint(FileChangeHintKind.Changed, path)));
        await sink.FirstApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var index = 0; index < 100; index++)
        {
            Assert.True(service.TryEnqueueForTests(new FileChangeHint(FileChangeHintKind.Changed, path)));
        }

        sink.ReleaseFirstApply.TrySetResult();
        await WaitUntilAsync(() => sink.ApplyCalls >= 2);

        Assert.Equal(1, sink.MaximumConcurrency);
        Assert.InRange(sink.ApplyCalls, 2, 3);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task InternalDataDirectoryIsNeverQueuedOrApplied()
    {
        var root = CreateTempDirectory();
        var dataDirectory = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        var internalFile = Path.Combine(dataDirectory, "index.db-wal");
        await File.WriteAllTextAsync(internalFile, "internal");
        var externalFile = Path.Combine(root, "visible.txt");
        await File.WriteAllTextAsync(externalFile, "visible");
        var sink = new RecordingSink();
        using var service = new ContinuousIndexingService(
            sink,
            new[] { root },
            Array.Empty<string>(),
            new[] { dataDirectory },
            () => { },
            new NoopWatcherFactory(),
            TimeSpan.Zero);
        service.Start(baselineReady: true);

        Assert.False(service.TryEnqueueForTests(new FileChangeHint(FileChangeHintKind.Changed, internalFile)));
        Assert.True(service.TryEnqueueForTests(new FileChangeHint(FileChangeHintKind.Changed, externalFile)));
        await WaitUntilAsync(() => sink.Changes.Count > 0);

        Assert.DoesNotContain(
            sink.Changes,
            change => change.FullPath is not null &&
                change.FullPath.StartsWith(dataDirectory, StringComparison.OrdinalIgnoreCase));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task CreateAndDeleteHintsUpdateSearchIndex()
    {
        var root = CreateTempDirectory();
        var databaseDirectory = CreateTempDirectory();
        var databasePath = Path.Combine(databaseDirectory, "index.db");
        var index = await SqliteSearchIndex.OpenAsync(databasePath, CancellationToken.None);
        try
        {
            using var service = CreateService(new SqliteLiveIndexChangeSink(index), root, TimeSpan.Zero);
            service.Start(baselineReady: true);
            var path = Path.Combine(root, "continuous-created-unique.txt");
            await File.WriteAllTextAsync(path, "created");

            Assert.True(service.TryEnqueueForTests(new FileChangeHint(FileChangeHintKind.Created, path)));
            await WaitUntilAsync(() => HasResultAsync(index, "continuous-created-unique"));

            File.Delete(path);
            Assert.True(service.TryEnqueueForTests(new FileChangeHint(FileChangeHintKind.Deleted, path)));
            await WaitUntilAsync(async () => !await HasResultAsync(index, "continuous-created-unique"));
        }
        finally
        {
            await index.DisposeAsync();
            Directory.Delete(root, recursive: true);
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DirectoryRenameDeletesOldSubtreeAndIndexesNewSubtree()
    {
        var root = CreateTempDirectory();
        var databaseDirectory = CreateTempDirectory();
        var databasePath = Path.Combine(databaseDirectory, "index.db");
        var index = await SqliteSearchIndex.OpenAsync(databasePath, CancellationToken.None);
        try
        {
            using var service = CreateService(new SqliteLiveIndexChangeSink(index), root, TimeSpan.Zero);
            service.Start(baselineReady: true);

            var oldDirectory = Directory.CreateDirectory(Path.Combine(root, "old-container")).FullName;
            var oldChild = Path.Combine(oldDirectory, "rename-child-unique.txt");
            await File.WriteAllTextAsync(oldChild, "child");
            Assert.True(service.TryEnqueueForTests(new FileChangeHint(FileChangeHintKind.Created, oldDirectory)));
            await WaitUntilAsync(() => HasPathAsync(index, oldChild));

            var newDirectory = Path.Combine(root, "new-container");
            Directory.Move(oldDirectory, newDirectory);
            var newChild = Path.Combine(newDirectory, Path.GetFileName(oldChild));
            Assert.True(service.TryEnqueueForTests(
                new FileChangeHint(FileChangeHintKind.Renamed, newDirectory, oldDirectory)));
            await WaitUntilAsync(async () =>
                !await HasPathAsync(index, oldChild) &&
                await HasPathAsync(index, newChild));
        }
        finally
        {
            await index.DisposeAsync();
            Directory.Delete(root, recursive: true);
            Directory.Delete(databaseDirectory, recursive: true);
        }
    }

    private static ContinuousIndexingService CreateService(
        ILiveIndexChangeSink sink,
        string root,
        TimeSpan debounce) =>
        new(
            sink,
            new[] { root },
            Array.Empty<string>(),
            Array.Empty<string>(),
            () => { },
            new NoopWatcherFactory(),
            debounce);

    private static async Task<bool> HasResultAsync(SqliteSearchIndex index, string term)
    {
        var results = await index.SearchAsync(
            new SearchQuery(term, SearchMode.FilesAndFolders, limit: 20),
            CancellationToken.None);
        return results.Count > 0;
    }

    private static async Task<bool> HasPathAsync(SqliteSearchIndex index, string path)
    {
        var results = await index.SearchAsync(
            new SearchQuery(Path.GetFileName(path), SearchMode.FilesAndFolders, limit: 50),
            CancellationToken.None);
        return results.Any(result => string.Equals(
            result.Record.FullPath,
            path,
            StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
            {
                throw new TimeoutException("Continuous indexing did not reach the expected state.");
            }

            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var timeoutAt = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow >= timeoutAt)
            {
                throw new TimeoutException("Continuous indexing did not reach the expected state.");
            }

            await Task.Delay(20);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"listary-open-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class NoopWatcherFactory : IIndexRootWatcherFactory
    {
        public IIndexRootWatcher Create(
            string rootPath,
            Action<FileChangeHint> onChange,
            Action<Exception> onError) => new NoopWatcher();
    }

    private sealed class NoopWatcher : IIndexRootWatcher
    {
        public void Start()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingSink : ILiveIndexChangeSink
    {
        private readonly object _gate = new();
        private readonly List<LiveIndexChange> _changes = new();

        public IReadOnlyList<LiveIndexChange> Changes
        {
            get
            {
                lock (_gate)
                {
                    return _changes.ToArray();
                }
            }
        }

        public Task ApplyAsync(IReadOnlyList<LiveIndexChange> changes, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _changes.AddRange(changes);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingSink : ILiveIndexChangeSink
    {
        private int _active;
        private int _applyCalls;
        private int _maximumConcurrency;

        public TaskCompletionSource FirstApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstApply { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ApplyCalls => Volatile.Read(ref _applyCalls);

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task ApplyAsync(IReadOnlyList<LiveIndexChange> changes, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            var call = Interlocked.Increment(ref _applyCalls);
            try
            {
                if (call == 1)
                {
                    FirstApplyStarted.TrySetResult();
                    await ReleaseFirstApply.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrency);
                if (current >= active ||
                    Interlocked.CompareExchange(ref _maximumConcurrency, active, current) == current)
                {
                    return;
                }
            }
        }
    }
}
