using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace ListaryOpen.Infrastructure.Windows;

public interface IExplorerSelectionService
{
    bool TrySelectItem(IntPtr explorerWindow, string currentFolder, string itemPath);

    void QueueSelectItem(IntPtr explorerWindow, string currentFolder, string itemPath) =>
        TrySelectItem(explorerWindow, currentFolder, itemPath);

    void CancelPending()
    {
    }
}

public sealed class ExplorerSelectionService : IExplorerSelectionService, IDisposable
{
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(75);
    private readonly IExplorerShellSelectionProvider _selectionProvider;
    private readonly TimeSpan _debounceDelay;
    private readonly object _pendingGate = new();
    private readonly AutoResetEvent _pendingSignal = new(initialState: false);
    private readonly Thread _selectionThread;
    private SelectionRequest? _pendingSelection;
    private bool _selectionThreadStarted;
    private bool _disposed;
    private int _selectionGeneration;

    public ExplorerSelectionService()
        : this(new ComExplorerShellSelectionProvider(), DefaultDebounceDelay)
    {
    }

    internal ExplorerSelectionService(IExplorerShellSelectionProvider selectionProvider)
        : this(selectionProvider, DefaultDebounceDelay)
    {
    }

    internal ExplorerSelectionService(
        IExplorerShellSelectionProvider selectionProvider,
        TimeSpan debounceDelay)
    {
        _selectionProvider = selectionProvider ?? throw new ArgumentNullException(nameof(selectionProvider));
        if (debounceDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceDelay));
        }

        _debounceDelay = debounceDelay;
        _selectionThread = new Thread(ProcessQueuedSelections)
        {
            IsBackground = true,
            Name = "ListaryOpen Explorer selection"
        };
        _selectionThread.SetApartmentState(ApartmentState.STA);
    }

    public bool TrySelectItem(IntPtr explorerWindow, string currentFolder, string itemPath)
    {
        if (explorerWindow == IntPtr.Zero ||
            !TryGetDirectChildName(currentFolder, itemPath, out var childName, out var normalizedFolder))
        {
            return false;
        }

        try
        {
            return _selectionProvider.TrySelectItem(explorerWindow, normalizedFolder, childName);
        }
        catch (Exception exception) when (IsExpectedSelectionException(exception))
        {
            return false;
        }
    }

    public void QueueSelectItem(IntPtr explorerWindow, string currentFolder, string itemPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (explorerWindow == IntPtr.Zero ||
            !TryGetDirectChildName(currentFolder, itemPath, out var childName, out var normalizedFolder))
        {
            return;
        }

        lock (_pendingGate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingSelection = new SelectionRequest(
                explorerWindow,
                normalizedFolder,
                childName,
                _selectionGeneration);
            if (!_selectionThreadStarted)
            {
                _selectionThreadStarted = true;
                _selectionThread.Start();
            }
        }

        _pendingSignal.Set();
    }

    public void CancelPending()
    {
        lock (_pendingGate)
        {
            if (_disposed)
            {
                return;
            }

            _selectionGeneration++;
            _pendingSelection = null;
        }

        _pendingSignal.Set();
    }

    public void Dispose()
    {
        lock (_pendingGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingSelection = null;
        }

        if (!_selectionThreadStarted)
        {
            if (_selectionProvider is IDisposable disposableProvider)
            {
                disposableProvider.Dispose();
            }

            _pendingSignal.Dispose();
            return;
        }

        _pendingSignal.Set();
        if (Thread.CurrentThread != _selectionThread &&
            _selectionThread.Join(TimeSpan.FromSeconds(2)))
        {
            _pendingSignal.Dispose();
        }
    }

    private void ProcessQueuedSelections()
    {
        try
        {
            while (true)
            {
                _pendingSignal.WaitOne();
                if (IsDisposed())
                {
                    return;
                }

                while (_pendingSignal.WaitOne(_debounceDelay))
                {
                    if (IsDisposed())
                    {
                        return;
                    }
                }

                SelectionRequest? request;
                lock (_pendingGate)
                {
                    request = _pendingSelection;
                    _pendingSelection = null;
                }

                if (request is not null)
                {
                    lock (_pendingGate)
                    {
                        if (request.Generation != _selectionGeneration || _disposed)
                        {
                            continue;
                        }
                    }

                    TrySelectPreparedItem(request);
                }
            }
        }
        finally
        {
            if (_selectionProvider is IDisposable disposableProvider)
            {
                disposableProvider.Dispose();
            }
        }
    }

    private bool IsDisposed()
    {
        lock (_pendingGate)
        {
            return _disposed;
        }
    }

    private bool TrySelectPreparedItem(SelectionRequest request)
    {
        try
        {
            return _selectionProvider.TrySelectItem(
                request.ExplorerWindow,
                request.CurrentFolder,
                request.ChildName);
        }
        catch (Exception exception) when (IsExpectedSelectionException(exception))
        {
            return false;
        }
    }

    internal static bool TryGetDirectChildName(
        string currentFolder,
        string itemPath,
        out string childName,
        out string normalizedFolder)
    {
        childName = string.Empty;
        normalizedFolder = string.Empty;
        if (string.IsNullOrWhiteSpace(currentFolder) || string.IsNullOrWhiteSpace(itemPath))
        {
            return false;
        }

        try
        {
            normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentFolder));
            var normalizedItem = Path.GetFullPath(itemPath);
            var parentFolder = Path.GetDirectoryName(normalizedItem);
            childName = Path.GetFileName(normalizedItem);
            return !string.IsNullOrWhiteSpace(parentFolder)
                && !string.IsNullOrWhiteSpace(childName)
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(parentFolder),
                    normalizedFolder,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (IsExpectedSelectionException(exception))
        {
            childName = string.Empty;
            normalizedFolder = string.Empty;
            return false;
        }
    }

    private static bool IsExpectedSelectionException(Exception exception)
    {
        return exception is ArgumentException
            or FormatException
            or IOException
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

    private sealed record SelectionRequest(
        IntPtr ExplorerWindow,
        string CurrentFolder,
        string ChildName,
        int Generation);
}

internal interface IExplorerShellSelectionProvider
{
    bool TrySelectItem(IntPtr explorerWindow, string currentFolder, string childName);
}

internal sealed class ComExplorerShellSelectionProvider : IExplorerShellSelectionProvider, IDisposable
{
    private const int SelectItemFlags = 0x01 | 0x04 | 0x08 | 0x10;
    private readonly Type? _shellType = Type.GetTypeFromProgID("Shell.Application");
    private object? _shellApplication;
    private object? _cachedDocument;
    private object? _cachedFolder;
    private IntPtr _cachedExplorerWindow;
    private IntPtr _cachedActiveTabHandle;
    private string? _cachedCurrentFolder;

    public bool TrySelectItem(IntPtr explorerWindow, string currentFolder, string childName)
    {
        if (_shellType is null)
        {
            return false;
        }

        object? shellWindows = null;
        try
        {
            var activeTabHandle = ComExplorerShellWindowsProvider.GetActiveExplorerTabHandle(explorerWindow);
            if (_cachedExplorerWindow == explorerWindow &&
                _cachedActiveTabHandle == activeTabHandle &&
                string.Equals(_cachedCurrentFolder, currentFolder, StringComparison.OrdinalIgnoreCase) &&
                _cachedDocument is not null &&
                _cachedFolder is not null)
            {
                try
                {
                    return TrySelectFromFolder(_cachedDocument, _cachedFolder, childName);
                }
                catch (Exception exception) when (IsExpectedShellException(exception))
                {
                    ClearCachedFolderView();
                }
            }
            else
            {
                ClearCachedFolderView();
            }

            _shellApplication ??= Activator.CreateInstance(_shellType);
            if (_shellApplication is null)
            {
                return false;
            }

            dynamic shell = _shellApplication;
            shellWindows = shell.Windows();
            if (shellWindows is null)
            {
                return false;
            }

            foreach (var shellWindow in (System.Collections.IEnumerable)shellWindows)
            {
                try
                {
                    dynamic window = shellWindow;
                    if (new IntPtr(Convert.ToInt64(window.HWND)) != explorerWindow)
                    {
                        continue;
                    }

                    if (activeTabHandle != IntPtr.Zero &&
                        ComExplorerShellWindowsProvider.TryGetShellBrowserWindowHandle(
                            shellWindow,
                            out var shellBrowserHandle) &&
                        !ComExplorerShellWindowsProvider.ShouldIncludeShellWindowForActiveTab(
                            activeTabHandle,
                            shellBrowserHandle))
                    {
                        continue;
                    }

                    object? document = window.Document;
                    object? folder = null;
                    object? folderSelf = null;
                    object? item = null;
                    try
                    {
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
                        if (string.IsNullOrWhiteSpace(folderPath) ||
                            !string.Equals(
                                Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath)),
                                currentFolder,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        item = shellFolder.ParseName(childName);
                        if (item is null)
                        {
                            return false;
                        }

                        folderView.SelectItem(item, SelectItemFlags);
                        _cachedExplorerWindow = explorerWindow;
                        _cachedActiveTabHandle = activeTabHandle;
                        _cachedCurrentFolder = currentFolder;
                        _cachedDocument = document;
                        _cachedFolder = folder;
                        document = null;
                        folder = null;
                        return true;
                    }
                    finally
                    {
                        ReleaseComObject(item);
                        ReleaseComObject(folderSelf);
                        ReleaseComObject(folder);
                        ReleaseComObject(document);
                    }
                }
                catch (Exception exception) when (IsExpectedShellException(exception))
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
        }

        return false;
    }

    public void Dispose()
    {
        ClearCachedFolderView();
        ReleaseComObject(_shellApplication);
        _shellApplication = null;
    }

    private static bool TrySelectFromFolder(object document, object folder, string childName)
    {
        object? item = null;
        try
        {
            dynamic shellFolder = folder;
            item = shellFolder.ParseName(childName);
            if (item is null)
            {
                return false;
            }

            dynamic folderView = document;
            folderView.SelectItem(item, SelectItemFlags);
            return true;
        }
        finally
        {
            ReleaseComObject(item);
        }
    }

    private void ClearCachedFolderView()
    {
        ReleaseComObject(_cachedFolder);
        ReleaseComObject(_cachedDocument);
        _cachedFolder = null;
        _cachedDocument = null;
        _cachedExplorerWindow = IntPtr.Zero;
        _cachedActiveTabHandle = IntPtr.Zero;
        _cachedCurrentFolder = null;
    }

    private static bool IsExpectedShellException(Exception exception)
    {
        return exception is ArgumentException
            or FormatException
            or IOException
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
