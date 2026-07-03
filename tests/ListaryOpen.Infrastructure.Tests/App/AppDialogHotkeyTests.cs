using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppDialogHotkeyTests
{
    [Fact]
    public void ObserveQuickSwitchFolderCandidatesReturnsMultipleExplorerFoldersForegroundFirst()
    {
        var firstFolder = Directory.CreateTempSubdirectory("listary-open-first-");
        var foregroundFolder = Directory.CreateTempSubdirectory("listary-open-foreground-");
        var thirdFolder = Directory.CreateTempSubdirectory("listary-open-third-");

        try
        {
            var foregroundHandle = new IntPtr(2222);
            var tracker = new ExplorerTracker(
                () => foregroundHandle,
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1111), firstFolder.FullName),
                    new ExplorerShellWindow(foregroundHandle, foregroundFolder.FullName),
                    new ExplorerShellWindow(new IntPtr(3333), thirdFolder.FullName)
                }));

            var candidates = ListaryOpen.App.App.ObserveQuickSwitchFolderCandidates(tracker);

            Assert.Equal(
                new[] { foregroundFolder.FullName, firstFolder.FullName, thirdFolder.FullName },
                candidates.Select(candidate => candidate.FolderPath));
            Assert.True(candidates[0].IsForeground);
            Assert.Equal(foregroundFolder.FullName, tracker.LastFolder);
        }
        finally
        {
            firstFolder.Delete(recursive: true);
            foregroundFolder.Delete(recursive: true);
            thirdFolder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveQuickSwitchFolderCandidatesWithoutExplorerTrackerReturnsEmpty()
    {
        var candidates = ListaryOpen.App.App.ObserveQuickSwitchFolderCandidates(null);

        Assert.Empty(candidates);
    }

    [Fact]
    public void ObserveAndGetExistingTrackedFolderRefreshesExplorerTrackerBeforeReadingLastFolder()
    {
        var staleFolder = Directory.CreateTempSubdirectory("listary-open-stale-");
        var foregroundFolder = Directory.CreateTempSubdirectory("listary-open-foreground-");

        try
        {
            var tracker = new ExplorerTracker(
                () => new IntPtr(1234),
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1234), foregroundFolder.FullName)
                }));
            var stalePath = staleFolder.FullName + Path.DirectorySeparatorChar;
            tracker.ObserveFolderForTests(stalePath);

            var trackedFolder = ListaryOpen.App.App.ObserveAndGetExistingTrackedFolder(tracker);

            Assert.Equal(foregroundFolder.FullName, trackedFolder);
        }
        finally
        {
            staleFolder.Delete(recursive: true);
            foregroundFolder.Delete(recursive: true);
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
