using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace ListaryOpen.Infrastructure.Windows;

public sealed class ExplorerTracker : IQuickSwitchWindowProvider
{
    private readonly Func<IntPtr> _foregroundWindowProvider;
    private readonly IExplorerShellWindowsProvider _shellWindowsProvider;
    private string? _lastFolder;

    public ExplorerTracker()
        : this(NativeMethods.GetForegroundWindow, new ComExplorerShellWindowsProvider())
    {
    }

    internal ExplorerTracker(
        Func<IntPtr> foregroundWindowProvider,
        IExplorerShellWindowsProvider shellWindowsProvider)
    {
        _foregroundWindowProvider = foregroundWindowProvider ?? throw new ArgumentNullException(nameof(foregroundWindowProvider));
        _shellWindowsProvider = shellWindowsProvider ?? throw new ArgumentNullException(nameof(shellWindowsProvider));
    }

    public string? LastFolder => _lastFolder;

    public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
    {
        try
        {
            var foregroundHandle = _foregroundWindowProvider();
            var candidates = new List<QuickSwitchFolderCandidate>();
            var candidateIndexesByFolderPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var window in _shellWindowsProvider.EnumerateWindows())
            {
                if (string.IsNullOrWhiteSpace(window.FolderPath))
                {
                    continue;
                }

                var folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(window.FolderPath));
                if (!Directory.Exists(folderPath))
                {
                    continue;
                }

                var isForeground = window.Handle == foregroundHandle;
                var candidate = new QuickSwitchFolderCandidate(
                    folderPath,
                    "Explorer",
                    window.Handle,
                    isForeground);

                if (candidateIndexesByFolderPath.TryGetValue(folderPath, out var candidateIndex))
                {
                    if (isForeground && !candidates[candidateIndex].IsForeground)
                    {
                        candidates[candidateIndex] = candidate;
                    }

                    continue;
                }

                candidateIndexesByFolderPath.Add(folderPath, candidates.Count);
                candidates.Add(candidate);
            }

            return candidates
                .OrderByDescending(candidate => candidate.IsForeground)
                .ToArray();
        }
        catch (Exception exception) when (IsExpectedExplorerObservationException(exception))
        {
            return Array.Empty<QuickSwitchFolderCandidate>();
        }
    }

    public void ObserveForegroundExplorerFolder()
    {
        try
        {
            var foregroundHandle = _foregroundWindowProvider();
            if (foregroundHandle == IntPtr.Zero)
            {
                return;
            }

            foreach (var window in _shellWindowsProvider.EnumerateWindows())
            {
                if (window.Handle != foregroundHandle || string.IsNullOrWhiteSpace(window.FolderPath))
                {
                    continue;
                }

                if (Directory.Exists(window.FolderPath))
                {
                    _lastFolder = window.FolderPath;
                }

                return;
            }
        }
        catch (Exception exception) when (IsExpectedExplorerObservationException(exception))
        {
        }
    }

    public void ObserveFolderForTests(string folderPath)
    {
        if (Directory.Exists(folderPath))
        {
            _lastFolder = folderPath;
        }
    }

    private static bool IsExpectedExplorerObservationException(Exception exception)
    {
        return exception is ArgumentException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or UnauthorizedAccessException
            or SecurityException
            or COMException
            || string.Equals(
                exception.GetType().FullName,
                "Microsoft.CSharp.RuntimeBinder.RuntimeBinderException",
                StringComparison.Ordinal);
    }
}

internal sealed record ExplorerShellWindow(IntPtr Handle, string? FolderPath);

internal interface IExplorerShellWindowsProvider
{
    IEnumerable<ExplorerShellWindow> EnumerateWindows();
}

internal sealed class ComExplorerShellWindowsProvider : IExplorerShellWindowsProvider
{
    public IEnumerable<ExplorerShellWindow> EnumerateWindows()
    {
        var windows = new List<ExplorerShellWindow>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return windows;
        }

        object? shellApplication = null;
        object? shellWindows = null;
        try
        {
            shellApplication = Activator.CreateInstance(shellType);
            if (shellApplication is null)
            {
                return windows;
            }

            dynamic shell = shellApplication;
            shellWindows = shell.Windows();
            if (shellWindows is null)
            {
                return windows;
            }

            foreach (var shellWindow in (System.Collections.IEnumerable)shellWindows)
            {
                try
                {
                    dynamic window = shellWindow;
                    var handle = new IntPtr(Convert.ToInt64(window.HWND));
                    var folderPath = window.Document?.Folder?.Self?.Path as string;
                    windows.Add(new ExplorerShellWindow(handle, folderPath));
                }
                catch (Exception exception) when (IsExpectedShellWindowException(exception))
                {
                }
                finally
                {
                    ReleaseComObject(shellWindow);
                }
            }
        }
        finally
        {
            ReleaseComObject(shellWindows);
            ReleaseComObject(shellApplication);
        }

        return windows;
    }

    private static bool IsExpectedShellWindowException(Exception exception)
    {
        return exception is ArgumentException
            or FormatException
            or InvalidCastException
            or InvalidOperationException
            or NotSupportedException
            or OverflowException
            or UnauthorizedAccessException
            or SecurityException
            or COMException
            || string.Equals(
                exception.GetType().FullName,
                "Microsoft.CSharp.RuntimeBinder.RuntimeBinderException",
                StringComparison.Ordinal);
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }
}
