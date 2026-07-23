using ListaryOpen.App;
using ListaryOpen.Infrastructure.AppData;
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
    public void CreateIndexRootsCollapsesDescendantsToAvoidDuplicateScans()
    {
        var parent = Path.Combine(Path.GetTempPath(), "listary-open-parent-" + Guid.NewGuid());
        var child = Path.Combine(parent, "Start Menu", "Programs");

        var roots = WpfApp.CreateIndexRoots(new[] { child, parent, child });

        Assert.Equal(Path.GetFullPath(parent), Assert.Single(roots).Path);
    }

    [Fact]
    public void CreateIndexRootsTreatsDriveRootAsParentOfEveryLocationOnDrive()
    {
        var driveRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var descendant = Path.Combine(driveRoot, "ProgramData", "Microsoft", "Windows", "Start Menu");

        var roots = WpfApp.CreateIndexRoots([descendant, driveRoot]);

        Assert.Equal(driveRoot, Assert.Single(roots).Path);
        Assert.True(WpfApp.IsPathWithinAnyRoot(descendant, [driveRoot]));
    }

    [Fact]
    public void ElevatedIndexerClientUsesHelperBundleFromProgramDirectory()
    {
        var programDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(programDirectory);

        try
        {
            _ = CreateUsableHelperBundle(programDirectory);
            var paths = AppDataPaths.CreateUnderProgramDirectory(programDirectory);

            var client = WpfApp.CreateElevatedIndexerClient(paths);

            Assert.True(WpfApp.EnableNtfsFastIndexingOnStartup(client));
            Assert.True(client.IsAvailable);
        }
        finally
        {
            Directory.Delete(programDirectory, recursive: true);
        }
    }

    [Fact]
    public void EnableNtfsFastIndexingEnablesClientAndRequestsReindex()
    {
        var helperDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(helperDirectory);
        var helperPath = CreateUsableHelperBundle(helperDirectory);
        var client = new ElevatedIndexerClient(helperPath, () => false);
        var reindexRequested = false;

        try
        {
            var enabled = WpfApp.EnableNtfsFastIndexing(client, () => reindexRequested = true);

            Assert.True(enabled);
            Assert.True(client.UacElevationEnabled);
            Assert.True(client.IsAvailable);
            Assert.True(reindexRequested);
        }
        finally
        {
            Directory.Delete(helperDirectory, recursive: true);
        }
    }

    [Fact]
    public void EnableNtfsFastIndexingDoesNotReindexWithoutHelperBundle()
    {
        var client = new ElevatedIndexerClient(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), () => false);
        var reindexRequested = false;

        var enabled = WpfApp.EnableNtfsFastIndexing(client, () => reindexRequested = true);

        Assert.False(enabled);
        Assert.False(client.IsAvailable);
        Assert.False(reindexRequested);
    }

    [Fact]
    public void EnableNtfsFastIndexingOnStartupEnablesClientWithoutRequestingImmediateReindex()
    {
        var helperDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(helperDirectory);
        var helperPath = CreateUsableHelperBundle(helperDirectory);
        var client = new ElevatedIndexerClient(helperPath, () => true);

        try
        {
            var enabled = WpfApp.EnableNtfsFastIndexingOnStartup(client);

            Assert.True(enabled);
            Assert.True(client.UacElevationEnabled);
            Assert.True(client.IsAvailable);
        }
        finally
        {
            Directory.Delete(helperDirectory, recursive: true);
        }
    }

    [Fact]
    public void EnableNtfsFastIndexingOnStartupArmsUacWhenProcessIsNotElevated()
    {
        var helperDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(helperDirectory);
        var helperPath = CreateUsableHelperBundle(helperDirectory);
        var client = new ElevatedIndexerClient(helperPath, () => false);

        try
        {
            var enabled = WpfApp.EnableNtfsFastIndexingOnStartup(client);

            Assert.True(enabled);
            Assert.True(client.UacElevationEnabled);
            Assert.True(client.IsAvailable);
        }
        finally
        {
            Directory.Delete(helperDirectory, recursive: true);
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
    public void IndexingRunCancellationManagerCancelsOutstandingQueuedRunWhenRestarting()
    {
        using var shutdown = new CancellationTokenSource();
        using var manager = new IndexingRunCancellationManager();

        var activeRun = manager.CreateRun(shutdown.Token, cancelActive: false);
        var queuedRun = manager.CreateRun(shutdown.Token, cancelActive: false);
        var restartRun = manager.CreateRun(shutdown.Token, cancelActive: true);

        try
        {
            Assert.True(activeRun.IsCancellationRequested);
            Assert.True(queuedRun.IsCancellationRequested);
            Assert.False(restartRun.IsCancellationRequested);
        }
        finally
        {
            manager.CompleteRun(activeRun);
            manager.CompleteRun(queuedRun);
            manager.CompleteRun(restartRun);
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

    private static string CreateUsableHelperBundle(string directory)
    {
        var helperPath = Path.Combine(directory, "ListaryOpen.Indexer.Elevated.exe");
        File.WriteAllText(helperPath, "placeholder");
        File.WriteAllText(Path.Combine(directory, "ListaryOpen.Indexer.Elevated.dll"), "placeholder");
        File.WriteAllText(Path.Combine(directory, "ListaryOpen.Indexer.Elevated.deps.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "ListaryOpen.Indexer.Elevated.runtimeconfig.json"), "{}");
        return helperPath;
    }
}
