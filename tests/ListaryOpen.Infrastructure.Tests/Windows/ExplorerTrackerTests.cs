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
