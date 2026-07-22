using System.Globalization;
using ListaryOpen.App.Converters;
using ListaryOpen.Core.Settings;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SettingsOptionDisplayConverterTests
{
    [Theory]
    [InlineData(IndexUpdateFrequency.StartupOnly, "When needed (recommended)")]
    [InlineData(IndexUpdateFrequency.Every15Minutes, "Every 15 minutes")]
    [InlineData(IndexUpdateFrequency.Every6Hours, "Every 6 hours")]
    public void FrequencyNamesAreReadable(IndexUpdateFrequency value, string expected)
    {
        var converter = new SettingsOptionDisplayConverter();

        Assert.Equal(expected, converter.Convert(value, typeof(string), null!, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(QuickMenuAction.RunCommand, "Run custom command")]
    [InlineData(QuickMenuAction.OpenFolders, "Opened folders submenu")]
    public void MenuActionNamesAreReadable(QuickMenuAction value, string expected)
    {
        var converter = new SettingsOptionDisplayConverter();

        Assert.Equal(expected, converter.Convert(value, typeof(string), null!, CultureInfo.InvariantCulture));
    }
}
