namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SearchPanelXamlTests
{
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
        Assert.Contains("Text=\"{Binding QueryPlaceholderText}\"", xaml);
        Assert.Contains("Text=\"{Binding Record.Name}\"", xaml);
        Assert.Contains("Text=\"{Binding Record.ParentPath}\"", xaml);
        Assert.Contains(
            "Text=\"{Binding MatchReason, Converter={StaticResource SearchMatchReasonDisplayConverter}}\"",
            xaml);
        Assert.DoesNotContain("Text=\"{Binding MatchReason}\"", xaml);
        Assert.Contains("Record.IsDirectory", xaml);
        Assert.Contains("SearchPanelResultListStyle", xaml);
    }

    [Fact]
    public void SearchQueryBoxUsesCompactInputSizing()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "SearchPanel.xaml"));

        Assert.Contains("<Setter Property=\"Height\" Value=\"40\" />", xaml);
        Assert.Contains("<Setter Property=\"Padding\" Value=\"14,6\" />", xaml);
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"18\" />", xaml);
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
