using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using ListaryOpen.Infrastructure.Windows;
using ListaryOpen.Core.Settings;

namespace ListaryOpen.App;

internal sealed class ExplorerQuickMenu
{
    private readonly Action _showSettings;
    private readonly Action<string> _reportError;
    private readonly List<string> _history = new();
    private ContextMenu? _openMenu;
    private Window? _menuOwner;
    private DispatcherTimer? _foregroundTimer;
    private IntPtr _explorerWindow;

    public ExplorerQuickMenu(Action showSettings, Action<string> reportError)
    {
        _showSettings = showSettings ?? throw new ArgumentNullException(nameof(showSettings));
        _reportError = reportError ?? throw new ArgumentNullException(nameof(reportError));
    }

    internal event EventHandler? OpenStateChanged;

    public void Show(
        string currentFolder,
        IReadOnlyList<QuickSwitchFolderCandidate> openFolders,
        IReadOnlyList<QuickMenuEntry> configuredEntries,
        int screenX,
        int screenY,
        IntPtr explorerWindow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFolder);
        ArgumentNullException.ThrowIfNull(openFolders);
        ArgumentNullException.ThrowIfNull(configuredEntries);

        RememberFolder(currentFolder);
        CloseOpenMenu();

        var dpiScale = GetDpiForWindow(explorerWindow) / 96d;
        if (dpiScale <= 0)
        {
            dpiScale = 1;
        }

        var placementTarget = new Border();
        var owner = new Window
        {
            Width = 1,
            Height = 1,
            Left = screenX / dpiScale,
            Top = screenY / dpiScale,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Opacity = 0.01,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            Content = placementTarget
        };
        var menu = new ContextMenu
        {
            Placement = PlacementMode.RelativePoint,
            PlacementTarget = placementTarget,
            HorizontalOffset = 0,
            VerticalOffset = 0,
            StaysOpen = false
        };

        foreach (var entry in configuredEntries.Where(entry => entry.Enabled))
        {
            AddConfiguredEntry(menu, entry, currentFolder, openFolders);
        }
        owner.Deactivated += (_, _) => owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(_menuOwner, owner) && !owner.IsActive)
            {
                CloseOpenMenu();
            }
        }));
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMenu, menu))
            {
                _openMenu = null;
            }

            if (ReferenceEquals(_menuOwner, owner))
            {
                _menuOwner = null;
            }

            if (owner.IsVisible)
            {
                owner.Close();
            }


            OpenStateChanged?.Invoke(this, EventArgs.Empty);
        };
        _menuOwner = owner;
        _openMenu = menu;
        _explorerWindow = explorerWindow;
        owner.Show();
        owner.Activate();
        menu.IsOpen = true;
        OpenStateChanged?.Invoke(this, EventArgs.Empty);
        StartForegroundMonitoring();
    }

    private void AddConfiguredEntry(
        ContextMenu menu,
        QuickMenuEntry entry,
        string currentFolder,
        IReadOnlyList<QuickSwitchFolderCandidate> openFolders)
    {
        switch (entry.Action)
        {
            case QuickMenuAction.QuickAccess:
                var quickAccess = CreateMenuItem(entry.Title, "\uE72A");
                AddFolderItem(quickAccess, "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                AddFolderItem(quickAccess, "Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
                AddFolderItem(quickAccess, "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                AddFolderItem(quickAccess, "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                menu.Items.Add(quickAccess);
                break;
            case QuickMenuAction.ThisPc:
                menu.Items.Add(CreateActionItem(entry.Title, "\uE7F4", () => OpenShellLocation("shell:MyComputerFolder")));
                break;
            case QuickMenuAction.OpenPath:
                var path = Environment.ExpandEnvironmentVariables(entry.Path);
                menu.Items.Add(CreateActionItem(entry.Title, "\uE8B7", () => OpenFolder(path)));
                break;
            case QuickMenuAction.History:
                var history = CreateMenuItem(entry.Title, "\uE81C");
                foreach (var folder in _history) AddFolderItem(history, DisplayName(folder), folder);
                history.IsEnabled = history.Items.Count > 0;
                menu.Items.Add(history);
                break;
            case QuickMenuAction.OpenFolders:
                var opened = CreateMenuItem(entry.Title, "\uE838");
                foreach (var candidate in openFolders.Where(candidate => Directory.Exists(candidate.FolderPath)).DistinctBy(candidate => candidate.FolderPath, StringComparer.OrdinalIgnoreCase))
                    AddFolderItem(opened, DisplayName(candidate.FolderPath), candidate.FolderPath);
                opened.IsEnabled = opened.Items.Count > 0;
                menu.Items.Add(opened);
                break;
            case QuickMenuAction.PowerShell:
                menu.Items.Add(CreateActionItem(entry.Title, "\uE756", () => OpenPowerShell(string.IsNullOrWhiteSpace(entry.Path) ? currentFolder : Environment.ExpandEnvironmentVariables(entry.Path))));
                break;
            case QuickMenuAction.RunCommand:
                menu.Items.Add(CreateActionItem(entry.Title, "\uE756", () => RunConfiguredCommand(entry, currentFolder)));
                break;
            case QuickMenuAction.Commands:
                var commands = CreateMenuItem(entry.Title, "\uE713");
                commands.Items.Add(CreateActionItem("Switch to Last Opened Folder", "\uE72A", () => OpenFolder(_history.Skip(1).FirstOrDefault() ?? currentFolder)));
                commands.Items.Add(CreateActionItem("Copy Folder Path", "\uE8C8", () => CopyFolderPath(currentFolder)));
                commands.Items.Add(CreateActionItem("PowerShell", "\uE756", () => OpenPowerShell(currentFolder)));
                menu.Items.Add(commands);
                break;
            case QuickMenuAction.Options:
                menu.Items.Add(CreateActionItem(entry.Title, "\uE713", _showSettings));
                break;
        }
    }

    private void RunConfiguredCommand(QuickMenuEntry entry, string currentFolder)
    {
        try
        {
            var startInfo = CreateConfiguredCommandStartInfo(entry, currentFolder);
            if (string.IsNullOrWhiteSpace(startInfo.FileName))
            {
                _reportError($"The command '{entry.Title}' does not have a program path.");
                return;
            }

            Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or IOException)
        {
            _reportError($"Could not run '{entry.Title}': {exception.Message}");
        }
    }

    internal static ProcessStartInfo CreateConfiguredCommandStartInfo(QuickMenuEntry entry, string currentFolder)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFolder);

        var fileName = ExpandMenuValue(entry.Path, currentFolder);
        var arguments = ExpandMenuValue(entry.Arguments, currentFolder);
        var workingDirectory = string.IsNullOrWhiteSpace(entry.WorkingDirectory)
            ? currentFolder
            : ExpandMenuValue(entry.WorkingDirectory, currentFolder);
        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = true,
            Verb = entry.RunAsAdmin ? "runas" : string.Empty,
            WindowStyle = entry.Silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
    }

    private static string ExpandMenuValue(string value, string currentFolder) =>
        Environment.ExpandEnvironmentVariables(value ?? string.Empty)
            .Replace("%CURRENT_FOLDER%", currentFolder, StringComparison.OrdinalIgnoreCase);

    internal bool IsOpen => _openMenu?.IsOpen == true;

    internal bool ContainsScreenPoint(int screenX, int screenY)
    {
        var menu = _openMenu;
        if (menu?.IsOpen != true)
        {
            return false;
        }

        try
        {
            var topLeft = menu.PointToScreen(new Point(0, 0));
            var bottomRight = menu.PointToScreen(new Point(menu.ActualWidth, menu.ActualHeight));
            if (screenX >= topLeft.X && screenX < bottomRight.X &&
                screenY >= topLeft.Y && screenY < bottomRight.Y)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }

        var pointedWindow = WindowFromPoint(new NativeScreenPoint(screenX, screenY));
        if (pointedWindow == IntPtr.Zero ||
            GetWindowThreadProcessId(pointedWindow, out var processId) == 0 ||
            processId != Environment.ProcessId)
        {
            return false;
        }

        // WPF renders each submenu in a separate popup HWND. Treat all ListaryOpen
        // popup hits as inside here; foreground monitoring still closes the menu
        // when another ListaryOpen top-level window is activated.
        return true;
    }

    internal void CloseOpenMenu()
    {
        var menu = _openMenu;
        var owner = _menuOwner;
        _openMenu = null;
        _menuOwner = null;
        _explorerWindow = IntPtr.Zero;
        StopForegroundMonitoring();
        if (menu is not null)
        {
            menu.IsOpen = false;
        }

        if (owner?.IsVisible == true)
        {
            owner.Close();
        }


        OpenStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartForegroundMonitoring()
    {
        StopForegroundMonitoring();
        _foregroundTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _foregroundTimer.Tick += OnForegroundTimerTick;
        _foregroundTimer.Start();
    }

    private void StopForegroundMonitoring()
    {
        if (_foregroundTimer is null)
        {
            return;
        }

        _foregroundTimer.Stop();
        _foregroundTimer.Tick -= OnForegroundTimerTick;
        _foregroundTimer = null;
    }

    private void OnForegroundTimerTick(object? sender, EventArgs e)
    {
        if (!IsOpen)
        {
            StopForegroundMonitoring();
            return;
        }

        var ownerHandle = _menuOwner is null
            ? IntPtr.Zero
            : new WindowInteropHelper(_menuOwner).Handle;
        var foregroundWindow = GetForegroundWindow();
        if (ShouldDismissForForeground(foregroundWindow, _explorerWindow, ownerHandle) &&
            !IsContextMenuWindow(foregroundWindow))
        {
            CloseOpenMenu();
        }
    }

    private static bool IsContextMenuWindow(IntPtr window)
    {
        if (window == IntPtr.Zero ||
            GetWindowThreadProcessId(window, out var processId) == 0 ||
            processId != Environment.ProcessId)
        {
            return false;
        }

        var className = new char[32];
        var length = GetClassName(window, className, className.Length);
        return length > 0 && string.Equals(
            new string(className, 0, length),
            "#32768",
            StringComparison.Ordinal);
    }

    internal static bool ShouldDismissForForeground(
        IntPtr foregroundWindow,
        IntPtr explorerWindow,
        IntPtr ownerWindow) =>
        foregroundWindow != IntPtr.Zero &&
        foregroundWindow != explorerWindow &&
        foregroundWindow != ownerWindow;

    private void RememberFolder(string folder)
    {
        _history.RemoveAll(item => string.Equals(item, folder, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, folder);
        if (_history.Count > 10)
        {
            _history.RemoveRange(10, _history.Count - 10);
        }
    }

    private void AddFolderItem(MenuItem parent, string header, string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            parent.Items.Add(CreateActionItem(header, "\uE8B7", () => OpenFolder(folder)));
        }
    }

    private static MenuItem CreateMenuItem(string header, string glyph) => new()
    {
        Header = header,
        Icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static MenuItem CreateActionItem(string header, string glyph, Action action)
    {
        var item = CreateMenuItem(header, glyph);
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenFolder(string folder) => RunAction(() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folder}\"",
            UseShellExecute = true
        }));

    private void OpenShellLocation(string location) => RunAction(() =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = location,
            UseShellExecute = true
        }));

    private void OpenPowerShell(string folder) => RunAction(() =>
        Process.Start(CreatePowerShellStartInfo(folder)));

    internal static ProcessStartInfo CreatePowerShellStartInfo(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = folder,
            UseShellExecute = true
        };
    }

    private void CopyFolderPath(string folder) => RunAction(() => Clipboard.SetText(folder));

    private void RunAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or ExternalException)
        {
            _reportError(exception.Message);
        }
    }

    private static string DisplayName(string folder)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return string.IsNullOrWhiteSpace(name) ? folder : name;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeScreenPoint(int X, int Y);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativeScreenPoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, char[] className, int maximumCount);
}

internal static class ExplorerQuickMenuHitTest
{
    private const uint GaRoot = 2;

    public static bool TryGetExplorerWindow(int screenX, int screenY, out IntPtr explorerWindow)
    {
        var pointedWindow = WindowFromPoint(new NativePoint(screenX, screenY));
        explorerWindow = pointedWindow == IntPtr.Zero ? IntPtr.Zero : GetAncestor(pointedWindow, GaRoot);
        if (explorerWindow == IntPtr.Zero)
        {
            return false;
        }

        var className = new char[64];
        var length = GetClassName(explorerWindow, className, className.Length);
        if (length <= 0)
        {
            return false;
        }

        var value = new string(className, 0, length);
        return string.Equals(value, "CabinetWClass", StringComparison.Ordinal) ||
            string.Equals(value, "ExploreWClass", StringComparison.Ordinal);
    }

    public static bool IsExplorerItemsBackground(int screenX, int screenY)
    {
        try
        {
            var element = AutomationElement.FromPoint(new Point(screenX, screenY));
            for (var depth = 0; element is not null && depth < 16; depth++)
            {
                var controlType = element.Current.ControlType;
                if (controlType == ControlType.DataItem ||
                    controlType == ControlType.ListItem ||
                    controlType == ControlType.TreeItem)
                {
                    return false;
                }

                if (controlType == ControlType.List ||
                    string.Equals(element.Current.AutomationId, "ItemsView", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(element.Current.ClassName, "SysListView32", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(element.Current.ClassName, "UIItemsView", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, char[] className, int maximumCount);
}
