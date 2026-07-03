using ListaryOpen.Infrastructure.Windows;
using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class ExplorerTrackerTests
{
    [Fact]
    public void ObserveForegroundExplorerFolderStoresMatchingExistingFolder()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");

        try
        {
            var foregroundHandle = new IntPtr(1234);
            var tracker = new ExplorerTracker(
                () => foregroundHandle,
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(5678), "C:\\Other"),
                    new ExplorerShellWindow(foregroundHandle, folder.FullName)
                }));

            tracker.ObserveForegroundExplorerFolder();

            Assert.Equal(folder.FullName, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveForegroundExplorerFolderWithoutMatchingWindowLeavesLastFolderUnchanged()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");

        try
        {
            var existingPath = folder.FullName + Path.DirectorySeparatorChar;
            var tracker = new ExplorerTracker(
                () => new IntPtr(1234),
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(5678), folder.FullName)
                }));
            tracker.ObserveFolderForTests(existingPath);

            tracker.ObserveForegroundExplorerFolder();

            Assert.Equal(existingPath, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveForegroundExplorerFolderWhenShellEnumerationFailsLeavesLastFolderUnchanged()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");

        try
        {
            var existingPath = folder.FullName + Path.DirectorySeparatorChar;
            var tracker = new ExplorerTracker(
                () => new IntPtr(1234),
                new ThrowingExplorerShellWindowsProvider(new COMException("Shell unavailable.")));
            tracker.ObserveFolderForTests(existingPath);

            tracker.ObserveForegroundExplorerFolder();

            Assert.Equal(existingPath, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveFolderForTestsStoresExistingFolderPathExactly()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");

        try
        {
            var tracker = new ExplorerTracker();

            var observedPath = folder.FullName + Path.DirectorySeparatorChar;

            tracker.ObserveFolderForTests(observedPath);

            Assert.Equal(observedPath, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void ObserveFolderForTestsIgnoresMissingFolderAndKeepsLastFolder()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");
        var missingFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        try
        {
            var tracker = new ExplorerTracker();
            var observedPath = folder.FullName + Path.DirectorySeparatorChar;
            tracker.ObserveFolderForTests(observedPath);

            tracker.ObserveFolderForTests(missingFolder);

            Assert.Equal(observedPath, tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetFolderCandidatesReturnsForegroundExplorerFolderFirstAndDeduplicates()
    {
        var first = Directory.CreateTempSubdirectory("listary-open-first-");
        var second = Directory.CreateTempSubdirectory("listary-open-second-");

        try
        {
            var foregroundHandle = new IntPtr(2222);
            var tracker = new ExplorerTracker(
                () => foregroundHandle,
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1111), first.FullName),
                    new ExplorerShellWindow(foregroundHandle, second.FullName),
                    new ExplorerShellWindow(new IntPtr(3333), first.FullName + Path.DirectorySeparatorChar)
                }));

            var candidates = tracker.GetFolderCandidates();

            Assert.Equal(new[] { second.FullName, first.FullName }, candidates.Select(candidate => candidate.FolderPath));
            Assert.True(candidates[0].IsForeground);
            Assert.Equal("Explorer", candidates[0].SourceName);
            Assert.Equal(foregroundHandle, candidates[0].WindowHandle);
        }
        finally
        {
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetFolderCandidatesPrefersLaterForegroundDuplicateFolder()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-duplicate-");

        try
        {
            var foregroundHandle = new IntPtr(2222);
            var tracker = new ExplorerTracker(
                () => foregroundHandle,
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1111), folder.FullName),
                    new ExplorerShellWindow(foregroundHandle, folder.FullName + Path.DirectorySeparatorChar)
                }));

            var candidate = Assert.Single(tracker.GetFolderCandidates());

            Assert.True(candidate.IsForeground);
            Assert.Equal(foregroundHandle, candidate.WindowHandle);
            Assert.Equal(folder.FullName, candidate.FolderPath);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetFolderCandidatesSkipsMissingFolders()
    {
        var existing = Directory.CreateTempSubdirectory("listary-open-existing-");
        var missing = Path.Combine(Path.GetTempPath(), "listary-open-missing-" + Guid.NewGuid());

        try
        {
            var tracker = new ExplorerTracker(
                () => new IntPtr(1234),
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1234), missing),
                    new ExplorerShellWindow(new IntPtr(5678), existing.FullName)
                }));

            var candidate = Assert.Single(tracker.GetFolderCandidates());

            Assert.Equal(existing.FullName, candidate.FolderPath);
            Assert.False(candidate.IsForeground);
        }
        finally
        {
            existing.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetFolderCandidatesPrefersRememberedFolderWhenNoExplorerWindowIsForeground()
    {
        var remembered = Directory.CreateTempSubdirectory("listary-open-remembered-");
        var firstEnumerated = Directory.CreateTempSubdirectory("listary-open-first-");

        try
        {
            var tracker = new ExplorerTracker(
                () => new IntPtr(9999),
                new RecordingExplorerShellWindowsProvider(new[]
                {
                    new ExplorerShellWindow(new IntPtr(1111), firstEnumerated.FullName),
                    new ExplorerShellWindow(new IntPtr(2222), remembered.FullName)
                }));
            tracker.ObserveFolderForTests(remembered.FullName);

            var candidates = tracker.GetFolderCandidates();

            Assert.Equal(remembered.FullName, candidates[0].FolderPath);
            Assert.False(candidates[0].IsForeground);
        }
        finally
        {
            remembered.Delete(recursive: true);
            firstEnumerated.Delete(recursive: true);
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

    private sealed class ThrowingExplorerShellWindowsProvider : IExplorerShellWindowsProvider
    {
        private readonly Exception _exception;

        public ThrowingExplorerShellWindowsProvider(Exception exception)
        {
            _exception = exception;
        }

        public IEnumerable<ExplorerShellWindow> EnumerateWindows()
        {
            throw _exception;
        }
    }
}
