using Hardcodet.Wpf.TaskbarNotification;
using System.Windows;
using System.Windows.Controls;

namespace ListaryOpen.App.Tray;

public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _openSettingsMenuItem;
    private readonly MenuItem _exitMenuItem;
    private readonly Window _settingsWindow;

    public TrayController(Window settingsWindow)
    {
        _settingsWindow = settingsWindow;
        _openSettingsMenuItem = new MenuItem { Header = "Open Settings" };
        _exitMenuItem = new MenuItem { Header = "Exit" };

        _openSettingsMenuItem.Click += OnOpenSettingsClick;
        _exitMenuItem.Click += OnExitClick;

        _icon = new TaskbarIcon
        {
            ToolTipText = "ListaryOpen",
            ContextMenu = CreateContextMenu()
        };
        _icon.TrayMouseDoubleClick += OnTrayMouseDoubleClick;
    }

    public void Dispose()
    {
        _icon.TrayMouseDoubleClick -= OnTrayMouseDoubleClick;
        _openSettingsMenuItem.Click -= OnOpenSettingsClick;
        _exitMenuItem.Click -= OnExitClick;
        _icon.Dispose();
    }

    private ContextMenu CreateContextMenu()
    {
        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(_openSettingsMenuItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(_exitMenuItem);

        return contextMenu;
    }

    private void OnTrayMouseDoubleClick(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    private static void OnExitClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private void ShowSettingsWindow()
    {
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }
}
