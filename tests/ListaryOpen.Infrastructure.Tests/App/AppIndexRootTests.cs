using ListaryOpen.App;
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
}
