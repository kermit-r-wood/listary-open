using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class ExplorerObservationSchedulerTests
{
    [Fact]
    public void TickObservesExplorerFolder()
    {
        var observations = 0;
        var observationApartment = ApartmentState.Unknown;
        var timer = new ManualExplorerObservationTimer();
        using var scheduler = new ListaryOpen.App.ExplorerObservationScheduler(
            () =>
            {
                observationApartment = Thread.CurrentThread.GetApartmentState();
                observations++;
            },
            TimeSpan.FromSeconds(1),
            () => timer);

        scheduler.Start();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref observations) == 1, TimeSpan.FromSeconds(1)));
        Assert.Equal(ApartmentState.STA, observationApartment);
        timer.RaiseTick();

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref observations) == 2, TimeSpan.FromSeconds(1)));
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
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref observations) == 1, TimeSpan.FromSeconds(1)));
        timer.RaiseTick();

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref observations) == 2, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RequestedObservationCompletesAfterFreshSnapshotIsPublished()
    {
        var folderA = Directory.CreateTempSubdirectory("listary-open-observe-a-");
        var folderB = Directory.CreateTempSubdirectory("listary-open-observe-b-");
        try
        {
            var window = new IntPtr(1234);
            var provider = new MutableExplorerShellWindowsProvider(
                new[] { new ExplorerShellWindow(window, folderA.FullName) });
            var tracker = new ExplorerTracker(() => window, provider);
            var timer = new ManualExplorerObservationTimer();
            using var scheduler = new ListaryOpen.App.ExplorerObservationScheduler(
                tracker.ObserveForegroundExplorerFolder,
                TimeSpan.FromSeconds(2),
                () => timer);
            scheduler.Start();
            await scheduler.RequestObservationAsync().WaitAsync(TimeSpan.FromSeconds(5));

            provider.Windows = new[] { new ExplorerShellWindow(window, folderB.FullName) };
            await scheduler.RequestObservationAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var candidate = Assert.Single(tracker.GetFolderCandidates());
            Assert.Equal(folderB.FullName, candidate.FolderPath);
            Assert.True(candidate.IsForeground);
        }
        finally
        {
            folderA.Delete(recursive: true);
            folderB.Delete(recursive: true);
        }
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
            Assert.Equal(TimeSpan.FromSeconds(10), timer.StartedInterval);

            timer.RaiseTick();

            Assert.True(SpinWait.SpinUntil(
                () => string.Equals(folder.FullName, tracker.LastFolder, StringComparison.Ordinal),
                TimeSpan.FromSeconds(1)));
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

    private sealed class MutableExplorerShellWindowsProvider : IExplorerShellWindowsProvider
    {
        public MutableExplorerShellWindowsProvider(IReadOnlyList<ExplorerShellWindow> windows)
        {
            Windows = windows;
        }

        public IReadOnlyList<ExplorerShellWindow> Windows { get; set; }

        public IEnumerable<ExplorerShellWindow> EnumerateWindows() => Windows;
    }
}
