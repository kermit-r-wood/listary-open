using System.IO;

namespace ListaryOpen.Infrastructure.AppData;

public sealed record AppDataPaths(
    string ProgramDirectory,
    string DataDirectory,
    string IndexDatabasePath,
    string IndexerTempDirectory)
{
    public static AppDataPaths CreateDefault()
    {
        return CreateUnderProgramDirectory(AppContext.BaseDirectory);
    }

    public static AppDataPaths CreateUnderProgramDirectory(string programDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);

        var fullProgramDirectory = Path.GetFullPath(programDirectory);
        var dataDirectory = Path.Combine(fullProgramDirectory, "data");
        return new AppDataPaths(
            fullProgramDirectory,
            dataDirectory,
            Path.Combine(dataDirectory, "index.db"),
            Path.Combine(dataDirectory, "tmp"));
    }

    public static string CreateLegacyLocalAppDataIndexDatabasePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ListaryOpen",
            "index.db");
    }

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string PerformanceMetricsPath => Path.Combine(DataDirectory, "performance-metrics.jsonl");

    public string DiagnosticLogPath => Path.Combine(DataDirectory, "listaryopen.log");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(IndexerTempDirectory);
    }
}
