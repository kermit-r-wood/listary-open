using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ListaryOpen.App.ViewModels;

namespace ListaryOpen.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public MainWindow()
        : this(new SettingsViewModel())
    {
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key is Key.Back or Key.Delete or Key.Escape)
        {
            textBox.Text = string.Empty;
            return;
        }

        var keyText = FormatHotkeyKey(key);
        if (keyText is null)
        {
            return;
        }

        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0)
        {
            return;
        }

        parts.Add(keyText);
        textBox.Text = string.Join("+", parts);
        textBox.CaretIndex = textBox.Text.Length;
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    internal static string? FormatHotkeyKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((int)(key - Key.NumPad0)).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Space => "Space",
        _ => null
    };

    public MainWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        LocalizationManager.LanguageChanged += LocalizationManager_LanguageChanged;
        ApplyRoundedChromeForWindowState();
    }

    private void LocalizationManager_LanguageChanged(object? sender, EventArgs e) =>
        _viewModel.RefreshLocalization();

    private void MainWindow_SourceInitialized(object? sender, EventArgs e) =>
        NativeWindowCorner.ClearRoundedChrome(this);

    private void MainWindow_StateChanged(object? sender, EventArgs e) =>
        ApplyRoundedChromeForWindowState();

    private void MainWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BorderlessWindowInteraction.TryBeginDrag(this, e);

    private void SettingsTitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            e.Handled = true;
            return;
        }

        BorderlessWindowInteraction.TryBeginDrag(this, e);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMaximized();

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void ApplyRoundedChromeForWindowState()
    {
        if (WindowChromeBorder is null || MaximizeButton is null || SettingsTitleBar is null)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowChromeBorder.Margin = new Thickness(0);
            WindowChromeBorder.CornerRadius = new CornerRadius(0);
            SettingsTitleBar.CornerRadius = new CornerRadius(0);
            MaximizeButton.Content = "\uE923"; // ChromeRestore
            MaximizeButton.ToolTip = "Restore";
        }
        else
        {
            WindowChromeBorder.Margin = new Thickness(10);
            WindowChromeBorder.CornerRadius = new CornerRadius(12);
            SettingsTitleBar.CornerRadius = new CornerRadius(12, 12, 0, 0);
            MaximizeButton.Content = "\uE922"; // ChromeMaximize
            MaximizeButton.ToolTip = "Maximize";
        }

        NativeWindowCorner.ClearRoundedChrome(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        LocalizationManager.LanguageChanged -= LocalizationManager_LanguageChanged;
        base.OnClosed(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
