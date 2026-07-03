using ListaryOpen.App.Tray;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Indexing;
using ListaryOpen.Infrastructure.Search;
using ListaryOpen.Infrastructure.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ListaryOpen.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly BackgroundIndexingTaskTracker _indexingTasks = new();
    private readonly SemaphoreSlim _indexingRunLock = new(1, 1);

    private DialogBridge? _dialogBridge;
    private FallbackIndexProvider? _fallbackIndexProvider;
    private ExplorerTracker? _explorerTracker;
    private HotkeyService? _hotkeyService;
    private IndexingCoordinator? _indexingCoordinator;
    private NtfsIndexProvider? _ntfsIndexProvider;
    private SearchPanel? _searchPanel;
    private SqliteSearchIndex? _searchIndex;
    private SettingsViewModel? _settingsViewModel;
    private TrayController? _trayController;
    private VolumeIndexer? _volumeIndexer;
    private WindowsDialogAutomation? _dialogAutomation;

    internal static bool IsShuttingDown =>
        Current?.Dispatcher.HasShutdownStarted == true ||
        Current?.Dispatcher.HasShutdownFinished == true;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            if (!await InitializeApplicationServicesAsync().ConfigureAwait(false))
            {
                await InvokeOnDispatcherAsync(Dispatcher, () => Shutdown(1)).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            await InvokeOnDispatcherAsync(Dispatcher, () =>
            {
                MessageBox.Show(
                    "ListaryOpen could not start.",
                    "ListaryOpen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }).ConfigureAwait(false);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownCancellation.Cancel();

        if (_indexingCoordinator is not null)
        {
            _indexingCoordinator.StatusChanged -= OnIndexingStatusChanged;
        }

        WaitForIndexingTasks();

        if (_hotkeyService is not null)
        {
            _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
            _hotkeyService.Dispose();
        }

        _trayController?.Dispose();
        DisposeSearchIndex();
        _shutdownCancellation.Dispose();

        base.OnExit(e);
    }

    private async Task<bool> InitializeApplicationServicesAsync()
    {
        _searchIndex = await SqliteSearchIndex.OpenAsync(CreateIndexDatabasePath(), CancellationToken.None)
            .ConfigureAwait(false);

        return await InvokeOnDispatcherAsync(
                Dispatcher,
                () => InitializeApplicationServices(_searchIndex))
            .ConfigureAwait(false);
    }

    private bool InitializeApplicationServices(SqliteSearchIndex searchIndex)
    {
        _fallbackIndexProvider = new FallbackIndexProvider();
        _ntfsIndexProvider = new NtfsIndexProvider(new ElevatedIndexerClient());
        _volumeIndexer = new VolumeIndexer(new IIndexProvider[]
        {
            _ntfsIndexProvider,
            _fallbackIndexProvider
        });
        _indexingCoordinator = new IndexingCoordinator(searchIndex, _volumeIndexer, _fallbackIndexProvider);
        _indexingCoordinator.StatusChanged += OnIndexingStatusChanged;

        _dialogAutomation = new WindowsDialogAutomation();
        _dialogBridge = new DialogBridge(_dialogAutomation);

        _settingsViewModel = new SettingsViewModel(AppSettings.Defaults());
        var settingsWindow = new MainWindow(_settingsViewModel);
        _explorerTracker = new ExplorerTracker();
        _searchPanel = new SearchPanel(new SearchPanelViewModel(searchIndex));
        _trayController = new TrayController(settingsWindow, RequestReindex);
        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        var hotkeyStartupDecision = CreateHotkeyStartupDecision(_hotkeyService.RegisterDefaults());
        if (hotkeyStartupDecision.Message is not null)
        {
            MessageBox.Show(
                hotkeyStartupDecision.Message,
                "ListaryOpen Hotkeys",
                MessageBoxButton.OK,
                hotkeyStartupDecision.Image);
        }

        if (!hotkeyStartupDecision.ShouldContinue)
        {
            return false;
        }

        MainWindow = settingsWindow;
        settingsWindow.Show();
        StartBackgroundIndexing();
        return true;
    }

    internal static Task InvokeOnDispatcherAsync(Dispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        return InvokeOnDispatcherAsync(
            dispatcher,
            () =>
            {
                action();
                return true;
            });
    }

    internal static Task<T> InvokeOnDispatcherAsync<T>(Dispatcher dispatcher, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    internal static HotkeyStartupDecision CreateHotkeyStartupDecision(HotkeyRegistrationResult registrationResult)
    {
        ArgumentNullException.ThrowIfNull(registrationResult);

        if (registrationResult.AllRegistered)
        {
            return new HotkeyStartupDecision(true, null, MessageBoxImage.None);
        }

        var failedHotkeys = JoinHotkeyNames(registrationResult.FailedHotkeys);
        if (!registrationResult.AnyRegistered)
        {
            return new HotkeyStartupDecision(
                false,
                $"ListaryOpen could not register any global hotkeys: {failedHotkeys}. They may already be in use by another application. ListaryOpen will exit.",
                MessageBoxImage.Error);
        }

        var registeredHotkeys = JoinHotkeyNames(registrationResult.RegisteredHotkeys);
        return new HotkeyStartupDecision(
            true,
            $"ListaryOpen could not register these global hotkeys: {failedHotkeys}. Registered hotkeys: {registeredHotkeys}. The app will keep running with the registered hotkeys.",
            MessageBoxImage.Warning);
    }

    internal static IReadOnlyList<IndexRoot> CreateIndexRoots(IEnumerable<string> rootPaths)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);

        var roots = new List<IndexRoot>();
        foreach (var rootPath in rootPaths)
        {
            try
            {
                roots.Add(new IndexRoot(rootPath));
            }
            catch (Exception exception) when (IsInvalidIndexRootException(exception))
            {
                Trace.TraceWarning("Skipping invalid index root '{0}': {1}", rootPath, exception.Message);
            }
        }

        return roots;
    }

    private static string JoinHotkeyNames(IEnumerable<HotkeyRegistration> hotkeys)
    {
        return string.Join(", ", hotkeys.Select(hotkey => hotkey.Name));
    }

    private static string CreateIndexDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The local application data folder is unavailable.");
        }

        var appDataDirectory = Path.Combine(localAppData, "ListaryOpen");
        Directory.CreateDirectory(appDataDirectory);
        return Path.Combine(appDataDirectory, "index.db");
    }

    private void RequestReindex()
    {
        StartBackgroundIndexing();
    }

    private void StartBackgroundIndexing()
    {
        BackgroundIndexingTaskStarter.Start(_indexingTasks, RunInitialIndexAsync, _shutdownCancellation.Token);
    }

    private async Task RunInitialIndexAsync(CancellationToken cancellationToken)
    {
        var lockTaken = false;
        try
        {
            lockTaken = await _indexingRunLock.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!lockTaken)
            {
                Trace.TraceInformation("Indexing is already running; reindex request skipped.");
                return;
            }

            var coordinator = _indexingCoordinator;
            if (coordinator is null)
            {
                return;
            }

            var rootPaths = _settingsViewModel?.Settings.IndexedRoots ?? AppSettings.Defaults().IndexedRoots;
            var roots = CreateIndexRoots(rootPaths);
            await coordinator.IndexRootsAsync(roots, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
        finally
        {
            if (lockTaken)
            {
                _indexingRunLock.Release();
            }
        }
    }

    private void OnIndexingStatusChanged(object? sender, IndexingStatus status)
    {
        if (IsShuttingDown)
        {
            return;
        }

        _ = UpdateIndexingStatusAsync(status);
    }

    private async Task UpdateIndexingStatusAsync(IndexingStatus status)
    {
        try
        {
            if (IsShuttingDown)
            {
                return;
            }

            await InvokeOnDispatcherAsync(
                    Dispatcher,
                    () =>
                    {
                        if (!IsShuttingDown)
                        {
                            _settingsViewModel?.UpdateIndexingStatus(status);
                        }
                    })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TaskCanceledException)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private void WaitForIndexingTasks()
    {
        try
        {
            _indexingTasks.WaitForCompletionAsync().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private void DisposeSearchIndex()
    {
        try
        {
            _searchIndex?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private static bool IsInvalidIndexRootException(Exception exception)
    {
        return exception is ArgumentException
            or NotSupportedException
            or PathTooLongException;
    }

    private void OnHotkeyPressed(object? sender, string name)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => HandleHotkeyPressed(name)));
            return;
        }

        HandleHotkeyPressed(name);
    }

    private void HandleHotkeyPressed(string name)
    {
        if (IsSearchHotkey(name))
        {
            _searchPanel?.ActivateSearch();
            return;
        }

        if (IsDialogHotkey(name))
        {
            HandleDialogHotkey();
        }
    }

    private void HandleDialogHotkey()
    {
        var lastFolder = GetExistingTrackedFolder();
        if (lastFolder is not null)
        {
            _ = JumpDialogToFolderAsync(lastFolder);
        }

        _searchPanel?.ActivateFolderSearch(lastFolder);
    }

    private string? GetExistingTrackedFolder()
    {
        var lastFolder = _explorerTracker?.LastFolder;
        return !string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder)
            ? lastFolder
            : null;
    }

    private async Task JumpDialogToFolderAsync(string folderPath)
    {
        try
        {
            var dialogBridge = _dialogBridge;
            if (dialogBridge is null)
            {
                return;
            }

            var result = await dialogBridge.JumpToFolderAsync(folderPath, CancellationToken.None);
            if (result.Status != DialogJumpStatus.Success)
            {
                Trace.TraceInformation("Dialog folder jump did not complete: {0}: {1}", result.Status, result.Message);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private static bool IsSearchHotkey(string name)
    {
        return string.Equals(name, "Search", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Ctrl+Space", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDialogHotkey(string name)
    {
        return string.Equals(name, "Dialog", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Ctrl+G", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record HotkeyStartupDecision(bool ShouldContinue, string? Message, MessageBoxImage Image);

internal static class BackgroundIndexingTaskStarter
{
    public static void Start(
        BackgroundIndexingTaskTracker tracker,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(work);

        tracker.Track(Task.Run(() => work(cancellationToken)));
    }
}

internal sealed class BackgroundIndexingTaskTracker
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = new();

    public void Track(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_gate)
        {
            _tasks.Add(task);
        }

        _ = RemoveWhenCompleteAsync(task);
    }

    public async Task WaitForCompletionAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (_gate)
            {
                tasks = _tasks.ToArray();
            }

            if (tasks.Length == 0)
            {
                return;
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private async Task RemoveWhenCompleteAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            lock (_gate)
            {
                _tasks.Remove(task);
            }
        }
    }
}
