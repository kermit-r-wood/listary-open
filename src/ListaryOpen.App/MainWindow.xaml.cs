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
    }

    private void LocalizationManager_LanguageChanged(object? sender, EventArgs e) =>
        _viewModel.RefreshLocalization();

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
