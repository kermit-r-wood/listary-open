using ListaryOpen.Infrastructure.AppData;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppDataPathsTests
{
    [Fact]
    public void CreateUnderProgramDirectoryUsesDataSubdirectories()
    {
        var programDirectory = Path.Combine(Path.GetTempPath(), "listary-open-program-" + Guid.NewGuid());

        var paths = AppDataPaths.CreateUnderProgramDirectory(programDirectory);

        Assert.Equal(Path.GetFullPath(programDirectory), paths.ProgramDirectory);
        Assert.Equal(Path.Combine(programDirectory, "data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(programDirectory, "data", "index.db"), paths.IndexDatabasePath);
        Assert.Equal(Path.Combine(programDirectory, "data", "tmp"), paths.IndexerTempDirectory);
        Assert.Equal(Path.Combine(programDirectory, "data", "listaryopen.log"), paths.DiagnosticLogPath);
    }

    [Fact]
    public void EnsureDirectoriesCreatesDataAndTmp()
    {
        var programDirectory = Path.Combine(Path.GetTempPath(), "listary-open-program-" + Guid.NewGuid());
        var paths = AppDataPaths.CreateUnderProgramDirectory(programDirectory);

        try
        {
            paths.EnsureDirectories();

            Assert.True(Directory.Exists(paths.DataDirectory));
            Assert.True(Directory.Exists(paths.IndexerTempDirectory));
        }
        finally
        {
            if (Directory.Exists(programDirectory))
            {
                Directory.Delete(programDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateLegacyLocalAppDataIndexDatabasePathReturnsOldIndexLocation()
    {
        var path = AppDataPaths.CreateLegacyLocalAppDataIndexDatabasePath();

        Assert.EndsWith(Path.Combine("ListaryOpen", "index.db"), path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            path,
            StringComparison.OrdinalIgnoreCase);
    }
}
