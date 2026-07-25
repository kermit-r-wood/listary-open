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
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ListaryOpen.App;

public partial class App : Application
{
    private static readonly TimeSpan IdleDialogProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AttachedDialogProbeInterval = TimeSpan.FromMilliseconds(500);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly IndexingRunCancellationManager _indexingCancellation = new();
    private readonly BackgroundIndexingTaskTracker _indexingTasks = new();
    private readonly SemaphoreSlim _indexingRunLock = new(1, 1);

    private DialogBridge? _dialogBridge;
    private ContinuousIndexingService? _continuousIndexing;
    private ElevatedIndexerClient? _elevatedIndexerClient;
    private ExplorerObservationScheduler? _explorerObservationScheduler;
    private FallbackIndexProvider? _fallbackIndexProvider;
    private GlobalTextInputService? _globalTextInputService;
    private IHookQuickSwitchBridge? _hookQuickSwitchBridge;
    private ExplorerTracker? _explorerTracker;
    private ExplorerQuickMenu? _explorerQuickMenu;
    private HotkeyService? _hotkeyService;
    private IndexingCoordinator? _indexingCoordinator;
    private NtfsIndexProvider? _ntfsIndexProvider;
    private PerformanceMetricsFileSink? _performanceMetricsFileSink;
    private IQuickSwitchWindowProvider? _quickSwitchWindowProvider;
    private QuickSwitchBarWindow? _quickSwitchBar;
    private SearchPanel? _searchPanel;
    private TaskManagerSearchWindow? _taskManagerSearchWindow;
    private SqliteSearchIndex? _searchIndex;
    private SettingsViewModel? _settingsViewModel;
    private SingleInstanceGuard? _singleInstanceGuard;
    private E2eControlServer? _e2eControlServer;
    private E2eControlOptions? _e2eControlOptions;
    private TrayController? _trayController;
    private VolumeIndexer? _volumeIndexer;
    private readonly AppSettingsStore _settingsStore = new();
    private readonly GitHubUpdateChecker _updateChecker = new();
    private AppSettings _activeSettings = AppSettings.Defaults();
    private IReadOnlyList<string> _internalIndexExclusions = Array.Empty<string>();
    private string? _settingsPath;
    private DispatcherTimer? _dialogAttachmentTimer;
    private DispatcherTimer? _foregroundObservationTimer;
    private DispatcherTimer? _scheduledIndexTimer;
    private bool _dialogAttachmentProbeRunning;
    private string? _attachedDialogId;
    private HookDialogContext? _lastE2eHookDialog;
    private IntPtr _lastExplorerTypeSearchWindow;
    private IntPtr _lastTaskManagerSearchWindow;
    private readonly ExplorerTypeSearchNavigationCapture _explorerNavigationCapture = new();
    private readonly object _globalTextInputQueueGate = new();
    private readonly Queue<GlobalTextInputEventArgs> _globalTextInputQueue = new();
    private readonly List<GlobalTextInputEventArgs> _pendingExplorerActivationInputs = new();
    private bool _globalTextInputDrainScheduled;
    private bool _explorerActivationRefreshPending;
    private IntPtr _lastObservedForegroundWindow;
    private IntPtr _activeDialogQuickSwitchInputWindow;

    internal static bool IsShuttingDown =>
        Current?.Dispatcher.HasShutdownStarted == true ||
        Current?.Dispatcher.HasShutdownFinished == true;

    internal static bool ShouldShowDuplicateInstanceMessage => false;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _e2eControlOptions = E2eControlOptions.TryParse(e.Args);
            _singleInstanceGuard = SingleInstanceGuard.TryAcquire();
            if (!_singleInstanceGuard.IsOwner)
            {
                if (ShouldShowDuplicateInstanceMessage)
                {
                    MessageBox.Show(
                        "ListaryOpen is already running.",
                        "ListaryOpen",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

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
        _e2eControlServer?.Dispose();
        _indexingCancellation.CancelActive();
        _shutdownCancellation.Cancel();
        _continuousIndexing?.Dispose();

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

        if (_globalTextInputService is not null)
        {
            _globalTextInputService.CaptureOverlayInput = false;
            _globalTextInputService.DialogInputWindow = IntPtr.Zero;
            _explorerNavigationCapture.Deactivate();
            _globalTextInputService.TextInput -= OnGlobalTextInput;
            _globalTextInputService.EscapePressed -= OnGlobalEscapePressed;
            _globalTextInputService.PointerPressed -= OnGlobalPointerPressed;
            _globalTextInputService.NavigationPressed -= OnGlobalNavigationPressed;
            _globalTextInputService.ConfirmPressed -= OnGlobalConfirmPressed;
            _globalTextInputService.EditCommandPressed -= OnGlobalEditCommandPressed;
            _globalTextInputService.ResultShortcutPressed -= OnGlobalResultShortcutPressed;
            _globalTextInputService.ExplorerMenuGesturePressed -= OnExplorerMenuGesturePressed;
            _globalTextInputService.Dispose();
        }

        if (_searchPanel is not null)
        {
            _searchPanel.ExplorerSearchSessionEnded -= OnExplorerSearchSessionEnded;
            _searchPanel.ResultContextMenuOpenStateChanged -= OnResultContextMenuOpenStateChanged;
        }

        if (_quickSwitchBar is not null)
        {
            _quickSwitchBar.AttachmentChanged -= OnQuickSwitchBarAttachmentChanged;
            _quickSwitchBar.InputCaptureStateChanged -= OnQuickSwitchBarInputCaptureStateChanged;
        }

        Interlocked.Exchange(ref _activeDialogQuickSwitchInputWindow, IntPtr.Zero);

        if (_taskManagerSearchWindow is not null)
        {
            _taskManagerSearchWindow.SearchSessionEnded -= OnTaskManagerSearchSessionEnded;
            _taskManagerSearchWindow.Close();
        }

        if (_hookQuickSwitchBridge is not null)
        {
            _hookQuickSwitchBridge.StatusChanged -= OnHookQuickSwitchStatusChanged;
            _hookQuickSwitchBridge.Dispose();
        }

        _explorerObservationScheduler?.Dispose();
        StopForegroundObservationMonitoring();
        _explorerQuickMenu?.CloseOpenMenu();
        _scheduledIndexTimer?.Stop();
        StopDialogAttachmentMonitoring();
        _trayController?.Dispose();
        _performanceMetricsFileSink?.Dispose();
        DisposeSearchIndex();
        _indexingCancellation.Dispose();
        _singleInstanceGuard?.Dispose();
        _shutdownCancellation.Dispose();

        base.OnExit(e);
    }

    private async Task<bool> InitializeApplicationServicesAsync()
    {
        var appDataPaths = CreateAppDataPaths();
        AppSettings settings;
        try
        {
            appDataPaths.EnsureDirectories();
            _settingsPath = appDataPaths.SettingsPath;
            settings = _settingsStore.Load(appDataPaths.SettingsPath);
            PinyinMatcher.Configure(settings.SearchTransliteration);
            _performanceMetricsFileSink = new PerformanceMetricsFileSink(
                appDataPaths.PerformanceMetricsPath);
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
                () => InitializeApplicationServices(_searchIndex, settings, appDataPaths))
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

    private bool InitializeApplicationServices(
        SqliteSearchIndex searchIndex,
        AppSettings settings,
        AppDataPaths appDataPaths)
    {
        _activeSettings = settings;
        ThemeManager.Initialize();
        ThemeManager.Apply(settings.Theme);
        LocalizationManager.Initialize();
        LocalizationManager.Apply(settings.Language);
        _fallbackIndexProvider = new FallbackIndexProvider();
        _elevatedIndexerClient = CreateElevatedIndexerClient(appDataPaths);
        var ntfsFastIndexingEnabled = EnableNtfsFastIndexingOnStartup(_elevatedIndexerClient);
        _ntfsIndexProvider = new NtfsIndexProvider(_elevatedIndexerClient);
        _volumeIndexer = new VolumeIndexer(new IIndexProvider[]
        {
            _ntfsIndexProvider,
            _fallbackIndexProvider
        });
        _internalIndexExclusions = new[] { appDataPaths.DataDirectory };
        _indexingCoordinator = new IndexingCoordinator(
            searchIndex,
            _volumeIndexer,
            _fallbackIndexProvider,
            recordFilter: record =>
                ConfiguredIndexFilter.ShouldInclude(record, _activeSettings.ExcludedPaths) &&
                !IsPathWithinAnyRoot(record.FullPath, _internalIndexExclusions));
        _indexingCoordinator.StatusChanged += OnIndexingStatusChanged;
        _continuousIndexing = new ContinuousIndexingService(
            searchIndex,
            GetAllIndexRootPaths(settings),
            settings.ExcludedPaths,
            _internalIndexExclusions,
            () => StartBackgroundIndexing());
        _continuousIndexing.Start(baselineReady: false);

        _hookQuickSwitchBridge = HookQuickSwitchBridgeFactory.CreateDefault();
        _hookQuickSwitchBridge.StatusChanged += OnHookQuickSwitchStatusChanged;
        _dialogBridge = new DialogBridge(
            _hookQuickSwitchBridge,
            CreateDefaultDialogJumpPlugins());

        _settingsViewModel = new SettingsViewModel(
            settings,
            EnableNtfsFastIndexing,
            EnableHookQuickSwitch,
            ntfsFastIndexingEnabled,
            ApplyHotkeys,
            ApplySettings,
            _updateChecker.CheckAsync,
            ThemeManager.Apply);
        _settingsViewModel.UpdateHookQuickSwitchStatus(_hookQuickSwitchBridge.Status);
        var settingsWindow = new MainWindow(_settingsViewModel);
        _explorerTracker = new ExplorerTracker();
        _quickSwitchWindowProvider = CreateDefaultQuickSwitchWindowProvider(_explorerTracker);
        _explorerObservationScheduler = StartPeriodicExplorerObservation(
            _explorerTracker,
            () => new DispatcherExplorerObservationTimer());
        StartForegroundObservationMonitoring();
        _searchPanel = new SearchPanel(new SearchPanelViewModel(
            searchIndex,
            JumpDialogToFolderAsync,
            () => _activeSettings.QuickLaunchEntries));
        _searchPanel.ExplorerSearchSessionEnded += OnExplorerSearchSessionEnded;
        _searchPanel.ResultContextMenuOpenStateChanged += OnResultContextMenuOpenStateChanged;
        _quickSwitchBar = new QuickSwitchBarWindow(
            new SearchPanelViewModel(searchIndex, JumpDialogToFolderAsync));
        _quickSwitchBar.AttachmentChanged += OnQuickSwitchBarAttachmentChanged;
        _quickSwitchBar.InputCaptureStateChanged += OnQuickSwitchBarInputCaptureStateChanged;
        _trayController = new TrayController(settingsWindow, RequestReindex);
        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        var hotkeyStartupDecision = CreateHotkeyStartupDecision(
            _hotkeyService.Register(settings.SearchHotkey, settings.DialogHotkey));
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

        StartGlobalTextInputCapture();

        MainWindow = settingsWindow;
        if (ShouldShowSettingsOnStartup(hasBlockingStartupMessage: false))
        {
            settingsWindow.Show();
        }

        StartHookQuickSwitchEnablementOnStartup(_hookQuickSwitchBridge, EnableHookQuickSwitchAsync);
        StartDialogAttachmentMonitoring();
        ConfigureScheduledIndexing(settings.IndexFrequency);
        _ = WarmSearchIndexAsync(searchIndex, _shutdownCancellation.Token);
        StartBackgroundIndexing();
        if (settings.CheckForUpdates)
        {
            _settingsViewModel.CheckForUpdatesCommand.Execute(null);
        }

        StartE2eControlServer();
        return true;
    }

    private void StartE2eControlServer()
    {
        if (_e2eControlOptions is null)
        {
            return;
        }

        _e2eControlServer = new E2eControlServer(
            _e2eControlOptions,
            setInjectedInputPermission: allowed =>
            {
                var inputService = _globalTextInputService;
                if (inputService is null)
                {
                    return false;
                }

                inputService.AcceptInjectedInputForTesting = allowed;
                return inputService.AcceptInjectedInputForTesting == allowed;
            },
            requestShutdown: () => Dispatcher.BeginInvoke(new Action(() => Shutdown())),
            getDialogPrecaptureProof: processId =>
            {
                var dialog = Volatile.Read(ref _lastE2eHookDialog);
                return dialog is not null && dialog.ProcessId == processId
                    ? new E2eDialogPrecaptureProof(
                        dialog.ProcessId,
                        dialog.FirefoxFileDialogUtility,
                        dialog.PreloadConfirmedBeforeDialog)
                    : null;
            });
        _e2eControlServer.Start();
    }

    private string? ApplySettings(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
        {
            return "Settings path is unavailable.";
        }

        try
        {
            var indexingChanged = !_activeSettings.IndexedRoots.SequenceEqual(settings.IndexedRoots, StringComparer.OrdinalIgnoreCase) ||
                !_activeSettings.ExcludedPaths.SequenceEqual(settings.ExcludedPaths, StringComparer.OrdinalIgnoreCase) ||
                _activeSettings.SearchTransliteration != settings.SearchTransliteration;
            _settingsStore.Save(_settingsPath, settings);
            _activeSettings = settings;
            PinyinMatcher.Configure(settings.SearchTransliteration);
            if (indexingChanged)
            {
                _continuousIndexing?.PauseUntilBaselineReady();
                _continuousIndexing?.Reconfigure(
                    GetAllIndexRootPaths(settings),
                    settings.ExcludedPaths,
                    _internalIndexExclusions);
            }
            ThemeManager.Apply(settings.Theme);
            LocalizationManager.Apply(settings.Language);
            ConfigureScheduledIndexing(settings.IndexFrequency);
            if (indexingChanged)
            {
                Dispatcher.BeginInvoke(new Action(() => StartBackgroundIndexing(cancelActive: true)));
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"Could not save settings: {exception.Message}";
        }
    }

    private void ConfigureScheduledIndexing(IndexUpdateFrequency frequency)
    {
        if (_scheduledIndexTimer is not null)
        {
            _scheduledIndexTimer.Stop();
            _scheduledIndexTimer = null;
        }

        var interval = GetIndexInterval(frequency);
        if (interval == TimeSpan.Zero)
        {
            return;
        }

        _scheduledIndexTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _scheduledIndexTimer.Tick += (_, _) => StartBackgroundIndexing();
        _scheduledIndexTimer.Start();
    }

    internal static TimeSpan GetIndexInterval(IndexUpdateFrequency frequency) => frequency switch
        {
            IndexUpdateFrequency.Every15Minutes => TimeSpan.FromMinutes(15),
            IndexUpdateFrequency.Hourly => TimeSpan.FromHours(1),
            IndexUpdateFrequency.Every6Hours => TimeSpan.FromHours(6),
            IndexUpdateFrequency.Daily => TimeSpan.FromDays(1),
            _ => TimeSpan.Zero
        };

    internal static async Task WarmSearchIndexAsync(
        ISearchIndex? searchIndex,
        CancellationToken cancellationToken)
    {
        if (searchIndex is null)
        {
            return;
        }

        try
        {
            var warmupQuery = new SearchQuery(
                "__listary_open_warmup__",
                SearchMode.FilesAndFolders,
                limit: 1);
            await Task.Run(
                    () => searchIndex.SearchAsync(warmupQuery, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Search warmup failed: {0}", exception.Message);
        }
    }

    private string? ApplyHotkeys(string searchHotkey, string dialogHotkey)
    {
        var service = _hotkeyService;
        var settingsViewModel = _settingsViewModel;
        if (service is null || settingsViewModel is null || string.IsNullOrWhiteSpace(_settingsPath))
        {
            return "Hotkey service is unavailable.";
        }

        var previous = settingsViewModel.Settings;
        var result = service.Register(searchHotkey, dialogHotkey);
        if (!result.AllRegistered)
        {
            service.Register(previous.SearchHotkey, previous.DialogHotkey);
            var failed = string.Join(", ", result.FailedHotkeys.Select(item => item.Name));
            return $"Could not register: {failed}. Check the format or conflicts.";
        }

        try
        {
            _settingsStore.Save(_settingsPath, previous.WithHotkeys(searchHotkey, dialogHotkey));
            _activeSettings = previous.WithHotkeys(searchHotkey, dialogHotkey);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            service.Register(previous.SearchHotkey, previous.DialogHotkey);
            return $"Could not save hotkeys: {exception.Message}";
        }
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

    internal static IReadOnlyList<IDialogJumpPlugin> CreateDefaultDialogJumpPlugins(
        string? pluginRoot = null) =>
        DialogPluginLoader.LoadFromRoot(pluginRoot ?? DialogPluginLoader.DefaultPluginRoot).Plugins;

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

        var candidates = new List<IndexRoot>();
        foreach (var rootPath in rootPaths)
        {
            try
            {
                candidates.Add(new IndexRoot(rootPath));
            }
            catch (Exception exception) when (IsInvalidIndexRootException(exception))
            {
                Trace.TraceWarning("Skipping invalid index root '{0}': {1}", rootPath, exception.Message);
            }
        }

        var roots = new List<IndexRoot>();
        foreach (var candidate in candidates
                     .DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(root => root.Path.Length))
        {
            if (!roots.Any(root => IsPathWithinAnyRoot(candidate.Path, new[] { root.Path })))
            {
                roots.Add(candidate);
            }
        }

        return roots;
    }

    internal static IReadOnlyList<string> GetAllIndexRootPaths(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.IndexedRoots
            .Concat(AppSettings.GetBuiltInShortcutRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsPathWithinAnyRoot(string path, IReadOnlyList<string> roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(roots);

        string normalizedPath;
        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (IsInvalidIndexRootException(exception))
        {
            return false;
        }

        foreach (var root in roots)
        {
            try
            {
                var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ||
                    normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
                        ? normalizedRoot
                        : normalizedRoot + Path.DirectorySeparatorChar;
                if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith(
                        rootPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception exception) when (IsInvalidIndexRootException(exception))
            {
            }
        }

        return false;
    }

    internal static bool EnableNtfsFastIndexing(ElevatedIndexerClient? client, Action requestReindex)
    {
        ArgumentNullException.ThrowIfNull(requestReindex);

        if (client is null)
        {
            return false;
        }

        client.EnableUacElevation();
        if (!client.IsAvailable)
        {
            return false;
        }

        requestReindex();
        return true;
    }

    internal static ElevatedIndexerClient CreateElevatedIndexerClient(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new ElevatedIndexerClient(Path.Combine(
            paths.ProgramDirectory,
            "ListaryOpen.Indexer.Elevated.exe"));
    }

    internal static bool EnableNtfsFastIndexingOnStartup(ElevatedIndexerClient? client)
    {
        if (client is null)
        {
            return false;
        }

        // IsAvailable depends on the launch mode. A non-elevated build can
        // only become available after UAC mode is armed, so checking it first
        // permanently forced the NTFS volume provider onto Fallback.
        client.EnableUacElevation();
        return client.IsAvailable;
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
            TimeSpan.FromSeconds(10),
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

    private bool EnableNtfsFastIndexing()
    {
        return EnableNtfsFastIndexing(
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
            _continuousIndexing?.NotifyReconciliationCompleted();
            _continuousIndexing?.MarkBaselineReady();
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

        var rootPaths = GetAllIndexRootPaths(_activeSettings);
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

    /// <summary>
    /// Bound how long exit waits for in-flight indexing so a full MFT scan or
    /// elevated helper cleanup cannot freeze tray exit.
    /// </summary>
    internal static readonly TimeSpan IndexingShutdownTimeout = TimeSpan.FromSeconds(3);

    private void WaitForIndexingTasks()
    {
        try
        {
            _indexingTasks
                .WaitForCompletionAsync()
                .WaitAsync(IndexingShutdownTimeout)
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException)
        {
            Trace.TraceWarning(
                "Timed out after {0} ms waiting for indexing tasks during shutdown.",
                IndexingShutdownTimeout.TotalMilliseconds);
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
            // DisposeAsync itself is time-bounded (FTS maintenance + connection gates).
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

    private void StartGlobalTextInputCapture()
    {
        try
        {
            _globalTextInputService = new GlobalTextInputService();
            _globalTextInputService.TextInput += OnGlobalTextInput;
            _globalTextInputService.EscapePressed += OnGlobalEscapePressed;
            _globalTextInputService.PointerPressed += OnGlobalPointerPressed;
            _globalTextInputService.NavigationPressed += OnGlobalNavigationPressed;
            _globalTextInputService.ConfirmPressed += OnGlobalConfirmPressed;
            _globalTextInputService.EditCommandPressed += OnGlobalEditCommandPressed;
            _globalTextInputService.ResultShortcutPressed += OnGlobalResultShortcutPressed;
            _globalTextInputService.ExplorerMenuGesturePressed += OnExplorerMenuGesturePressed;
            _globalTextInputService.CaptureExplorerMenuInput = true;
            _globalTextInputService.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            Trace.TraceWarning("Explorer type-to-search is unavailable: {0}", exception.Message);
            if (_globalTextInputService is not null)
            {
                _globalTextInputService.TextInput -= OnGlobalTextInput;
                _globalTextInputService.EscapePressed -= OnGlobalEscapePressed;
                _globalTextInputService.PointerPressed -= OnGlobalPointerPressed;
                _globalTextInputService.NavigationPressed -= OnGlobalNavigationPressed;
                _globalTextInputService.ConfirmPressed -= OnGlobalConfirmPressed;
                _globalTextInputService.EditCommandPressed -= OnGlobalEditCommandPressed;
                _globalTextInputService.ResultShortcutPressed -= OnGlobalResultShortcutPressed;
                _globalTextInputService.ExplorerMenuGesturePressed -= OnExplorerMenuGesturePressed;
                _globalTextInputService.Dispose();
                _globalTextInputService = null;
            }
        }
    }

    private void OnGlobalTextInput(object? sender, GlobalTextInputEventArgs input)
    {
        if (IsShuttingDown)
        {
            return;
        }

        input.Handled = ShouldConsumeGlobalTextInput(input.Host);

        var scheduleDrain = false;
        lock (_globalTextInputQueueGate)
        {
            _globalTextInputQueue.Enqueue(input);
            if (!_globalTextInputDrainScheduled)
            {
                _globalTextInputDrainScheduled = true;
                scheduleDrain = true;
            }
        }

        if (scheduleDrain)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(DrainGlobalTextInputQueue));
        }
    }

    internal static bool ShouldConsumeGlobalTextInput(GlobalTextInputHost host) =>
        host == GlobalTextInputHost.Dialog;

    private void DrainGlobalTextInputQueue()
    {
        while (true)
        {
            List<GlobalTextInputEventArgs> inputs;
            lock (_globalTextInputQueueGate)
            {
                if (_globalTextInputQueue.Count == 0)
                {
                    _globalTextInputDrainScheduled = false;
                    return;
                }

                inputs = new List<GlobalTextInputEventArgs>(_globalTextInputQueue.Count);
                while (_globalTextInputQueue.TryDequeue(out var input))
                {
                    inputs.Add(input);
                }
            }

            foreach (var input in CoalesceGlobalTextInputs(inputs))
            {
                HandleGlobalTextInput(input);
            }
        }
    }

    internal static IReadOnlyList<GlobalTextInputEventArgs> CoalesceGlobalTextInputs(
        IReadOnlyList<GlobalTextInputEventArgs> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return [];
        }

        var coalesced = new List<GlobalTextInputEventArgs>(inputs.Count);
        var currentWindow = inputs[0].ForegroundWindow;
        var currentControlClass = inputs[0].FocusedControlClass;
        var currentHost = inputs[0].Host;
        var currentText = new StringBuilder(inputs[0].Text);
        for (var index = 1; index < inputs.Count; index++)
        {
            var input = inputs[index];
            if (input.ForegroundWindow == currentWindow &&
                input.Host == currentHost &&
                string.Equals(input.FocusedControlClass, currentControlClass, StringComparison.Ordinal))
            {
                currentText.Append(input.Text);
                continue;
            }

            coalesced.Add(new GlobalTextInputEventArgs(currentText.ToString(), currentWindow, currentControlClass, currentHost));
            currentWindow = input.ForegroundWindow;
            currentControlClass = input.FocusedControlClass;
            currentHost = input.Host;
            currentText.Clear();
            currentText.Append(input.Text);
        }

        coalesced.Add(new GlobalTextInputEventArgs(currentText.ToString(), currentWindow, currentControlClass, currentHost));
        return coalesced;
    }

    private void OnGlobalEscapePressed(object? sender, GlobalEscapeInputEventArgs input)
    {
        if (IsShuttingDown)
        {
            return;
        }

        var dialogInputWindow = TryCaptureDialogQuickSwitchInput(input.ForegroundWindow);
        Dispatcher.BeginInvoke(new Action(() => HandleGlobalEscapePressed(input, dialogInputWindow)));
    }

    private void HandleGlobalEscapePressed(
        GlobalEscapeInputEventArgs input,
        IntPtr dialogInputWindow)
    {
        _explorerQuickMenu?.CloseOpenMenu();

        if (dialogInputWindow != IntPtr.Zero)
        {
            _quickSwitchBar?.CollapseFromDialogInput(input.ForegroundWindow);
            return;
        }

        var searchPanel = _searchPanel;
        switch (GetOverlayEscapeTarget(
                    _taskManagerSearchWindow?.IsTaskManagerSearchActive == true,
                    searchPanel?.IsVisible == true && searchPanel.IsExplorerTypeSearchActive))
        {
            case OverlayEscapeTarget.TaskManager:
                _taskManagerSearchWindow!.DismissSearch();
                break;
            case OverlayEscapeTarget.Explorer:
                searchPanel!.DismissExplorerSearch();
                break;
        }
    }

    private void OnGlobalPointerPressed(object? sender, GlobalPointerInputEventArgs input)
    {
        if (IsShuttingDown)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => HandleGlobalPointerPressed(input)));
        if (ShouldCloseResultContextMenus(input.Button))
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() =>
                {
                    _searchPanel?.CloseResultContextMenu();
                    _quickSwitchBar?.CloseResultContextMenu();
                }));
        }
    }

    private void OnExplorerMenuGesturePressed(object? sender, ExplorerMenuGestureInputEventArgs input)
    {
        if (IsShuttingDown)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => _ = ShowExplorerQuickMenuAsync(input)));
    }

    private async Task ShowExplorerQuickMenuAsync(ExplorerMenuGestureInputEventArgs input)
    {
        if (!ExplorerQuickMenuHitTest.TryGetExplorerWindow(
                input.ScreenX,
                input.ScreenY,
                out var explorerWindow) ||
            !await Task.Run(() => ExplorerQuickMenuHitTest.IsExplorerItemsBackground(input.ScreenX, input.ScreenY)))
        {
            return;
        }

        try
        {
            if (_explorerObservationScheduler is not null)
            {
                await _explorerObservationScheduler.RequestObservationAsync();
            }

            var candidates = _explorerTracker?.GetFolderCandidates() ?? Array.Empty<QuickSwitchFolderCandidate>();
            var current = candidates.FirstOrDefault(candidate => candidate.WindowHandle == explorerWindow);
            if (current is null || string.IsNullOrWhiteSpace(current.FolderPath))
            {
                return;
            }

            if (_explorerQuickMenu is null)
            {
                _explorerQuickMenu = new ExplorerQuickMenu(
                    ShowSettingsWindow,
                    message => _trayController?.ShowStatus($"Quick menu: {message}"));
                _explorerQuickMenu.OpenStateChanged += OnExplorerQuickMenuOpenStateChanged;
            }
            _explorerQuickMenu.Show(
                current.FolderPath,
                candidates,
                _activeSettings.QuickMenuEntries,
                input.ScreenX,
                input.ScreenY,
                explorerWindow);
            RefreshOverlayInputCapture();
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            Trace.TraceWarning("Could not show Explorer quick menu: {0}", exception.Message);
        }
    }

    private void ShowSettingsWindow()
    {
        if (MainWindow is not Window settingsWindow)
        {
            return;
        }

        settingsWindow.Show();
        settingsWindow.Activate();
    }

    private void HandleGlobalPointerPressed(GlobalPointerInputEventArgs input)
    {
        if (_explorerQuickMenu?.IsOpen == true &&
            !_explorerQuickMenu.ContainsScreenPoint(input.ScreenX, input.ScreenY))
        {
            _explorerQuickMenu.CloseOpenMenu();
        }

        if (_taskManagerSearchWindow?.IsTaskManagerSearchActive == true &&
            !_taskManagerSearchWindow.ContainsScreenPoint(input.ScreenX, input.ScreenY))
        {
            _taskManagerSearchWindow.DismissSearch();
        }

        if (_quickSwitchBar?.IsDialogSearchExpanded == true &&
            !_quickSwitchBar.ContainsScreenPoint(input.ScreenX, input.ScreenY))
        {
            _quickSwitchBar.Collapse();
        }

        var searchPanel = _searchPanel;
        var explorerSearchVisible = searchPanel?.IsVisible == true && searchPanel.IsExplorerTypeSearchActive;
        var pointerInsidePanel = explorerSearchVisible &&
            searchPanel!.ContainsScreenPoint(input.ScreenX, input.ScreenY);
        if (!ShouldDismissExplorerTypeSearchForPointer(explorerSearchVisible, pointerInsidePanel))
        {
            return;
        }

        searchPanel!.DismissExplorerSearch();
    }

    private void OnGlobalNavigationPressed(object? sender, GlobalNavigationInputEventArgs input)
    {
        if (IsShuttingDown)
        {
            return;
        }

        var dialogInputWindow = TryCaptureDialogQuickSwitchInput(input.ForegroundWindow);
        if (dialogInputWindow != IntPtr.Zero)
        {
            input.Handled = true;
            Dispatcher.BeginInvoke(new Action(() =>
                _quickSwitchBar?.MoveSelectionFromDialogInput(
                    input.SelectionDelta,
                    input.ForegroundWindow)));
            return;
        }

        // The low-level hook needs its handled decision synchronously. The immutable
        // session reference atomically couples the active state and Explorer window.
        // The dispatched callback rejects work queued by an older overlay session.
        var capturedSession = _explorerNavigationCapture.TryCapture(input);
        if (capturedSession is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var currentPanel = _searchPanel;
            if (_explorerNavigationCapture.IsCurrent(capturedSession) &&
                _lastExplorerTypeSearchWindow == capturedSession.ExplorerWindow &&
                currentPanel?.IsVisible == true &&
                currentPanel.IsExplorerTypeSearchActive)
            {
                currentPanel.MoveSearchSelection(input.SelectionDelta);
            }
            else if (_explorerNavigationCapture.IsCurrent(capturedSession) &&
                _lastTaskManagerSearchWindow == capturedSession.ExplorerWindow &&
                _taskManagerSearchWindow?.IsTaskManagerSearchActive == true)
            {
                _taskManagerSearchWindow.MoveSelection(input.SelectionDelta);
            }
        }));
    }

    private void OnGlobalConfirmPressed(object? sender, GlobalConfirmInputEventArgs input)
    {
        if (IsShuttingDown)
        {
            return;
        }

        var dialogInputWindow = TryCaptureDialogQuickSwitchInput(input.ForegroundWindow);
        if (dialogInputWindow == IntPtr.Zero)
        {
            return;
        }

        input.Handled = true;
        Dispatcher.BeginInvoke(new Action(() =>
            _quickSwitchBar?.ConfirmSelectionFromDialogInput(input.ForegroundWindow)));
    }

    private void OnGlobalEditCommandPressed(object? sender, GlobalEditCommandInputEventArgs input)
    {
        if (IsShuttingDown)
        {
            return;
        }

        var dialogInputWindow = TryCaptureDialogQuickSwitchInput(input.ForegroundWindow);
        if (dialogInputWindow != IntPtr.Zero)
        {
            input.Handled = true;
            Dispatcher.BeginInvoke(new Action(() =>
                _quickSwitchBar?.ExecuteEditCommandFromDialogInput(
                    input.Command,
                    input.ForegroundWindow)));
            return;
        }

        var capturedSession = _explorerNavigationCapture.TryCapture(input);
        if (capturedSession is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var currentPanel = _searchPanel;
            if (_explorerNavigationCapture.IsCurrent(capturedSession) &&
                _lastExplorerTypeSearchWindow == capturedSession.ExplorerWindow &&
                currentPanel?.IsVisible == true &&
                currentPanel.IsExplorerTypeSearchActive)
            {
                currentPanel.ExecuteExplorerSearchEditCommand(input.Command);
            }
            else if (_explorerNavigationCapture.IsCurrent(capturedSession) &&
                _lastTaskManagerSearchWindow == capturedSession.ExplorerWindow &&
                _taskManagerSearchWindow?.IsTaskManagerSearchActive == true)
            {
                _taskManagerSearchWindow.ExecuteEditCommand(input.Command);
            }
        }));
    }

    private void OnGlobalResultShortcutPressed(object? sender, GlobalResultShortcutInputEventArgs input)
    {
        if (IsShuttingDown)
        {
            return;
        }

        var dialogInputWindow = TryCaptureDialogQuickSwitchInput(input.ForegroundWindow);
        if (dialogInputWindow != IntPtr.Zero)
        {
            input.Handled = true;
            Dispatcher.BeginInvoke(new Action(() =>
                _quickSwitchBar?.ActivateResultShortcutFromDialogInput(
                    input.ResultIndex,
                    input.ForegroundWindow)));
            return;
        }

        var capturedSession = _explorerNavigationCapture.TryCapture(input);
        if (capturedSession is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var currentPanel = _searchPanel;
            if (_explorerNavigationCapture.IsCurrent(capturedSession) &&
                _lastExplorerTypeSearchWindow == capturedSession.ExplorerWindow &&
                currentPanel?.IsVisible == true &&
                currentPanel.IsExplorerTypeSearchActive)
            {
                _ = currentPanel.ActivateResultShortcutAsync(input.ResultIndex);
            }
            else if (_explorerNavigationCapture.IsCurrent(capturedSession) &&
                _lastTaskManagerSearchWindow == capturedSession.ExplorerWindow &&
                _taskManagerSearchWindow?.IsTaskManagerSearchActive == true)
            {
                _taskManagerSearchWindow.ActivateResultShortcut(input.ResultIndex);
            }
        }));
    }

    private void HandleGlobalTextInput(GlobalTextInputEventArgs input)
    {
        if (input.Host == GlobalTextInputHost.TaskManager)
        {
            HandleTaskManagerTextInput(input);
            return;
        }

        if (input.Host == GlobalTextInputHost.Dialog)
        {
            _quickSwitchBar?.TryAppendDialogText(input.Text, input.ForegroundWindow);
            return;
        }

        var searchPanel = _searchPanel;
        if (ShouldAppendExplorerTypeSearchInput(
                input,
                _lastExplorerTypeSearchWindow,
                searchPanel?.IsVisible == true && searchPanel.IsExplorerTypeSearchActive))
        {
            searchPanel!.AppendExplorerSearchText(input.Text);
            return;
        }

        QueueExplorerActivationAfterRefresh(input);
    }

    private void HandleTaskManagerTextInput(GlobalTextInputEventArgs input)
    {
        if (string.IsNullOrWhiteSpace(input.Text) ||
            GlobalTextInputService.IsTextEntryControlClass(input.FocusedControlClass) ||
            TaskManagerAutomationService.IsNativeTextInputFocused(input.ForegroundWindow))
        {
            return;
        }

        if (_taskManagerSearchWindow?.IsTaskManagerSearchActive == true &&
            input.ForegroundWindow == _lastTaskManagerSearchWindow)
        {
            _taskManagerSearchWindow.AppendText(input.Text);
            return;
        }

        if (_searchPanel?.IsExplorerTypeSearchActive == true)
        {
            _searchPanel.DismissExplorerSearch();
        }

        _taskManagerSearchWindow ??= CreateTaskManagerSearchWindow();
        _lastTaskManagerSearchWindow = input.ForegroundWindow;
        _taskManagerSearchWindow.ActivateSearch(input.Text, input.ForegroundWindow);
        _explorerNavigationCapture.Activate(input.ForegroundWindow);
        RefreshOverlayInputCapture();
    }

    private TaskManagerSearchWindow CreateTaskManagerSearchWindow()
    {
        var window = new TaskManagerSearchWindow();
        window.SearchSessionEnded += OnTaskManagerSearchSessionEnded;
        return window;
    }

    private void OnTaskManagerSearchSessionEnded(object? sender, EventArgs e)
    {
        _explorerNavigationCapture.Deactivate();
        _lastTaskManagerSearchWindow = IntPtr.Zero;
        RefreshOverlayInputCapture();
    }

    private void QueueExplorerActivationAfterRefresh(GlobalTextInputEventArgs input)
    {
        _pendingExplorerActivationInputs.Add(input);
        if (_explorerActivationRefreshPending)
        {
            return;
        }

        _explorerActivationRefreshPending = true;
        var refreshTask = _explorerObservationScheduler?.RequestObservationAsync() ?? Task.CompletedTask;
        _ = ReplayExplorerActivationInputsAsync(refreshTask);
    }

    private async Task ReplayExplorerActivationInputsAsync(Task refreshTask)
    {
        try
        {
            await refreshTask;
        }
        catch (OperationCanceledException)
        {
            _pendingExplorerActivationInputs.Clear();
            _explorerActivationRefreshPending = false;
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning("Could not refresh Explorer before type-to-search: {0}", exception.Message);
        }

        if (IsShuttingDown)
        {
            _pendingExplorerActivationInputs.Clear();
            _explorerActivationRefreshPending = false;
            return;
        }

        var inputs = CoalesceGlobalTextInputs(_pendingExplorerActivationInputs.ToArray());
        _pendingExplorerActivationInputs.Clear();
        _explorerActivationRefreshPending = false;
        foreach (var input in inputs)
        {
            ActivateOrAppendExplorerTypeSearchFromSnapshot(input);
        }
    }

    private void ActivateOrAppendExplorerTypeSearchFromSnapshot(GlobalTextInputEventArgs input)
    {
        var searchPanel = _searchPanel;
        if (ShouldAppendExplorerTypeSearchInput(
                input,
                _lastExplorerTypeSearchWindow,
                searchPanel?.IsVisible == true && searchPanel.IsExplorerTypeSearchActive))
        {
            searchPanel!.AppendExplorerSearchText(input.Text);
            return;
        }

        var request = TryCreateExplorerTypeSearchRequest(input, _explorerTracker);
        if (request is not null)
        {
            if (searchPanel is null)
            {
                return;
            }

            if (_taskManagerSearchWindow?.IsTaskManagerSearchActive == true)
            {
                _taskManagerSearchWindow.DismissSearch();
            }

            _lastExplorerTypeSearchWindow = request.ExplorerWindow;
            searchPanel.ActivateExplorerSearch(
                request.InitialQuery,
                request.CurrentFolder,
                request.ExplorerWindow);
            _explorerNavigationCapture.Activate(request.ExplorerWindow);
            RefreshOverlayInputCapture();
            return;
        }
    }

    private void OnExplorerSearchSessionEnded(object? sender, EventArgs e)
    {
        _explorerNavigationCapture.Deactivate();
        _lastExplorerTypeSearchWindow = IntPtr.Zero;
        RefreshOverlayInputCapture();
    }

    private void OnExplorerQuickMenuOpenStateChanged(object? sender, EventArgs e) =>
        RefreshOverlayInputCapture();

    private void OnResultContextMenuOpenStateChanged(object? sender, EventArgs e) =>
        RefreshOverlayInputCapture();

    private void OnQuickSwitchBarAttachmentChanged(object? sender, EventArgs e) =>
        RefreshOverlayInputCapture();

    private void OnQuickSwitchBarInputCaptureStateChanged(object? sender, EventArgs e) =>
        RefreshOverlayInputCapture();

    private void RefreshOverlayInputCapture()
    {
        var dialogInputWindow = _quickSwitchBar?.IsDialogSearchExpanded == true
            ? _quickSwitchBar.AnchorWindow
            : IntPtr.Zero;
        Interlocked.Exchange(ref _activeDialogQuickSwitchInputWindow, dialogInputWindow);
        if (_globalTextInputService is not null)
        {
            _globalTextInputService.DialogInputWindow =
                _quickSwitchBar?.IsAttached == true
                    ? _quickSwitchBar.AnchorWindow
                    : IntPtr.Zero;
            _globalTextInputService.CaptureOverlayInput = ShouldCaptureOverlayInput(
                dialogInputWindow != IntPtr.Zero,
                _explorerQuickMenu?.IsOpen == true,
                _taskManagerSearchWindow?.IsTaskManagerSearchActive == true,
                _searchPanel?.IsResultContextMenuOpen == true,
                _searchPanel?.IsVisible == true && _searchPanel.IsExplorerTypeSearchActive);
        }
    }

    internal static bool ShouldCaptureOverlayInput(
        bool dialogQuickSwitchExpanded,
        bool explorerQuickMenuOpen,
        bool taskManagerSearchActive,
        bool resultContextMenuOpen,
        bool explorerTypeSearchActive) =>
        dialogQuickSwitchExpanded ||
        explorerQuickMenuOpen ||
        taskManagerSearchActive ||
        resultContextMenuOpen ||
        explorerTypeSearchActive;

    private IntPtr TryCaptureDialogQuickSwitchInput(IntPtr foregroundWindow)
    {
        var dialogInputWindow = Interlocked.CompareExchange(
            ref _activeDialogQuickSwitchInputWindow,
            IntPtr.Zero,
            IntPtr.Zero);
        return ShouldHandleDialogQuickSwitchInput(
                foregroundWindow,
                dialogInputWindow,
                _quickSwitchBar?.WindowHandle ?? IntPtr.Zero)
            ? dialogInputWindow
            : IntPtr.Zero;
    }

    internal static bool ShouldHandleDialogQuickSwitchInput(
        IntPtr foregroundWindow,
        IntPtr activeDialogInputWindow,
        IntPtr quickSwitchWindow = default) =>
        activeDialogInputWindow != IntPtr.Zero &&
        ((quickSwitchWindow != IntPtr.Zero && foregroundWindow == quickSwitchWindow) ||
            GlobalTextInputService.IsConfiguredDialogInputWindow(
                foregroundWindow,
                activeDialogInputWindow));

    internal static bool ShouldAppendExplorerTypeSearchInput(
        GlobalTextInputEventArgs input,
        IntPtr activeExplorerWindow,
        bool explorerSearchPanelVisible)
    {
        ArgumentNullException.ThrowIfNull(input);
        return explorerSearchPanelVisible
            && activeExplorerWindow != IntPtr.Zero
            && input.ForegroundWindow == activeExplorerWindow
            && !string.IsNullOrWhiteSpace(input.Text)
            && !GlobalTextInputService.IsTextEntryControlClass(input.FocusedControlClass);
    }

    internal static bool ShouldDismissExplorerTypeSearch(
        GlobalEscapeInputEventArgs input,
        IntPtr activeExplorerWindow,
        bool explorerSearchPanelVisible)
    {
        ArgumentNullException.ThrowIfNull(input);
        return explorerSearchPanelVisible
            && activeExplorerWindow != IntPtr.Zero
            && input.ForegroundWindow == activeExplorerWindow
            && !GlobalTextInputService.IsTextEntryControlClass(input.FocusedControlClass);
    }

    internal static bool ShouldDismissExplorerTypeSearchForPointer(
        bool explorerSearchPanelVisible,
        bool pointerInsidePanel) =>
        explorerSearchPanelVisible && !pointerInsidePanel;

    internal static bool ShouldCloseResultContextMenus(GlobalPointerButton button) =>
        button != GlobalPointerButton.Right;

    internal static OverlayEscapeTarget GetOverlayEscapeTarget(
        bool taskManagerSearchActive,
        bool explorerSearchActive) =>
        taskManagerSearchActive
            ? OverlayEscapeTarget.TaskManager
            : explorerSearchActive
                ? OverlayEscapeTarget.Explorer
                : OverlayEscapeTarget.None;

    internal static bool ShouldHandleExplorerTypeSearchNavigation(
        GlobalNavigationInputEventArgs input,
        IntPtr activeExplorerWindow,
        bool explorerSearchPanelVisible)
    {
        ArgumentNullException.ThrowIfNull(input);
        return explorerSearchPanelVisible
            && activeExplorerWindow != IntPtr.Zero
            && input.ForegroundWindow == activeExplorerWindow
            && !GlobalTextInputService.IsTextEntryControlClass(input.FocusedControlClass);
    }

    internal static ExplorerTypeSearchRequest? TryCreateExplorerTypeSearchRequest(
        GlobalTextInputEventArgs input,
        IQuickSwitchWindowProvider? explorerWindowProvider)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (explorerWindowProvider is null
            || string.IsNullOrWhiteSpace(input.Text)
            || GlobalTextInputService.IsTextEntryControlClass(input.FocusedControlClass))
        {
            return null;
        }

        var provider = explorerWindowProvider;
        var candidate = provider
            .GetFolderCandidates()
            .FirstOrDefault(item =>
                item.IsForeground
                && item.WindowHandle == input.ForegroundWindow
                && !string.IsNullOrWhiteSpace(item.FolderPath));
        return candidate is null
            ? null
            : new ExplorerTypeSearchRequest(input.Text, candidate.FolderPath, candidate.WindowHandle);
    }

    private void HandleHotkeyPressed(string name)
    {
        if (IsSearchHotkey(name))
        {
            if (_taskManagerSearchWindow?.IsTaskManagerSearchActive == true)
            {
                _taskManagerSearchWindow.DismissSearch();
            }
            if (_searchPanel is not null)
            {
                if (GetSearchHotkeyPanelAction(_searchPanel.IsVisible) == SearchHotkeyPanelAction.Hide)
                {
                    if (_searchPanel.IsExplorerTypeSearchActive)
                    {
                        _searchPanel.DismissExplorerSearch();
                    }
                    else
                    {
                        _searchPanel.Hide();
                    }
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
        using var metrics = PerformanceMetrics.Begin("quick_switch.hotkey");
        try
        {
            DialogAttachmentTarget? capturedDialog;
            using (PerformanceMetrics.MeasureStage("hook.capture_dialog"))
            {
                capturedDialog = await TryCaptureDialogTargetAsync(CancellationToken.None);
            }

            var dialogFolderActivations = CreateDialogFolderActivationsForTarget(capturedDialog);
            IReadOnlyList<QuickSwitchFolderCandidate> candidates;
            using (PerformanceMetrics.MeasureStage("quick_switch.collect_candidates"))
            {
                if (_explorerObservationScheduler is not null)
                {
                    await _explorerObservationScheduler.RequestObservationAsync();
                }

                candidates = ObserveQuickSwitchFolderCandidates(_quickSwitchWindowProvider);
            }

            PerformanceMetrics.SetCounter("candidate_count", candidates.Count);
            DialogJumpResult? directJumpResult = null;
            using (PerformanceMetrics.MeasureStage("quick_switch.direct_jump"))
            {
                var jumped = await TryJumpToFirstQuickSwitchFolderAsync(
                    candidates,
                    dialogFolderActivations.Direct,
                    result => directJumpResult = result,
                    CancellationToken.None);
                if (jumped)
                {
                    if (directJumpResult is not null)
                    {
                        ReportDirectDialogJumpStatus(
                            directJumpResult,
                            result => _quickSwitchBar?.ReportDialogJumpResult(result),
                            message => _trayController?.ShowStatus(message));
                    }

                    metrics.Complete("success", candidates.Count);
                    return;
                }
            }

            if (_quickSwitchBar?.IsAttached == true)
            {
                _quickSwitchBar.Expand();
            }
            else if (_quickSwitchBar is not null)
            {
                await _quickSwitchBar.AttachAsync(
                    candidates,
                    dialogFolderActivations.Panel,
                    capturedDialog?.WindowHandle ?? IntPtr.Zero);
                _quickSwitchBar.Expand();
            }

            metrics.Complete("no_direct_target", candidates.Count);
        }
        catch (Exception exception)
        {
            metrics.Complete("failed");
            Trace.TraceError(exception.ToString());
            _trayController?.ShowStatus($"Could not open Quick Switch: {exception.Message}");
        }
    }

    private void StartDialogAttachmentMonitoring()
    {
        if (_dialogAttachmentTimer is not null)
        {
            return;
        }

        _dialogAttachmentTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = IdleDialogProbeInterval
        };
        _dialogAttachmentTimer.Tick += OnDialogAttachmentTick;
        _dialogAttachmentTimer.Start();
    }

    private void StartForegroundObservationMonitoring()
    {
        if (_foregroundObservationTimer is not null)
        {
            return;
        }

        _lastObservedForegroundWindow = GetForegroundWindow();
        _foregroundObservationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _foregroundObservationTimer.Tick += OnForegroundObservationTick;
        _foregroundObservationTimer.Start();
    }

    private void StopForegroundObservationMonitoring()
    {
        if (_foregroundObservationTimer is null)
        {
            return;
        }

        _foregroundObservationTimer.Tick -= OnForegroundObservationTick;
        _foregroundObservationTimer.Stop();
        _foregroundObservationTimer = null;
    }

    private void OnForegroundObservationTick(object? sender, EventArgs e)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero || foregroundWindow == _lastObservedForegroundWindow)
        {
            return;
        }

        _lastObservedForegroundWindow = foregroundWindow;
        _ = _explorerObservationScheduler?.RequestObservationAsync();
    }

    private void StopDialogAttachmentMonitoring()
    {
        if (_dialogAttachmentTimer is null)
        {
            return;
        }

        _dialogAttachmentTimer.Tick -= OnDialogAttachmentTick;
        _dialogAttachmentTimer.Stop();
        _dialogAttachmentTimer = null;
    }

    private async void OnDialogAttachmentTick(object? sender, EventArgs e)
    {
        if (_dialogAttachmentProbeRunning || IsShuttingDown)
        {
            return;
        }

        var bar = _quickSwitchBar;
        if (ShouldDetachDialogAttachment(
                bar?.IsAttached == true,
                bar?.IsAnchorWindowAlive == true))
        {
            SetDialogProbeInterval(IdleDialogProbeInterval);
            _attachedDialogId = null;
            bar!.Detach();
            return;
        }

        if (ShouldSkipDialogProbeWhileBarActive(
                bar?.IsAttached == true,
                bar?.IsActive == true,
                bar?.IsAnchorWindowAvailable == true))
        {
            SetDialogProbeInterval(AttachedDialogProbeInterval);
            bar!.Reposition();
            return;
        }

        _dialogAttachmentProbeRunning = true;
        try
        {
            var dialog = await TryCaptureDialogTargetAsync(_shutdownCancellation.Token);
            if (dialog is null)
            {
                if (ShouldRetainDialogAttachmentOnProbeMiss(
                        bar?.IsAttached == true,
                        bar?.IsAnchorWindowAvailable == true))
                {
                    SetDialogProbeInterval(AttachedDialogProbeInterval);
                    bar!.Reposition();
                    return;
                }

                SetDialogProbeInterval(IdleDialogProbeInterval);
                _attachedDialogId = null;
                bar?.Detach();
                return;
            }

            if (string.Equals(_attachedDialogId, dialog.Id, StringComparison.Ordinal) &&
                bar?.IsAttached == true)
            {
                SetDialogProbeInterval(AttachedDialogProbeInterval);
                bar.Reposition();
                return;
            }

            var candidates = ObserveQuickSwitchFolderCandidates(_quickSwitchWindowProvider);
            var activations = CreateDialogFolderActivationsForTarget(dialog);
            var automaticFolder = _explorerTracker?.GetRecentlyObservedFolder(TimeSpan.FromSeconds(6));
            if (!string.IsNullOrWhiteSpace(automaticFolder))
            {
                try
                {
                    await activations.Direct(automaticFolder, _shutdownCancellation.Token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Trace.TraceWarning("Could not automatically jump the dialog: {0}", exception.Message);
                }
            }

            if (bar is not null)
            {
                await bar.AttachAsync(candidates, activations.Panel, dialog.WindowHandle);
                _attachedDialogId = dialog.Id;
                SetDialogProbeInterval(AttachedDialogProbeInterval);
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
        finally
        {
            _dialogAttachmentProbeRunning = false;
        }
    }

    internal static bool ShouldRetainDialogAttachmentOnProbeMiss(
        bool isAttached,
        bool anchorWindowAvailable) =>
        isAttached && anchorWindowAvailable;

    internal static bool ShouldDetachDialogAttachment(
        bool isAttached,
        bool anchorWindowAlive) =>
        isAttached && !anchorWindowAlive;

    internal static bool ShouldSkipDialogProbeWhileBarActive(
        bool isAttached,
        bool barIsActive,
        bool anchorWindowAvailable) =>
        isAttached && barIsActive && anchorWindowAvailable;

    private void SetDialogProbeInterval(TimeSpan interval)
    {
        if (_dialogAttachmentTimer is not null && _dialogAttachmentTimer.Interval != interval)
        {
            _dialogAttachmentTimer.Interval = interval;
        }
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

    private async Task<DialogAttachmentTarget?> TryCaptureDialogTargetAsync(
        CancellationToken cancellationToken)
    {
        var target = _dialogBridge is null
            ? null
            : await _dialogBridge.TryCaptureActiveTargetAsync(cancellationToken).ConfigureAwait(false);
        if (_e2eControlOptions is not null && target is NativeHookDialogTarget nativeTarget)
        {
            Volatile.Write(ref _lastE2eHookDialog, nativeTarget.Dialog);
        }
        return target is null
            ? null
            : new DialogAttachmentTarget(target.Id, target.WindowHandle, target);
    }

    private (
        Func<string, CancellationToken, Task<DialogJumpResult>> Direct,
        Func<string, CancellationToken, Task<DialogJumpResult>> Panel) CreateDialogFolderActivationsForTarget(
        DialogAttachmentTarget? target)
    {
        if (target is not null)
        {
            Func<string, CancellationToken, Task<DialogJumpResult>> capturedActivation =
                (folderPath, cancellationToken) =>
                    JumpDialogToFolderAsync(target.Target, folderPath, cancellationToken);
            return (capturedActivation, capturedActivation);
        }

        Func<string, CancellationToken, Task<DialogJumpResult>> unavailable = (_, _) =>
            Task.FromResult(new DialogJumpResult(
                DialogJumpStatus.Failed,
                "No native-hook or dialog-plugin target was captured."));
        return (unavailable, unavailable);
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

        var capturedActivation = capturedDialog is null
            ? activeDialogActivation
            : (folderPath, cancellationToken) =>
                capturedDialogActivation(capturedDialog, folderPath, cancellationToken);
        return (capturedActivation, capturedActivation);
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
        DialogJumpTarget target,
        string folderPath,
        CancellationToken cancellationToken)
    {
        var dialogBridge = _dialogBridge;
        var result = await (dialogBridge is null
            ? Task.FromResult(new DialogJumpResult(DialogJumpStatus.Failed, "Dialog integration is not available."))
            : dialogBridge.JumpToFolderAsync(target, folderPath, cancellationToken)).ConfigureAwait(false);
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

    private async Task<DialogJumpResult> JumpDialogToFolderAsync(
        IntPtr dialogWindow,
        string folderPath,
        CancellationToken cancellationToken)
    {
        var dialogBridge = _dialogBridge;
        var result = await (dialogBridge is null
            ? Task.FromResult(new DialogJumpResult(DialogJumpStatus.Failed, "Dialog integration is not available."))
            : dialogBridge.JumpToFolderAsync(dialogWindow, folderPath, cancellationToken)).ConfigureAwait(false);
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

internal sealed record HotkeyStartupDecision(bool ShouldContinue, string? Message, MessageBoxImage Image);

internal sealed record DialogAttachmentTarget(
    string Id,
    IntPtr WindowHandle,
    DialogJumpTarget Target);

internal sealed record ExplorerTypeSearchRequest(
    string InitialQuery,
    string CurrentFolder,
    IntPtr ExplorerWindow);

internal enum SearchHotkeyPanelAction
{
    Activate,
    Hide
}

internal enum OverlayEscapeTarget
{
    None,
    TaskManager,
    Explorer
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

        tracker.Track(Task.Run(async () =>
        {
            var previousPriority = Thread.CurrentThread.Priority;
            try
            {
                // Keep UI/input threads at Normal; only the indexing worker yields.
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                await work(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    Thread.CurrentThread.Priority = previousPriority;
                }
                catch (ThreadStateException)
                {
                }
            }
        }));
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

    public Task WaitForCompletionAsync(TimeSpan timeout) =>
        WaitForCompletionAsync().WaitAsync(timeout);

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
