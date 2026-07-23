namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchPanelXamlTests
{
    [Fact]
    public void GlobalSearchExposesStableAutomationIdsForPackagedBlackboxOracles()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "SearchPanel.xaml"));

        Assert.Contains("AutomationProperties.AutomationId=\"GlobalSearchWindow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"GlobalSearchQuery\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"GlobalSearchResults\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"GlobalSearchPreview\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"GlobalSearchStatus\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchPanelBindsModePlaceholderAndRicherResultFields()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "SearchPanel.xaml"));

        Assert.Contains("x:Name=\"SearchPanelRoot\"", xaml);
        Assert.Contains("xmlns:converters=\"clr-namespace:ListaryOpen.App.Converters\"", xaml);
        Assert.Contains(
            "<converters:SearchMatchReasonDisplayConverter x:Key=\"SearchMatchReasonDisplayConverter\" />",
            xaml);
        Assert.Contains("Text=\"{Binding ModeDisplayText}\"", xaml);
        Assert.Contains("Text=\"{Binding PerformanceText}\"", xaml);
        Assert.Contains("Text=\"{Binding QueryPlaceholderText}\"", xaml);
        Assert.Contains("SearchResultNameConverter", xaml);
        Assert.Contains(
            "Text=\"{Binding Record.FullPath, Converter={StaticResource SearchResultNameConverter}}\"",
            xaml);
        Assert.Contains("Text=\"{Binding Record.ParentPath}\"", xaml);
        Assert.Contains(
            "Text=\"{Binding MatchReason, Converter={StaticResource SearchMatchReasonDisplayConverter}}\"",
            xaml);
        Assert.DoesNotContain("Text=\"{Binding MatchReason}\"", xaml);
        Assert.Contains("Record.IsDirectory", xaml);
        Assert.Contains("SearchPanelResultListStyle", xaml);
        Assert.Contains("PreviewMouseRightButtonDown=\"ResultsList_PreviewMouseRightButtonDown\"", xaml);
        Assert.Contains("PreviewMouseRightButtonUp=\"ResultsList_PreviewMouseRightButtonUp\"", xaml);
        Assert.Contains("PreviewMouseLeftButtonDown=\"ResultsList_PreviewMouseLeftButtonDown\"", xaml);
        Assert.Contains("StaysOpen=\"False\"", xaml);
        Assert.Contains("Header=\"Show in File Explorer\"", xaml);
        Assert.Contains("Header=\"Copy full path\"", xaml);
        Assert.Contains("IsQuickSwitchBarCollapsed", xaml);
        Assert.DoesNotContain("QueryBox_PreviewMouseLeftButtonDown", xaml);
        Assert.Contains("AreResultsVisible", xaml);
    }

    [Fact]
    public void SearchQueryBoxUsesSharedRoundedOverlayStyle()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "SearchPanel.xaml"));

        Assert.Contains("<Setter Property=\"Height\" Value=\"48\" />", xaml);
        Assert.Contains("x:Key=\"OverlaySearchInputChromeStyle\"", xaml);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"10\" />", xaml);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"0\" />", xaml);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"15\" />", xaml);
        Assert.Contains("<Setter Property=\"CaretBrush\" Value=\"{DynamicResource Brush.Accent}\" />", xaml);
    }

    [Fact]
    public void BorderlessSearchWindowsApplyNativeRoundedCorners()
    {
        var searchPanel = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "SearchPanel.xaml.cs"));
        var taskManagerSearch = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "TaskManagerSearchWindow.xaml.cs"));
        var nativeCorner = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "NativeWindowCorner.cs"));

        Assert.Contains("NativeWindowCorner.ApplyRounded(this);", searchPanel);
        Assert.Contains("NativeWindowCorner.ApplyRounded(this);", taskManagerSearch);
        Assert.Contains("DwmWindowCornerPreferenceRound = 2", nativeCorner);
        Assert.Contains("DwmSetWindowAttribute", nativeCorner);
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            Path.Combine(segments)));
    }
}
