using Hardcodet.Wpf.TaskbarNotification;
using System.Windows;

namespace ListaryOpen.App.Tray;

public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayController(Window settingsWindow)
    {
        _icon = new TaskbarIcon { ToolTipText = "ListaryOpen" };
        _icon.TrayMouseDoubleClick += (_, _) => settingsWindow.Show();
    }

    public void Dispose()
    {
        _icon.Dispose();
    }
}
