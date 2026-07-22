using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ListaryOpen.App.Converters;

public sealed class SearchMatchReasonDisplayConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<string, string> KnownLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["usage"] = "Usage",
            ["name"] = "Name",
            ["path"] = "Path",
            ["pinyin"] = "Pinyin",
            ["recent"] = "Recent",
            ["explorer"] = "Pinned"
        };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var reason = value?.ToString();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return LocalizationManager.Translate("Match");
        }

        reason = reason.Trim();
        if (KnownLabels.TryGetValue(reason, out var label))
        {
            return LocalizationManager.Translate(label);
        }

        return LocalizationManager.Translate(ToReadableLabel(reason));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return DependencyProperty.UnsetValue;
    }

    private static string ToReadableLabel(string reason)
    {
        var words = reason.Split(
            new[] { ' ', '\t', '\r', '\n', '_', '-' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (words.Length == 0)
        {
            return "Match";
        }

        return string.Join(
            " ",
            words.Select(word => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word.ToLowerInvariant())));
    }
}
