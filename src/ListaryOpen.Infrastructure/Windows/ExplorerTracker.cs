using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace ListaryOpen.Infrastructure.Windows;

public sealed class ExplorerTracker : IRefreshableQuickSwitchWindowProvider
{
    private readonly Func<IntPtr> _foregroundWindowProvider;
    private readonly IExplorerShellWindowsProvider _shellWindowsProvider;
    private readonly object _snapshotGate = new();
    private IReadOnlyList<ExplorerShellWindow>? _windowSnapshot;
    private string? _lastFolder;
    private string? _rememberedFolderPath;
    private long _lastFolderObservedUtcTicks;

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

    public void Refresh()
    {
        // Shell COM is exclusively owned by the observation STA. Callers consume the
        // latest immutable snapshot and request freshness through the scheduler.
    }

    public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
    {
        try
        {
            var foregroundHandle = _foregroundWindowProvider();
            var candidates = new List<QuickSwitchFolderCandidate>();
            var candidateIndexesByFolderPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var windows = GetWindowSnapshot() ?? Array.Empty<ExplorerShellWindow>();

            foreach (var window in windows)
            {
                if (string.IsNullOrWhiteSpace(window.FolderPath))
                {
                    continue;
                }

                var folderPath = window.FolderPath;

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

            var rememberedFolderPath = Volatile.Read(ref _rememberedFolderPath);
            return candidates
                .OrderByDescending(candidate => candidate.IsForeground)
                .ThenByDescending(candidate => IsRememberedFolderCandidate(candidate, rememberedFolderPath))
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
            var windows = ObserveWindows();
            if (foregroundHandle == IntPtr.Zero)
            {
                return;
            }

            foreach (var window in windows)
            {
                if (window.Handle != foregroundHandle || string.IsNullOrWhiteSpace(window.FolderPath))
                {
                    continue;
                }

                _lastFolder = window.FolderPath;
                Volatile.Write(ref _rememberedFolderPath, window.FolderPath);
                Interlocked.Exchange(ref _lastFolderObservedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

                return;
            }
        }
        catch (Exception exception) when (IsExpectedExplorerObservationException(exception))
        {
        }
    }

    public void ObserveFolderForTests(string folderPath)
    {
        var normalizedFolder = NormalizeExistingFolderPath(folderPath);
        if (normalizedFolder is not null)
        {
            _lastFolder = folderPath;
            Volatile.Write(ref _rememberedFolderPath, normalizedFolder);
            Interlocked.Exchange(ref _lastFolderObservedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
    }

    public string? GetRecentlyObservedFolder(TimeSpan maximumAge, DateTimeOffset? now = null)
    {
        if (maximumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        var ticks = Interlocked.Read(ref _lastFolderObservedUtcTicks);
        var folder = Volatile.Read(ref _rememberedFolderPath);
        if (ticks <= 0 || string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var observedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
        return (now ?? DateTimeOffset.UtcNow) - observedAt <= maximumAge && Directory.Exists(folder)
            ? folder
            : null;
    }

    private IReadOnlyList<ExplorerShellWindow> ObserveWindows()
    {
        var snapshot = _shellWindowsProvider
            .EnumerateWindows()
            .Select(window =>
            {
                var folderPath = NormalizeExistingFolderPath(window.FolderPath);
                return folderPath is null ? null : window with { FolderPath = folderPath };
            })
            .Where(window => window is not null)
            .Cast<ExplorerShellWindow>()
            .ToArray();
        lock (_snapshotGate)
        {
            _windowSnapshot = snapshot;
        }

        return snapshot;
    }

    private IReadOnlyList<ExplorerShellWindow>? GetWindowSnapshot()
    {
        lock (_snapshotGate)
        {
            return _windowSnapshot;
        }
    }

    private static bool IsRememberedFolderCandidate(
        QuickSwitchFolderCandidate candidate,
        string? rememberedFolderPath)
    {
        return !string.IsNullOrWhiteSpace(rememberedFolderPath) &&
            string.Equals(candidate.FolderPath, rememberedFolderPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeExistingFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or SecurityException)
        {
            return null;
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
                object? document = null;
                object? folder = null;
                object? folderSelf = null;
                try
                {
                    dynamic window = shellWindow;
                    var handle = new IntPtr(Convert.ToInt64(window.HWND));
                    var activeTabHandle = GetActiveExplorerTabHandle(handle);
                    if (activeTabHandle != IntPtr.Zero &&
                        TryGetShellBrowserWindowHandle(shellWindow, out var shellBrowserHandle) &&
                        !ShouldIncludeShellWindowForActiveTab(activeTabHandle, shellBrowserHandle))
                    {
                        continue;
                    }

                    document = window.Document;
                    if (document is null)
                    {
                        continue;
                    }

                    dynamic folderView = document;
                    folder = folderView.Folder;
                    if (folder is null)
                    {
                        continue;
                    }

                    dynamic shellFolder = folder;
                    folderSelf = shellFolder.Self;
                    var folderPath = folderSelf is null ? null : ((dynamic)folderSelf).Path as string;
                    windows.Add(new ExplorerShellWindow(handle, folderPath));
                }
                catch (Exception exception) when (IsExpectedShellWindowException(exception))
                {
                }
                finally
                {
                    ReleaseComObject(folderSelf);
                    ReleaseComObject(folder);
                    ReleaseComObject(document);
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

    internal static bool ShouldIncludeShellWindowForActiveTab(IntPtr activeTabHandle, IntPtr shellBrowserHandle)
    {
        return activeTabHandle == IntPtr.Zero ||
            shellBrowserHandle == IntPtr.Zero ||
            activeTabHandle == shellBrowserHandle;
    }

    internal static IntPtr GetActiveExplorerTabHandle(IntPtr explorerWindowHandle)
    {
        var activeTabHandle = IntPtr.Zero;
        NativeMethods.EnumChildWindows(
            explorerWindowHandle,
            (childHandle, _) =>
            {
                if (string.Equals(GetClassName(childHandle), "ShellTabWindowClass", StringComparison.Ordinal))
                {
                    activeTabHandle = childHandle;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);

        return activeTabHandle;
    }

    internal static bool TryGetShellBrowserWindowHandle(object shellWindow, out IntPtr shellBrowserHandle)
    {
        shellBrowserHandle = IntPtr.Zero;
        var shellBrowserInterfaceId = typeof(IShellBrowser).GUID;
        var unknown = IntPtr.Zero;
        var shellBrowserPointer = IntPtr.Zero;
        object? shellBrowserObject = null;

        try
        {
            unknown = Marshal.GetIUnknownForObject(shellWindow);
            if (Marshal.QueryInterface(unknown, ref shellBrowserInterfaceId, out shellBrowserPointer) != 0 ||
                shellBrowserPointer == IntPtr.Zero)
            {
                return false;
            }

            shellBrowserObject = Marshal.GetObjectForIUnknown(shellBrowserPointer);
            if (shellBrowserObject is not IShellBrowser shellBrowser)
            {
                return false;
            }

            shellBrowser.GetWindow(out shellBrowserHandle);
            return shellBrowserHandle != IntPtr.Zero;
        }
        catch (Exception exception) when (IsExpectedShellWindowException(exception))
        {
            shellBrowserHandle = IntPtr.Zero;
            return false;
        }
        finally
        {
            ReleaseComObject(shellBrowserObject);
            if (shellBrowserPointer != IntPtr.Zero)
            {
                Marshal.Release(shellBrowserPointer);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
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

    private static string GetClassName(IntPtr handle)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(handle, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr windowHandle);
    }
}
