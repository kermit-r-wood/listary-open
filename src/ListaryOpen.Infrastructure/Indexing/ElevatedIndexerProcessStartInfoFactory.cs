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

        AddFileArguments(startInfo, rootPath, outputPath, errorPath);
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

        AddFileArguments(startInfo, rootPath, outputPath, errorPath);
        return startInfo;
    }

    private static void AddFileArguments(
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
}
