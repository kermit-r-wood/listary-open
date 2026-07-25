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
    public void SearchPanelUsesWpfAntiAliasedRoundedChrome()
    {
        var searchXaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "SearchPanel.xaml"));
        var searchPanel = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "SearchPanel.xaml.cs"));
        var nativeCorner = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "NativeWindowCorner.cs"));

        // Per-pixel alpha + Border.CornerRadius (not SetWindowRgn) keeps Win10 corners smooth.
        Assert.Contains("AllowsTransparency=\"True\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"Transparent\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WindowChromeBorder\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"12\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("SnapsToDevicePixels=\"False\"", searchXaml, StringComparison.Ordinal);
        Assert.Contains("NativeWindowCorner.ClearRoundedChrome", searchPanel, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeWindowCorner.ApplyRounded", searchPanel);
        Assert.Contains("ClearRoundedChrome", nativeCorner, StringComparison.Ordinal);
        Assert.Contains("AllowsTransparency", nativeCorner, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskManagerSearchStillAppliesNativeRoundedCorners()
    {
        var taskManagerSearch = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "TaskManagerSearchWindow.xaml.cs"));
        var nativeCorner = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "NativeWindowCorner.cs"));

        Assert.Contains("ApplyBorderlessWindowChrome()", taskManagerSearch);
        Assert.Contains("NativeWindowCorner.ApplyRounded", taskManagerSearch);
        Assert.Contains("OnRenderSizeChanged", taskManagerSearch);
        Assert.Contains("DwmWindowCornerPreferenceRound = 2", nativeCorner);
        Assert.Contains("DwmSetWindowAttribute", nativeCorner);
        Assert.Contains("CreateRoundRectRgn", nativeCorner);
        Assert.Contains("SetWindowRgn", nativeCorner);
    }

    [Fact]
    public void BorderlessOverlayWindowsSupportMouseDragOnNonInteractiveChrome()
    {
        var searchPanel = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "SearchPanel.xaml.cs"));
        var searchXaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "SearchPanel.xaml"));
        var taskManagerXaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "TaskManagerSearchWindow.xaml"));
        var taskManager = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "TaskManagerSearchWindow.xaml.cs"));
        var interaction = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "BorderlessWindowInteraction.cs"));

        Assert.Contains("PreviewMouseLeftButtonDown=\"SearchPanel_PreviewMouseLeftButtonDown\"", searchXaml);
        Assert.Contains("BorderlessWindowInteraction.TryBeginDrag", searchPanel);
        Assert.Contains("RememberCompactGlobalSearchPosition", searchPanel);
        Assert.Contains("CalculateCompactGlobalSearchPosition", searchPanel);
        Assert.Contains("SuppressDeactivateDismiss", searchPanel);
        Assert.Contains("_suppressDeactivateDismissUntilTick", searchPanel);
        Assert.Contains("PreviewMouseLeftButtonDown=\"TaskManagerSearchWindow_PreviewMouseLeftButtonDown\"", taskManagerXaml);
        Assert.Contains("BorderlessWindowInteraction.TryBeginDrag", taskManager);
        Assert.Contains("DragMove()", interaction);
        Assert.Contains("ShouldBeginWindowDrag", interaction);
        Assert.Contains("IsDragBlockedSource", interaction);
    }

    [Fact]
    public void QuickSwitchBarUsesTransparentRoundedChromeWithoutFreeDrag()
    {
        // Anchored under dialogs: WPF transparency + CornerRadius (not free drag).
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "QuickSwitchBarWindow.xaml"));
        Assert.Contains("AllowsTransparency=\"True\"", xaml);
        Assert.Contains("Background=\"Transparent\"", xaml);
        Assert.Contains("CornerRadius=", xaml);
        Assert.DoesNotContain("DragMove", File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "QuickSwitchBarWindow.xaml.cs")));
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
