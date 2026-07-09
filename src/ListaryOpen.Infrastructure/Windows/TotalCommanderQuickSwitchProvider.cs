using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace ListaryOpen.Infrastructure.Windows;

public sealed class TotalCommanderQuickSwitchProvider : IQuickSwitchWindowProvider
{
    private const string SourceName = "Total Commander";
    private const string MainWindowClassName = "TTOTAL_CMD";
    private const uint SendMessageTimeoutAbortIfHung = 0x0002;
    private const uint WmGetText = 0x000D;
    private const uint ChildTextTimeoutMilliseconds = 50;
    private static readonly string[] ProcessNames = { "TOTALCMD64", "TOTALCMD" };
    private readonly Func<IReadOnlyList<TotalCommanderWindowSnapshot>> _snapshotReader;

    public TotalCommanderQuickSwitchProvider()
        : this(ReadWindowSnapshots)
    {
    }

    internal TotalCommanderQuickSwitchProvider(Func<IReadOnlyList<TotalCommanderWindowSnapshot>> snapshotReader)
    {
        _snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
    }

    public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
    {
        try
        {
            var candidates = new List<TotalCommanderPanelCandidate>();
            var candidateIndexesByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var snapshot in _snapshotReader())
            {
                foreach (var childText in snapshot.ChildTexts)
                {
                    var panelPath = ParsePanelPath(childText);
                    if (panelPath is null)
                    {
                        continue;
                    }

                    var candidate = new QuickSwitchFolderCandidate(
                        panelPath.FolderPath,
                        SourceName,
                        snapshot.WindowHandle,
                        snapshot.IsForeground);
                    var panelCandidate = new TotalCommanderPanelCandidate(candidate, panelPath.SortRank);

                    if (candidateIndexesByPath.TryGetValue(panelPath.FolderPath, out var existingIndex))
                    {
                        if (IsBetterPanelCandidate(panelCandidate, candidates[existingIndex]))
                        {
                            candidates[existingIndex] = panelCandidate;
                        }

                        continue;
                    }

                    candidateIndexesByPath.Add(panelPath.FolderPath, candidates.Count);
                    candidates.Add(panelCandidate);
                }
            }

            return candidates
                .OrderByDescending(candidate => candidate.Candidate.IsForeground)
                .ThenByDescending(candidate => candidate.SortRank)
                .Select(candidate => candidate.Candidate)
                .ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or PathTooLongException
                                          or UnauthorizedAccessException
                                          or SecurityException
                                          or Win32Exception)
        {
            return Array.Empty<QuickSwitchFolderCandidate>();
        }
    }

    internal static string? ParsePanelPathText(string? text)
    {
        return ParsePanelPath(text)?.FolderPath;
    }

    private static TotalCommanderPanelPath? ParsePanelPath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var isActivePanel = value.EndsWith('>');
        if (isActivePanel)
        {
            value = value[..^1].TrimEnd();
        }

        var isPanelWildcard = value.EndsWith("*.*", StringComparison.Ordinal);
        if (!isActivePanel && !isPanelWildcard)
        {
            return null;
        }

        if (isPanelWildcard)
        {
            value = value[..^3].TrimEnd();
        }

        if (!Path.IsPathFullyQualified(value))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(value);
            var normalizedPath = NormalizeFolderPath(fullPath);
            return Directory.Exists(normalizedPath)
                ? new TotalCommanderPanelPath(normalizedPath, isActivePanel ? 1 : 0)
                : null;
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

    private static bool IsBetterPanelCandidate(
        TotalCommanderPanelCandidate candidate,
        TotalCommanderPanelCandidate existingCandidate) =>
        candidate.Candidate.IsForeground && !existingCandidate.Candidate.IsForeground
        || candidate.Candidate.IsForeground == existingCandidate.Candidate.IsForeground
        && candidate.SortRank > existingCandidate.SortRank;

    private static IReadOnlyList<TotalCommanderWindowSnapshot> ReadWindowSnapshots()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        var snapshots = new List<TotalCommanderWindowSnapshot>();

        NativeMethods.EnumWindows(
            (windowHandle, _) =>
            {
                if (IsTotalCommanderMainWindow(windowHandle))
                {
                    snapshots.Add(new TotalCommanderWindowSnapshot(
                        windowHandle,
                        ReadChildWindowTexts(windowHandle),
                        windowHandle == foregroundWindow));
                }

                return true;
            },
            IntPtr.Zero);

        return snapshots;
    }

    private static bool IsTotalCommanderMainWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindowVisible(windowHandle))
        {
            return false;
        }

        if (!string.Equals(ReadClassName(windowHandle), MainWindowClassName, StringComparison.Ordinal))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
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

    private static IReadOnlyList<string> ReadChildWindowTexts(IntPtr windowHandle)
    {
        var texts = new List<string>();

        NativeMethods.EnumChildWindows(
            windowHandle,
            (childHandle, _) =>
            {
                var text = ReadWindowText(childHandle);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text);
                }

                return true;
            },
            IntPtr.Zero);

        return texts;
    }

    private static string ReadClassName(IntPtr windowHandle)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(windowHandle, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static string ReadWindowText(IntPtr windowHandle)
    {
        var buffer = new char[1024];
        var sendResult = NativeMethods.SendMessageTimeout(
            windowHandle,
            WmGetText,
            new UIntPtr((uint)buffer.Length),
            buffer,
            SendMessageTimeoutAbortIfHung,
            ChildTextTimeoutMilliseconds,
            out var messageResult);
        if (sendResult == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = (int)Math.Min(messageResult.ToUInt64(), (ulong)(buffer.Length - 1));
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static string NormalizeFolderPath(string fullPath)
    {
        fullPath = NormalizeDriveLetter(fullPath);
        var root = Path.GetPathRoot(fullPath);
        return !string.IsNullOrEmpty(root) &&
               string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? root
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static string NormalizeDriveLetter(string path)
    {
        return path.Length >= 2 && path[1] == ':' && char.IsAsciiLetterLower(path[0])
            ? char.ToUpperInvariant(path[0]) + path[1..]
            : path;
    }
}

internal sealed record TotalCommanderWindowSnapshot(
    IntPtr WindowHandle,
    IReadOnlyList<string> ChildTexts,
    bool IsForeground);

internal sealed record TotalCommanderPanelPath(string FolderPath, int SortRank);

internal sealed record TotalCommanderPanelCandidate(QuickSwitchFolderCandidate Candidate, int SortRank);
