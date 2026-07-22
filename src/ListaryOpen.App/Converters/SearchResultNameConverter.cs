using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace ListaryOpen.App.Converters;

public sealed class SearchResultNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string ?? string.Empty;
        return string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(path);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SearchResultKindConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var path = value as string ?? string.Empty;
        return string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)
            ? "App"
            : "File";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
