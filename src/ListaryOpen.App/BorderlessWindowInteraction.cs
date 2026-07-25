using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ListaryOpen.App;

/// <summary>
/// Shared helpers for borderless overlay windows: drag chrome without blocking inputs.
/// </summary>
internal static class BorderlessWindowInteraction
{
    internal static bool TryBeginDrag(Window window, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!ShouldBeginWindowDrag(e))
        {
            return false;
        }

        try
        {
            window.DragMove();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool ShouldBeginWindowDrag(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed)
        {
            return false;
        }

        return !IsDragBlockedSource(e.OriginalSource as DependencyObject);
    }

    internal static bool IsDragBlockedSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is TextBoxBase or PasswordBox or ButtonBase or ComboBox or ListBoxItem
                or Selector or ScrollBar or Thumb or MenuBase or MenuItem or Slider
                or DataGrid or Calendar or DatePicker)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current) =>
        current is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);
}
