using ListaryOpen.App.Tray;
using ListaryOpen.Infrastructure.Windows;
using System.IO;
using System.Windows;

namespace ListaryOpen.App;

public partial class App : Application
{
    private ExplorerTracker? _explorerTracker;
    private HotkeyService? _hotkeyService;
    private SearchPanel? _searchPanel;
    private TrayController? _trayController;

    internal static bool IsShuttingDown =>
        Current?.Dispatcher.HasShutdownStarted == true ||
        Current?.Dispatcher.HasShutdownFinished == true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settingsWindow = new MainWindow();
        _explorerTracker = new ExplorerTracker();
        _searchPanel = new SearchPanel();
        _trayController = new TrayController(settingsWindow);
        _hotkeyService = new HotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        _hotkeyService.RegisterDefaults();

        MainWindow = settingsWindow;
        settingsWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_hotkeyService is not null)
        {
            _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
            _hotkeyService.Dispose();
        }

        _trayController?.Dispose();

        base.OnExit(e);
    }

    private void OnHotkeyPressed(object? sender, string name)
    {
        if (string.Equals(name, "Search", StringComparison.OrdinalIgnoreCase))
        {
            _searchPanel?.ActivateSearch();
            return;
        }

        if (IsDialogHotkey(name))
        {
            var lastFolder = _explorerTracker?.LastFolder;
            _searchPanel?.ActivateFolderSearch(
                !string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder)
                    ? lastFolder
                    : null);
        }
    }

    private static bool IsDialogHotkey(string name)
    {
        return string.Equals(name, "Dialog", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Ctrl+G", StringComparison.OrdinalIgnoreCase);
    }
}
