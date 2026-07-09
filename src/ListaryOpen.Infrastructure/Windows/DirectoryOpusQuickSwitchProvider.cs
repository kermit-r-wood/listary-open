using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Xml.Linq;

namespace ListaryOpen.Infrastructure.Windows;

public sealed class DirectoryOpusQuickSwitchProvider : IQuickSwitchWindowProvider
{
    private const string SourceName = "Directory Opus";
    private static readonly TimeSpan InfoTimeout = TimeSpan.FromSeconds(1);
    private readonly Func<string?> _infoReader;
    private readonly Func<bool> _foregroundProvider;

    public DirectoryOpusQuickSwitchProvider()
        : this(ReadDirectoryOpusInfo, IsDirectoryOpusForeground)
    {
    }

    internal DirectoryOpusQuickSwitchProvider(Func<string?> infoReader, Func<bool>? foregroundProvider = null)
    {
        _infoReader = infoReader ?? throw new ArgumentNullException(nameof(infoReader));
        _foregroundProvider = foregroundProvider ?? (() => false);
    }

    public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
    {
        try
        {
            var isForeground = _foregroundProvider();
            return ParseInfoPaths(_infoReader(), directoryOpusIsForeground: isForeground);
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or Win32Exception
                                          or UnauthorizedAccessException
                                          or SecurityException)
        {
            return Array.Empty<QuickSwitchFolderCandidate>();
        }
    }

    internal static IReadOnlyList<QuickSwitchFolderCandidate> ParseInfoPaths(
        string? info,
        bool directoryOpusIsForeground = true)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return Array.Empty<QuickSwitchFolderCandidate>();
        }

        var elements = TryParsePathElements(info);
        if (elements.Count == 0)
        {
            return Array.Empty<QuickSwitchFolderCandidate>();
        }

        return elements
            .Select(CreateParsedPath)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .GroupBy(candidate => candidate.FolderPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(candidate => candidate.SortRank).First())
            .OrderBy(candidate => candidate.SortRank)
            .ThenBy(candidate => candidate.FolderPath, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new QuickSwitchFolderCandidate(
                candidate.FolderPath,
                SourceName,
                IntPtr.Zero,
                directoryOpusIsForeground && candidate.IsForeground))
            .ToArray();
    }

    private static ParsedDirectoryOpusPath? CreateParsedPath(XElement element)
    {
        var path = element.Value;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(folderPath))
            {
                return null;
            }

            var activeLister = IsTruthyAttribute(element, "active_lister");
            var activeTab = IsTruthyAttribute(element, "active_tab");
            var tabState = ((string?)element.Attribute("tab_state") ?? string.Empty).Trim();
            var sortRank = GetSortRank(activeLister, activeTab, tabState);
            return new ParsedDirectoryOpusPath(
                folderPath,
                sortRank,
                activeLister && activeTab);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or NotSupportedException
                                          or PathTooLongException
                                          or UnauthorizedAccessException
                                          or SecurityException)
        {
            return null;
        }
    }

    private static int GetSortRank(bool activeLister, bool activeTab, string tabState)
    {
        if (activeLister && activeTab)
        {
            return 0;
        }

        if (activeLister && IsSourceTabState(tabState))
        {
            return 1;
        }

        if (activeLister && IsDestinationTabState(tabState))
        {
            return 2;
        }

        return 3;
    }

    private static bool IsTruthyAttribute(XElement element, string attributeName)
    {
        var value = ((string?)element.Attribute(attributeName) ?? string.Empty).Trim();
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceTabState(string tabState)
    {
        return string.Equals(tabState, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tabState, "source", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tabState, "src", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDestinationTabState(string tabState)
    {
        return string.Equals(tabState, "2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tabState, "destination", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tabState, "dest", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<XElement> TryParsePathElements(string info)
    {
        try
        {
            return XDocument.Parse(info).Descendants("path").ToArray();
        }
        catch (Exception exception) when (exception is System.Xml.XmlException)
        {
        }

        try
        {
            return XDocument.Parse("<paths>" + info + "</paths>").Descendants("path").ToArray();
        }
        catch (Exception exception) when (exception is System.Xml.XmlException)
        {
            return Array.Empty<XElement>();
        }
    }

    private static string? ReadDirectoryOpusInfo()
    {
        var dopusPath = TryGetDirectoryOpusProcessPath();
        if (string.IsNullOrWhiteSpace(dopusPath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(dopusPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var dopusRtPath = Path.Combine(directory, "dopusrt.exe");
        if (!File.Exists(dopusRtPath))
        {
            return null;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "ListaryOpen", "QuickSwitch");
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".xml");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = dopusRtPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("/info");
            startInfo.ArgumentList.Add(outputPath + ",paths");

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit((int)InfoTimeout.TotalMilliseconds))
            {
                TryKillProcess(process);
                return null;
            }

            return File.Exists(outputPath)
                ? File.ReadAllText(outputPath)
                : null;
        }
        finally
        {
            TryDeleteFile(outputPath);
        }
    }

    private static string? TryGetDirectoryOpusProcessPath()
    {
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcessesByName("dopus");
            return processes.FirstOrDefault()?.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or NotSupportedException
                                          or Win32Exception
                                          or UnauthorizedAccessException
                                          or SecurityException)
        {
            return null;
        }
        finally
        {
            if (processes is not null)
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
    }

    private static bool IsDirectoryOpusForeground()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "dopus", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or Win32Exception
                                          or UnauthorizedAccessException
                                          or SecurityException)
        {
            return false;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                process.WaitForExit();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ParsedDirectoryOpusPath(string FolderPath, int SortRank, bool IsForeground);
}
