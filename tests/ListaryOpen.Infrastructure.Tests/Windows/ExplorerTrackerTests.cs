using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class ExplorerTrackerTests
{
    [Fact]
    public void ObserveFolderForTestsStoresExistingFolderAsFullyQualifiedPath()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-explorer-");

        try
        {
            var tracker = new ExplorerTracker();

            tracker.ObserveFolderForTests(folder.FullName + Path.DirectorySeparatorChar);

            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.FullName)),
                tracker.LastFolder);
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
            tracker.ObserveFolderForTests(folder.FullName);

            tracker.ObserveFolderForTests(missingFolder);

            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.FullName)),
                tracker.LastFolder);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
