using System.Diagnostics;

namespace ListaryOpen.Infrastructure.Indexing;

internal static class ElevatedIndexerProcessStartInfoFactory
{
    public static ProcessStartInfo CreateRedirected(string helperPath, string rootPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("scan");
        startInfo.ArgumentList.Add(rootPath);
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

        startInfo.ArgumentList.Add("scan-to-file");
        startInfo.ArgumentList.Add(rootPath);
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add(errorPath);
        return startInfo;
    }
}
