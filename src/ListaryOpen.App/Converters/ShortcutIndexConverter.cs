using System.Globalization;
using System.Windows.Data;

namespace ListaryOpen.App.Converters;

public sealed class ShortcutIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int index && index is >= 0 and < 9 ? $"Ctrl+{index + 1}" : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
