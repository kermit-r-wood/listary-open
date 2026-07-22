using System.Globalization;
using System.Windows.Data;
using ListaryOpen.Core.Settings;
using ListaryOpen.Core.Search;

namespace ListaryOpen.App.Converters;

public sealed class SettingsOptionDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var displayText = value switch
        {
        AppLanguage.System => "Follow Windows (recommended)",
        AppLanguage.English => "English",
        AppLanguage.SimplifiedChinese => "简体中文",
        IndexUpdateFrequency.StartupOnly => "When needed (recommended)",
        IndexUpdateFrequency.Every15Minutes => "Every 15 minutes",
        IndexUpdateFrequency.Hourly => "Every hour",
        IndexUpdateFrequency.Every6Hours => "Every 6 hours",
        IndexUpdateFrequency.Daily => "Every day",
        SearchTransliterationMode.Disabled => "Disabled",
        SearchTransliterationMode.ChinesePinyin => "Chinese pinyin",
        SearchTransliterationMode.Multilingual => "Multilingual (recommended)",
        SearchItemTypeFilter.All => "All types",
        SearchItemTypeFilter.Folders => "Folders",
        SearchItemTypeFilter.Files => "Files",
        SearchItemTypeFilter.Documents => "Documents",
        SearchItemTypeFilter.Images => "Images",
        SearchItemTypeFilter.Videos => "Videos",
        SearchDateFilter.AnyTime => "Any time",
        SearchDateFilter.Today => "Today",
        SearchDateFilter.Last7Days => "Last 7 days",
        SearchDateFilter.Last30Days => "Last 30 days",
        SearchDateFilter.LastYear => "Last year",
        QuickMenuAction.QuickAccess => "Quick access",
        QuickMenuAction.ThisPc => "This PC",
        QuickMenuAction.OpenPath => "Open folder or path",
        QuickMenuAction.History => "History submenu",
        QuickMenuAction.OpenFolders => "Opened folders submenu",
        QuickMenuAction.PowerShell => "PowerShell here",
        QuickMenuAction.RunCommand => "Run custom command",
        QuickMenuAction.Commands => "Commands submenu",
        QuickMenuAction.Options => "Open options",
        _ => value?.ToString() ?? string.Empty
        };

        return LocalizationManager.Translate(displayText);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
