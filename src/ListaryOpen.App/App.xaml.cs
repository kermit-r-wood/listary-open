using ListaryOpen.App.Tray;
using ListaryOpen.Infrastructure.Windows;
using System.Windows;

namespace ListaryOpen.App;

public partial class App : Application
{
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
        }
    }
}
