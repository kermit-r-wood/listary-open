using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class ExplorerTrackerTests
{
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
}
