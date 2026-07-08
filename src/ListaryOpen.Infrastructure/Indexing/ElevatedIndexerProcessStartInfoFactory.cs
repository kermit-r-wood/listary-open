using System.Diagnostics;

namespace ListaryOpen.Infrastructure.Indexing;

internal static class ElevatedIndexerProcessStartInfoFactory
{
    public static ProcessStartInfo CreateRedirectedFile(
        string helperPath,
        string rootPath,
        string outputPath,
        string errorPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };

        AddScanFileArguments(startInfo, rootPath, outputPath, errorPath);
        return startInfo;
    }

    public static ProcessStartInfo CreateRedirectedJournalStateFile(
        string helperPath,
        string rootPath,
        string outputPath,
        string errorPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };

        AddJournalStateFileArguments(startInfo, rootPath, outputPath, errorPath);
        return startInfo;
    }

    public static ProcessStartInfo CreateRedirectedJournalChangesFile(
        string helperPath,
        string rootPath,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        string outputPath,
        string errorPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };

        AddJournalChangesFileArguments(startInfo, rootPath, expectedUsnJournalId, startUsn, endUsn, outputPath, errorPath);
        return startInfo;
    }

    public static ProcessStartInfo CreateUacFile(
        string helperPath,
        string rootPath,
        string outputPath,
        string errorPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true
        };

        AddScanFileArguments(startInfo, rootPath, outputPath, errorPath);
        return startInfo;
    }

    public static ProcessStartInfo CreateUacJournalStateFile(
        string helperPath,
        string rootPath,
        string outputPath,
        string errorPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true
        };

        AddJournalStateFileArguments(startInfo, rootPath, outputPath, errorPath);
        return startInfo;
    }

    public static ProcessStartInfo CreateUacJournalChangesFile(
        string helperPath,
        string rootPath,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        string outputPath,
        string errorPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true
        };

        AddJournalChangesFileArguments(startInfo, rootPath, expectedUsnJournalId, startUsn, endUsn, outputPath, errorPath);
        return startInfo;
    }

    private static void AddScanFileArguments(
        ProcessStartInfo startInfo,
        string rootPath,
        string outputPath,
        string errorPath)
    {
        startInfo.ArgumentList.Add("scan-to-file");
        startInfo.ArgumentList.Add(rootPath);
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(errorPath);
    }

    private static void AddJournalStateFileArguments(
        ProcessStartInfo startInfo,
        string rootPath,
        string outputPath,
        string errorPath)
    {
        startInfo.ArgumentList.Add("journal-state-to-file");
        startInfo.ArgumentList.Add(rootPath);
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(errorPath);
    }

    private static void AddJournalChangesFileArguments(
        ProcessStartInfo startInfo,
        string rootPath,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        string outputPath,
        string errorPath)
    {
        startInfo.ArgumentList.Add("read-journal-to-file");
        startInfo.ArgumentList.Add(rootPath);
        startInfo.ArgumentList.Add(expectedUsnJournalId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(startUsn.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(endUsn.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(errorPath);
    }
}
