using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace ListaryOpen.Infrastructure.Windows;

public interface IExplorerNavigationService
{
    Task<bool> NavigateToFolderAsync(
        IntPtr explorerWindow,
        string folderPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Navigates the active tab of an existing Explorer window on a dedicated STA
/// thread. Calling Process.Start for a directory would create a second window.
/// </summary>
public sealed class ExplorerNavigationService : IExplorerNavigationService
{
    private readonly IExplorerShellNavigationProvider _navigationProvider;

    public ExplorerNavigationService()
        : this(new ComExplorerShellNavigationProvider())
    {
    }

    internal ExplorerNavigationService(IExplorerShellNavigationProvider navigationProvider)
    {
        _navigationProvider = navigationProvider ?? throw new ArgumentNullException(nameof(navigationProvider));
    }

    public Task<bool> NavigateToFolderAsync(
        IntPtr explorerWindow,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        if (explorerWindow == IntPtr.Zero || string.IsNullOrWhiteSpace(folderPath))
        {
            return Task.FromResult(false);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var navigationThread = new Thread(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                var normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
                completion.TrySetResult(
                    Directory.Exists(normalizedFolder) &&
                    _navigationProvider.TryNavigateToFolder(explorerWindow, normalizedFolder));
            }
            catch (Exception exception) when (IsExpectedNavigationException(exception))
            {
                completion.TrySetResult(false);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "ListaryOpen Explorer navigation"
        };
        navigationThread.SetApartmentState(ApartmentState.STA);
        navigationThread.Start();
        return completion.Task;
    }

    private static bool IsExpectedNavigationException(Exception exception) =>
        exception is ArgumentException
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

internal interface IExplorerShellNavigationProvider
{
    bool TryNavigateToFolder(IntPtr explorerWindow, string folderPath);
}

internal sealed class ComExplorerShellNavigationProvider : IExplorerShellNavigationProvider
{
    public bool TryNavigateToFolder(IntPtr explorerWindow, string folderPath)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return false;
        }

        object? shellApplication = null;
        object? shellWindows = null;
        try
        {
            shellApplication = Activator.CreateInstance(shellType);
            if (shellApplication is null)
            {
                return false;
            }

            dynamic shell = shellApplication;
            shellWindows = shell.Windows();
            if (shellWindows is null)
            {
                return false;
            }

            var activeTabHandle = ComExplorerShellWindowsProvider.GetActiveExplorerTabHandle(explorerWindow);
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

                    window.Navigate2(folderPath);
                    return true;
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
            ReleaseComObject(shellApplication);
        }

        return false;
    }

    private static bool IsExpectedShellException(Exception exception) =>
        exception is ArgumentException
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

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }
}
