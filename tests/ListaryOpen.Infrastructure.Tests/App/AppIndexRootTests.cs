using ListaryOpen.App;
using ListaryOpen.Infrastructure.Indexing;
using WpfApp = ListaryOpen.App.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppIndexRootTests
{
    [Fact]
    public void CreateIndexRootsSkipsInvalidRoots()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "listary-open-root-" + Guid.NewGuid());

        var roots = WpfApp.CreateIndexRoots(new[] { rootPath, " ", "relative-root" });

        var root = Assert.Single(roots);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath)), root.Path);
    }

    [Fact]
    public async Task BackgroundIndexingTaskTrackerWaitsForTrackedWork()
    {
        var tracker = new BackgroundIndexingTaskTracker();
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackedTaskCompleted = false;

        tracker.Track(Task.Run(async () =>
        {
            await releaseWork.Task;
            trackedTaskCompleted = true;
        }));

        var waitTask = tracker.WaitForCompletionAsync();

        Assert.False(waitTask.IsCompleted);

        releaseWork.SetResult();
        await waitTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(trackedTaskCompleted);
    }

    [Fact]
    public async Task BackgroundIndexingTaskStarterRunsWorkOffCallerSynchronizationContext()
    {
        var tracker = new BackgroundIndexingTaskTracker();
        var callerThreadId = Environment.CurrentManagedThreadId;
        var callerContext = new SynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        var observedExecution = new TaskCompletionSource<(int ThreadId, SynchronizationContext? Context)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        SynchronizationContext.SetSynchronizationContext(callerContext);
        try
        {
            BackgroundIndexingTaskStarter.Start(
                tracker,
                _ =>
                {
                    observedExecution.SetResult((Environment.CurrentManagedThreadId, SynchronizationContext.Current));
                    return Task.CompletedTask;
                },
                CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        var execution = await observedExecution.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await tracker.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(callerThreadId, execution.ThreadId);
        Assert.Null(execution.Context);
    }

    [Fact]
    public void EnableNtfsFastIndexingEnablesClientAndRequestsReindex()
    {
        var helperPath = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid(), "ListaryOpen.Indexer.Elevated.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, "placeholder");
        var client = new ElevatedIndexerClient(helperPath, () => false);
        var reindexRequested = false;

        try
        {
            WpfApp.EnableNtfsFastIndexing(client, () => reindexRequested = true);

            Assert.True(client.UacElevationEnabled);
            Assert.True(client.IsAvailable);
            Assert.True(reindexRequested);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(helperPath)!, recursive: true);
        }
    }

    [Fact]
    public void IndexingRunCancellationManagerCancelsActiveRunWhenRestarting()
    {
        using var shutdown = new CancellationTokenSource();
        using var manager = new IndexingRunCancellationManager();

        var firstRun = manager.CreateRun(shutdown.Token, cancelActive: false);
        var secondRun = manager.CreateRun(shutdown.Token, cancelActive: true);

        try
        {
            Assert.True(firstRun.IsCancellationRequested);
            Assert.False(secondRun.IsCancellationRequested);
        }
        finally
        {
            manager.CompleteRun(firstRun);
            manager.CompleteRun(secondRun);
        }
    }

    [Fact]
    public async Task RunSerializedIndexingAsyncQueuesConcurrentRequests()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var releaseFirstRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRunStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;

        Task Work(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref runCount) == 1)
            {
                firstRunStarted.SetResult();
                return releaseFirstRun.Task;
            }

            return Task.CompletedTask;
        }

        var firstRun = WpfApp.RunSerializedIndexingAsync(gate, Work, CancellationToken.None);
        await firstRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondRun = WpfApp.RunSerializedIndexingAsync(gate, Work, CancellationToken.None);

        Assert.False(secondRun.IsCompleted);

        releaseFirstRun.SetResult();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, runCount);
    }
}
