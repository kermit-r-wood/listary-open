using Hardcodet.Wpf.TaskbarNotification;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ListaryOpen.App.Tray;

public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _openSettingsMenuItem;
    private readonly MenuItem _reindexMenuItem;
    private readonly MenuItem _exitMenuItem;
    private readonly Action _reindex;
    private readonly Window _settingsWindow;

    public TrayController(Window settingsWindow, Action? reindex = null)
    {
        _settingsWindow = settingsWindow;
        _reindex = reindex ?? (() => { });
        _openSettingsMenuItem = new MenuItem { Header = "Open Settings" };
        _reindexMenuItem = new MenuItem { Header = "Reindex" };
        _exitMenuItem = new MenuItem { Header = "Exit" };

        _openSettingsMenuItem.Click += OnOpenSettingsClick;
        _reindexMenuItem.Click += OnReindexClick;
        _exitMenuItem.Click += OnExitClick;

        _icon = new TaskbarIcon
        {
            IconSource = Application.Current.TryFindResource("ListaryOpenIcon") as ImageSource,
            ToolTipText = "ListaryOpen",
            ContextMenu = CreateContextMenu()
        };
        _icon.TrayMouseDoubleClick += OnTrayMouseDoubleClick;
    }

    public void Dispose()
    {
        _icon.TrayMouseDoubleClick -= OnTrayMouseDoubleClick;
        _openSettingsMenuItem.Click -= OnOpenSettingsClick;
        _reindexMenuItem.Click -= OnReindexClick;
        _exitMenuItem.Click -= OnExitClick;
        _icon.Dispose();
    }

    private ContextMenu CreateContextMenu()
    {
        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(_openSettingsMenuItem);
        contextMenu.Items.Add(_reindexMenuItem);
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

    private void OnReindexClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _reindex();
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }

    private void ShowSettingsWindow()
    {
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }
}
