using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class ExplorerObservationSchedulerTests
{
    [Fact]
    public void TickObservesExplorerFolder()
    {
        var observations = 0;
        var timer = new ManualExplorerObservationTimer();
        using var scheduler = new ListaryOpen.App.ExplorerObservationScheduler(
            () => observations++,
            TimeSpan.FromSeconds(1),
            () => timer);

        scheduler.Start();
        timer.RaiseTick();

        Assert.Equal(1, observations);
    }

    [Fact]
    public void TickCatchesObservationExceptionsAndContinuesObservingLaterTicks()
    {
        var observations = 0;
        var timer = new ManualExplorerObservationTimer();
        using var scheduler = new ListaryOpen.App.ExplorerObservationScheduler(
            () =>
            {
                observations++;
                if (observations == 1)
                {
                    throw new InvalidOperationException("Observation failed.");
                }
            },
            TimeSpan.FromSeconds(1),
            () => timer);

        scheduler.Start();
        timer.RaiseTick();
        timer.RaiseTick();

        Assert.Equal(2, observations);
    }

    [Fact]
    public void StartPeriodicExplorerObservationStartsSchedulerForTracker()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-foreground-");

        try
        {
            var foregroundHandle = new IntPtr(1234);
            var tracker = new ExplorerTracker(
                () => foregroundHandle,
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(foregroundHandle, folder.FullName)
                }));
            var timer = new ManualExplorerObservationTimer();

            using var scheduler = ListaryOpen.App.App.StartPeriodicExplorerObservation(
                tracker,
                () => timer);

            Assert.NotNull(scheduler);
            Assert.Equal(TimeSpan.FromSeconds(2), timer.StartedInterval);

            timer.RaiseTick();

            Assert.Equal(folder.FullName, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private sealed class ManualExplorerObservationTimer : ListaryOpen.App.IExplorerObservationTimer
    {
        public event EventHandler? Tick;

        public TimeSpan? StartedInterval { get; private set; }

        public void Start(TimeSpan interval)
        {
            StartedInterval = interval;
        }

        public void Stop()
        {
            StartedInterval = null;
        }

        public void Dispose()
        {
        }

        public void RaiseTick()
        {
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class RecordingExplorerShellWindowsProvider : IExplorerShellWindowsProvider
    {
        private readonly IReadOnlyList<ExplorerShellWindow> _windows;

        public RecordingExplorerShellWindowsProvider(IReadOnlyList<ExplorerShellWindow> windows)
        {
            _windows = windows;
        }

        public IEnumerable<ExplorerShellWindow> EnumerateWindows()
        {
            return _windows;
        }
    }
}
