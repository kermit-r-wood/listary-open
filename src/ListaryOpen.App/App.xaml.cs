using ListaryOpen.App.Tray;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Indexing;
using ListaryOpen.Infrastructure.Search;
using ListaryOpen.Infrastructure.Windows;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ListaryOpen.App;

public partial class App : Application
{
    private DialogBridge? _dialogBridge;
    private FallbackIndexProvider? _fallbackIndexProvider;
    private ExplorerTracker? _explorerTracker;
    private HotkeyService? _hotkeyService;
    private NtfsIndexProvider? _ntfsIndexProvider;
    private SearchPanel? _searchPanel;
    private SqliteSearchIndex? _searchIndex;
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
            if (!await InitializeApplicationServicesAsync())
            {
                Shutdown(1);
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            MessageBox.Show(
                "ListaryOpen could not start.",
                "ListaryOpen",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hotkeyService is not null)
        {
            _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
            _hotkeyService.Dispose();
        }

        _trayController?.Dispose();
        DisposeSearchIndex();

        base.OnExit(e);
    }

    private async Task<bool> InitializeApplicationServicesAsync()
    {
        _searchIndex = await SqliteSearchIndex.OpenAsync(CreateIndexDatabasePath(), CancellationToken.None);
        _fallbackIndexProvider = new FallbackIndexProvider();
        _ntfsIndexProvider = new NtfsIndexProvider(new ElevatedIndexerClient());
        _volumeIndexer = new VolumeIndexer(new IIndexProvider[]
        {
            _ntfsIndexProvider,
            _fallbackIndexProvider
        });

        _dialogAutomation = new WindowsDialogAutomation();
        _dialogBridge = new DialogBridge(_dialogAutomation);

        var settingsWindow = new MainWindow();
        _explorerTracker = new ExplorerTracker();
        _searchPanel = new SearchPanel(new SearchPanelViewModel(_searchIndex));
        _trayController = new TrayController(settingsWindow);
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
        return true;
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
