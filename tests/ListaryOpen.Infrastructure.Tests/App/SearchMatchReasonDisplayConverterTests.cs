using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchMatchReasonDisplayConverterTests
{
    [Theory]
    [InlineData("usage", "Usage")]
    [InlineData("name", "Name")]
    [InlineData("path", "Path")]
    [InlineData("pinyin", "Pinyin")]
    [InlineData("explorer", "Pinned")]
    public void ConvertMapsKnownRawReasonsToDisplayLabels(string matchReason, string expected)
    {
        var converter = CreateConverter();

        var label = converter.Convert(matchReason, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData("custom_reason", "Custom Reason")]
    [InlineData("dialog-jump", "Dialog Jump")]
    [InlineData("recent document", "Recent Document")]
    public void ConvertFormatsUnknownReasonsAsReadableLabels(string matchReason, string expected)
    {
        var converter = CreateConverter();

        var label = converter.Convert(matchReason, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ConvertUsesGenericLabelForMissingReasons(string? matchReason)
    {
        var converter = CreateConverter();

        var label = converter.Convert(matchReason, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal("Match", label);
    }

    [Fact]
    public void ConvertBackDoesNotTryToWriteDisplayLabelsToMatchReason()
    {
        var converter = CreateConverter();

        var value = converter.ConvertBack("Usage", typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Same(DependencyProperty.UnsetValue, value);
    }

    private static IValueConverter CreateConverter()
    {
        var converterType = Type.GetType(
            "ListaryOpen.App.Converters.SearchMatchReasonDisplayConverter, ListaryOpen.App");

        Assert.NotNull(converterType);
        return Assert.IsAssignableFrom<IValueConverter>(Activator.CreateInstance(converterType));
    }
}
