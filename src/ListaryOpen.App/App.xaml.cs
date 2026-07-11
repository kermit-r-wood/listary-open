using ListaryOpen.App.Tray;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.AppData;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Hooks;
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
    private readonly IndexingRunCancellationManager _indexingCancellation = new();
    private readonly BackgroundIndexingTaskTracker _indexingTasks = new();
    private readonly SemaphoreSlim _indexingRunLock = new(1, 1);

    private DialogBridge? _dialogBridge;
    private ElevatedIndexerClient? _elevatedIndexerClient;
    private ExplorerObservationScheduler? _explorerObservationScheduler;
    private FallbackIndexProvider? _fallbackIndexProvider;
    private IHookQuickSwitchBridge? _hookQuickSwitchBridge;
    private ExplorerTracker? _explorerTracker;
    private HotkeyService? _hotkeyService;
    private IndexingCoordinator? _indexingCoordinator;
    private NtfsIndexProvider? _ntfsIndexProvider;
    private IQuickSwitchWindowProvider? _quickSwitchWindowProvider;
    private SearchPanel? _searchPanel;
    private SqliteSearchIndex? _searchIndex;
    private SettingsViewModel? _settingsViewModel;
    private SingleInstanceGuard? _singleInstanceGuard;
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
            _singleInstanceGuard = SingleInstanceGuard.TryAcquire();
            if (!_singleInstanceGuard.IsOwner)
            {
                MessageBox.Show(
                    "ListaryOpen is already running.",
                    "ListaryOpen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

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
                    exception is AppStartupException startupException
                        ? startupException.UserMessage
                        : "ListaryOpen could not start.",
                    "ListaryOpen",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }).ConfigureAwait(false);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _indexingCancellation.CancelActive();
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

        if (_hookQuickSwitchBridge is not null)
        {
            _hookQuickSwitchBridge.StatusChanged -= OnHookQuickSwitchStatusChanged;
            _hookQuickSwitchBridge.Dispose();
        }

        _explorerObservationScheduler?.Dispose();
        _trayController?.Dispose();
        DisposeSearchIndex();
        _indexingCancellation.Dispose();
        _singleInstanceGuard?.Dispose();
        _shutdownCancellation.Dispose();

        base.OnExit(e);
    }

    private async Task<bool> InitializeApplicationServicesAsync()
    {
        var appDataPaths = CreateAppDataPaths();
        try
        {
            appDataPaths.EnsureDirectories();
            _searchIndex = await SqliteSearchIndex.OpenAsync(appDataPaths.IndexDatabasePath, CancellationToken.None)
                .ConfigureAwait(false);
            await ImportLegacyUsageIfAvailableAsync(_searchIndex, appDataPaths, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAppDataStartupException(exception))
        {
            throw new AppStartupException(CreateAppDataStartupFailureMessage(appDataPaths, exception), exception);
        }

        return await InvokeOnDispatcherAsync(
                Dispatcher,
                () => InitializeApplicationServices(_searchIndex))
            .ConfigureAwait(false);
    }

    private static async Task ImportLegacyUsageIfAvailableAsync(
        SqliteSearchIndex searchIndex,
        AppDataPaths appDataPaths,
        CancellationToken cancellationToken)
    {
        var legacyDbPath = AppDataPaths.CreateLegacyLocalAppDataIndexDatabasePath();
        if (string.Equals(
                Path.GetFullPath(legacyDbPath),
                Path.GetFullPath(appDataPaths.IndexDatabasePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await searchIndex.ImportUsageFromAsync(legacyDbPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                         or UnauthorizedAccessException
                                         or InvalidOperationException
                                         or Microsoft.Data.Sqlite.SqliteException)
        {
            Trace.TraceWarning("Legacy usage import skipped for '{0}': {1}", legacyDbPath, exception.Message);
        }
    }

    private bool InitializeApplicationServices(SqliteSearchIndex searchIndex)
    {
        _fallbackIndexProvider = new FallbackIndexProvider();
        _elevatedIndexerClient = new ElevatedIndexerClient();
        var ntfsFastIndexingEnabled = EnableNtfsFastIndexingOnStartup(_elevatedIndexerClient);
        _ntfsIndexProvider = new NtfsIndexProvider(_elevatedIndexerClient);
        _volumeIndexer = new VolumeIndexer(new IIndexProvider[]
        {
            _ntfsIndexProvider,
            _fallbackIndexProvider
        });
        _indexingCoordinator = new IndexingCoordinator(searchIndex, _volumeIndexer, _fallbackIndexProvider);
        _indexingCoordinator.StatusChanged += OnIndexingStatusChanged;

        _dialogAutomation = new WindowsDialogAutomation();
        _hookQuickSwitchBridge = HookQuickSwitchBridgeFactory.CreateDefault();
        _hookQuickSwitchBridge.StatusChanged += OnHookQuickSwitchStatusChanged;
        _dialogBridge = new DialogBridge(
            _dialogAutomation,
            _hookQuickSwitchBridge,
            CreateDefaultCustomDialogAdapters());

        _settingsViewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            EnableNtfsFastIndexing,
            EnableHookQuickSwitch,
            ntfsFastIndexingEnabled);
        _settingsViewModel.UpdateHookQuickSwitchStatus(_hookQuickSwitchBridge.Status);
        var settingsWindow = new MainWindow(_settingsViewModel);
        _explorerTracker = new ExplorerTracker();
        _quickSwitchWindowProvider = CreateDefaultQuickSwitchWindowProvider(_explorerTracker);
        _explorerObservationScheduler = StartPeriodicExplorerObservation(
            _explorerTracker,
            () => new DispatcherExplorerObservationTimer());
        _searchPanel = new SearchPanel(new SearchPanelViewModel(searchIndex, JumpDialogToFolderAsync));
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
        if (ShouldShowSettingsOnStartup(hasBlockingStartupMessage: false))
        {
            settingsWindow.Show();
        }

        StartHookQuickSwitchEnablementOnStartup(_hookQuickSwitchBridge, EnableHookQuickSwitchAsync);
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

    internal static SearchHotkeyPanelAction GetSearchHotkeyPanelAction(bool panelIsVisible)
    {
        return panelIsVisible ? SearchHotkeyPanelAction.Hide : SearchHotkeyPanelAction.Activate;
    }

    internal static bool ShouldShowSettingsOnStartup(bool hasBlockingStartupMessage)
    {
        return hasBlockingStartupMessage;
    }

    internal static IQuickSwitchWindowProvider CreateDefaultQuickSwitchWindowProvider(ExplorerTracker explorerTracker)
    {
        ArgumentNullException.ThrowIfNull(explorerTracker);

        return new CompositeQuickSwitchWindowProvider(
            new IQuickSwitchWindowProvider[]
            {
                explorerTracker,
                new DirectoryOpusQuickSwitchProvider(),
                new TotalCommanderQuickSwitchProvider()
            },
            () => CreateExplorerFallbackCandidates(explorerTracker));
    }

    internal static IReadOnlyList<ICustomDialogAdapter> CreateDefaultCustomDialogAdapters()
    {
        return new ICustomDialogAdapter[]
        {
            new BlenderFileBrowserAdapter()
        };
    }

    internal static void StartHookQuickSwitchEnablementOnStartup(
        IHookQuickSwitchBridge? bridge,
        Func<IHookQuickSwitchBridge, Task> startEnablement)
    {
        ArgumentNullException.ThrowIfNull(startEnablement);

        if (bridge is null)
        {
            return;
        }

        _ = startEnablement(bridge);
    }

    internal static string CreateAppDataStartupFailureMessage(AppDataPaths paths, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(exception);

        return string.Join(
            Environment.NewLine,
            "ListaryOpen could not open its program data directory.",
            string.Empty,
            $"Data directory: {paths.DataDirectory}",
            $"Index database: {paths.IndexDatabasePath}",
            string.Empty,
            exception.Message);
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

    internal static void EnableNtfsFastIndexing(ElevatedIndexerClient? client, Action requestReindex)
    {
        ArgumentNullException.ThrowIfNull(requestReindex);

        client?.EnableUacElevation();
        requestReindex();
    }

    internal static bool EnableNtfsFastIndexingOnStartup(ElevatedIndexerClient? client)
    {
        if (client is null)
        {
            return false;
        }

        if (!client.IsAvailable)
        {
            return false;
        }

        client.EnableUacElevation();
        return true;
    }

    internal static ExplorerObservationScheduler? StartPeriodicExplorerObservation(
        ExplorerTracker? explorerTracker,
        Func<IExplorerObservationTimer> timerFactory)
    {
        ArgumentNullException.ThrowIfNull(timerFactory);

        if (explorerTracker is null)
        {
            return null;
        }

        var scheduler = new ExplorerObservationScheduler(
            explorerTracker.ObserveForegroundExplorerFolder,
            TimeSpan.FromSeconds(2),
            timerFactory);
        scheduler.Start();
        return scheduler;
    }

    private static string JoinHotkeyNames(IEnumerable<HotkeyRegistration> hotkeys)
    {
        return string.Join(", ", hotkeys.Select(hotkey => hotkey.Name));
    }

    internal static AppDataPaths CreateAppDataPaths()
    {
        return AppDataPaths.CreateDefault();
    }

    private void RequestReindex()
    {
        StartBackgroundIndexing();
    }

    private void EnableNtfsFastIndexing()
    {
        EnableNtfsFastIndexing(
            _elevatedIndexerClient,
            () => StartBackgroundIndexing(cancelActive: true));
    }

    private void EnableHookQuickSwitch()
    {
        var bridge = _hookQuickSwitchBridge;
        if (bridge is null)
        {
            return;
        }

        _ = EnableHookQuickSwitchAsync(bridge);
    }

    private async Task EnableHookQuickSwitchAsync(IHookQuickSwitchBridge bridge)
    {
        try
        {
            await bridge.EnableAsync(_shutdownCancellation.Token).ConfigureAwait(false);
            if (!_shutdownCancellation.IsCancellationRequested)
            {
                await UpdateHookQuickSwitchStatusAsync(bridge.Status).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            if (!_shutdownCancellation.IsCancellationRequested)
            {
                await UpdateHookQuickSwitchStatusAsync(bridge.Status).ConfigureAwait(false);
            }
        }
    }

    private void StartBackgroundIndexing(bool cancelActive = false)
    {
        var runCancellation = _indexingCancellation.CreateRun(_shutdownCancellation.Token, cancelActive);
        BackgroundIndexingTaskStarter.Start(
            _indexingTasks,
            _ => RunIndexingRunAsync(runCancellation),
            CancellationToken.None);
    }

    private async Task RunIndexingRunAsync(CancellationTokenSource runCancellation)
    {
        try
        {
            await RunInitialIndexAsync(runCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            _indexingCancellation.CompleteRun(runCancellation);
        }
    }

    private async Task RunInitialIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunSerializedIndexingAsync(_indexingRunLock, RunSingleIndexAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    internal static async Task RunSerializedIndexingAsync(
        SemaphoreSlim indexingRunLock,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexingRunLock);
        ArgumentNullException.ThrowIfNull(work);

        await indexingRunLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            indexingRunLock.Release();
        }
    }

    private async Task RunSingleIndexAsync(CancellationToken cancellationToken)
    {
        var coordinator = _indexingCoordinator;
        if (coordinator is null)
        {
            return;
        }

        var rootPaths = _settingsViewModel?.Settings.IndexedRoots ?? AppSettings.Defaults().IndexedRoots;
        var roots = CreateIndexRoots(rootPaths);
        await coordinator.IndexRootsAsync(roots, cancellationToken).ConfigureAwait(false);
    }

    private void OnIndexingStatusChanged(object? sender, IndexingStatus status)
    {
        if (IsShuttingDown)
        {
            return;
        }

        _ = UpdateIndexingStatusAsync(status);
    }

    private void OnHookQuickSwitchStatusChanged(object? sender, HookQuickSwitchStatus status)
    {
        if (IsShuttingDown)
        {
            return;
        }

        _ = UpdateHookQuickSwitchStatusAsync(status);
    }

    private async Task UpdateHookQuickSwitchStatusAsync(HookQuickSwitchStatus status)
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
                            _settingsViewModel?.UpdateHookQuickSwitchStatus(status);
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

    private static bool IsAppDataStartupException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException;
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
            if (_searchPanel is not null)
            {
                if (GetSearchHotkeyPanelAction(_searchPanel.IsVisible) == SearchHotkeyPanelAction.Hide)
                {
                    _searchPanel.Hide();
                }
                else
                {
                    _searchPanel.ActivateSearch();
                }
            }

            return;
        }

        if (IsDialogHotkey(name))
        {
            HandleDialogHotkey();
        }
    }

    private void HandleDialogHotkey()
    {
        _ = HandleDialogHotkeyAsync();
    }

    private async Task HandleDialogHotkeyAsync()
    {
        var capturedDialog = await TryCaptureHookDialogAsync(
            _hookQuickSwitchBridge,
            CancellationToken.None);
        var dialogFolderActivations = CreateDialogFolderActivations(
            capturedDialog,
            JumpDialogToFolderAsync,
            JumpDialogToFolderAsync);
        var candidates = ObserveQuickSwitchFolderCandidates(_quickSwitchWindowProvider);
        DialogJumpResult? directJumpResult = null;
        if (await TryJumpToFirstQuickSwitchFolderAsync(
                candidates,
                dialogFolderActivations.Direct,
                result => directJumpResult = result,
                CancellationToken.None))
        {
            if (directJumpResult is not null)
            {
                ReportDirectDialogJumpStatus(
                    directJumpResult,
                    result => _searchPanel?.ReportDialogJumpResult(result),
                    message => _trayController?.ShowStatus(message));
            }

            return;
        }

        await ActivateQuickSwitchFolderSearchWithDirectJumpStatusAsync(
            candidates,
            directJumpResult,
            folderCandidates => _searchPanel is null
                ? Task.CompletedTask
                : _searchPanel.ActivateQuickSwitchFolderSearchAsync(
                    folderCandidates,
                    dialogFolderActivations.Panel),
            result => _searchPanel?.ReportDialogJumpResult(result),
            message => _trayController?.ShowStatus(message));
    }

    internal static async Task<HookDialogContext?> TryCaptureHookDialogAsync(
        IHookQuickSwitchBridge? hookBridge,
        CancellationToken cancellationToken)
    {
        if (hookBridge is null)
        {
            return null;
        }

        try
        {
            return await hookBridge.GetActiveDialogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            return null;
        }
    }

    internal static (
        Func<string, CancellationToken, Task<DialogJumpResult>> Direct,
        Func<string, CancellationToken, Task<DialogJumpResult>> Panel) CreateDialogFolderActivations(
        HookDialogContext? capturedDialog,
        Func<string, CancellationToken, Task<DialogJumpResult>> activeDialogActivation,
        Func<HookDialogContext, string, CancellationToken, Task<DialogJumpResult>> capturedDialogActivation)
    {
        ArgumentNullException.ThrowIfNull(activeDialogActivation);
        ArgumentNullException.ThrowIfNull(capturedDialogActivation);

        var panelActivation = capturedDialog is null
            ? activeDialogActivation
            : (folderPath, cancellationToken) =>
                capturedDialogActivation(capturedDialog, folderPath, cancellationToken);
        return (activeDialogActivation, panelActivation);
    }

    internal static IReadOnlyList<QuickSwitchFolderCandidate> ObserveQuickSwitchFolderCandidates(
        IQuickSwitchWindowProvider? quickSwitchWindowProvider)
    {
        if (quickSwitchWindowProvider is null)
        {
            return Array.Empty<QuickSwitchFolderCandidate>();
        }

        if (quickSwitchWindowProvider is IRefreshableQuickSwitchWindowProvider refreshableProvider)
        {
            refreshableProvider.Refresh();
        }

        var candidates = quickSwitchWindowProvider.GetFolderCandidates();
        if (candidates.Count > 0)
        {
            return candidates;
        }

        var lastFolder = quickSwitchWindowProvider is ExplorerTracker explorerTracker
            ? explorerTracker.LastFolder
            : null;
        return !string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder)
            ? new[]
            {
                new QuickSwitchFolderCandidate(
                    lastFolder,
                    "Explorer",
                    IntPtr.Zero,
                    false)
            }
            : Array.Empty<QuickSwitchFolderCandidate>();
    }

    private static IReadOnlyList<QuickSwitchFolderCandidate> CreateExplorerFallbackCandidates(
        ExplorerTracker explorerTracker)
    {
        var lastFolder = explorerTracker.LastFolder;
        return !string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder)
            ? new[]
            {
                new QuickSwitchFolderCandidate(
                    lastFolder,
                    "Explorer",
                    IntPtr.Zero,
                    false)
            }
            : Array.Empty<QuickSwitchFolderCandidate>();
    }

    internal static string? ObserveAndGetExistingTrackedFolder(ExplorerTracker? explorerTracker)
    {
        var candidateFolder = ObserveQuickSwitchFolderCandidates(explorerTracker)
            .FirstOrDefault()
            ?.FolderPath;
        if (!string.IsNullOrWhiteSpace(candidateFolder) && Directory.Exists(candidateFolder))
        {
            return candidateFolder;
        }

        var lastFolder = explorerTracker?.LastFolder;
        return !string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder)
            ? lastFolder
            : null;
    }

    internal static async Task<bool> TryJumpToFirstQuickSwitchFolderAsync(
        IReadOnlyList<QuickSwitchFolderCandidate> candidates,
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation,
        CancellationToken cancellationToken)
    {
        return await TryJumpToFirstQuickSwitchFolderAsync(
                candidates,
                dialogFolderActivation,
                _ => { },
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<bool> TryJumpToFirstQuickSwitchFolderAsync(
        IReadOnlyList<QuickSwitchFolderCandidate> candidates,
        Func<string, CancellationToken, Task<DialogJumpResult>> dialogFolderActivation,
        Action<DialogJumpResult> reportDialogJumpResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(dialogFolderActivation);
        ArgumentNullException.ThrowIfNull(reportDialogJumpResult);

        var folderPath = candidates
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.FolderPath) &&
                                         Directory.Exists(candidate.FolderPath))
            ?.FolderPath;
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        try
        {
            var result = await dialogFolderActivation(folderPath, cancellationToken);
            reportDialogJumpResult(result);
            return result.Status == DialogJumpStatus.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceError(exception.ToString());
            return false;
        }
    }

    internal static async Task ActivateQuickSwitchFolderSearchWithDirectJumpStatusAsync(
        IReadOnlyList<QuickSwitchFolderCandidate> candidates,
        DialogJumpResult? directJumpResult,
        Func<IReadOnlyList<QuickSwitchFolderCandidate>, Task> activateFolderSearch,
        Action<DialogJumpResult> reportPanelStatus,
        Action<string> showStatus)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(activateFolderSearch);
        ArgumentNullException.ThrowIfNull(reportPanelStatus);
        ArgumentNullException.ThrowIfNull(showStatus);

        await activateFolderSearch(candidates);
        if (directJumpResult is not null)
        {
            ReportDirectDialogJumpStatus(directJumpResult, reportPanelStatus, showStatus);
        }
    }

    internal static void ReportDirectDialogJumpStatus(
        DialogJumpResult result,
        Action<DialogJumpResult> reportPanelStatus,
        Action<string> showStatus)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(reportPanelStatus);
        ArgumentNullException.ThrowIfNull(showStatus);

        if (result.Status == DialogJumpStatus.Success)
        {
            if (result.IsDegradedSuccess)
            {
                showStatus(
                    string.IsNullOrWhiteSpace(result.Message)
                        ? "Dialog folder changed."
                        : result.Message);
            }

            return;
        }

        reportPanelStatus(result);
    }

    private async Task<DialogJumpResult> JumpDialogToFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        var dialogBridge = _dialogBridge;
        var result = await (dialogBridge is null
            ? Task.FromResult(new DialogJumpResult(DialogJumpStatus.Failed, "Dialog integration is not available."))
            : dialogBridge.JumpToFolderAsync(folderPath, cancellationToken)).ConfigureAwait(false);
        return await RecordSuccessfulDialogUsageAsync(
            result,
            folderPath,
            _searchIndex).ConfigureAwait(false);
    }

    private async Task<DialogJumpResult> JumpDialogToFolderAsync(
        HookDialogContext dialog,
        string folderPath,
        CancellationToken cancellationToken)
    {
        var dialogBridge = _dialogBridge;
        var result = await (dialogBridge is null
            ? Task.FromResult(new DialogJumpResult(DialogJumpStatus.Failed, "Dialog integration is not available."))
            : dialogBridge.JumpToFolderAsync(dialog, folderPath, cancellationToken)).ConfigureAwait(false);
        return await RecordSuccessfulDialogUsageAsync(
            result,
            folderPath,
            _searchIndex).ConfigureAwait(false);
    }

    internal static async Task<DialogJumpResult> RecordSuccessfulDialogUsageAsync(
        DialogJumpResult result,
        string folderPath,
        ISearchIndex? searchIndex)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status == DialogJumpStatus.Success && searchIndex is not null)
        {
            try
            {
                await searchIndex.RecordUsageAsync(folderPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Trace.TraceError(exception.ToString());
            }
        }

        return result;
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

internal enum SearchHotkeyPanelAction
{
    Activate,
    Hide
}

internal sealed class AppStartupException : Exception
{
    public AppStartupException(string userMessage, Exception innerException)
        : base(userMessage, innerException)
    {
        UserMessage = userMessage;
    }

    public string UserMessage { get; }
}

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

internal sealed class IndexingRunCancellationManager : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<CancellationTokenSource> _runs = new();
    private bool _disposed;

    public CancellationTokenSource CreateRun(CancellationToken shutdownToken, bool cancelActive)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (cancelActive)
            {
                CancelRuns();
            }

            var run = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            _runs.Add(run);
            return run;
        }
    }

    public void CompleteRun(CancellationTokenSource run)
    {
        ArgumentNullException.ThrowIfNull(run);

        lock (_gate)
        {
            _runs.Remove(run);
        }

        run.Dispose();
    }

    public void CancelActive()
    {
        lock (_gate)
        {
            CancelRuns();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource[] runs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelRuns();
            runs = _runs.ToArray();
            _runs.Clear();
        }

        foreach (var run in runs)
        {
            run.Dispose();
        }
    }

    private void CancelRuns()
    {
        foreach (var run in _runs)
        {
            run.Cancel();
        }
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
