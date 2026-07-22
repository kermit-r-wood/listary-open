namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class ControlsXamlTests
{
    [Fact]
    public void ActionButtonStyleUsesCustomTemplateForDisabledState()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "Controls.xaml"));

        Assert.Contains("x:Key=\"ActionButtonStyle\"", xaml);
        Assert.Contains("<ControlTemplate TargetType=\"Button\">", xaml);
        Assert.Contains("x:Name=\"ButtonBorder\"", xaml);
        Assert.Contains("<Trigger Property=\"IsEnabled\" Value=\"False\">", xaml);
        Assert.Contains("Brush.SurfaceMuted", xaml);
        Assert.Contains("Brush.TextMuted", xaml);
    }

    [Fact]
    public void CommonControlsUseDynamicThemeBrushes()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "Controls.xaml"));

        Assert.Contains("<Style TargetType=\"{x:Type TextBox}\">", xaml);
        Assert.Contains("<Style TargetType=\"{x:Type ComboBox}\">", xaml);
        Assert.Contains("<Style TargetType=\"{x:Type CheckBox}\">", xaml);
        Assert.Contains("<Style TargetType=\"{x:Type ListBox}\">", xaml);
        Assert.Contains("<Style TargetType=\"{x:Type ContextMenu}\">", xaml);
        Assert.Contains("<Style TargetType=\"{x:Type MenuItem}\">", xaml);
        Assert.Contains("{DynamicResource Brush.TextPrimary}", xaml);
        Assert.Contains("{DynamicResource Brush.Surface}", xaml);
        Assert.Contains("{DynamicResource Brush.Selection}", xaml);
    }

    [Fact]
    public void ComboBoxUsesThemeAwareTemplateAndCentersSelectedText()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "Controls.xaml"));

        Assert.Contains("<ControlTemplate TargetType=\"ComboBox\">", xaml);
        Assert.Contains("x:Name=\"SelectedItemPresenter\"", xaml);
        Assert.Contains("VerticalAlignment=\"Center\"", xaml);
        Assert.Contains("TextElement.Foreground=\"{TemplateBinding Foreground}\"", xaml);
        Assert.Contains("Background=\"{DynamicResource Brush.Surface}\"", xaml);
    }

    [Fact]
    public void FixedHeightControlTextIsCenteredAndMultilineTextRemainsTopAligned()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "Controls.xaml"));

        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type Button}\">", "VerticalContentAlignment\" Value=\"Center");
        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type TextBox}\">", "VerticalContentAlignment\" Value=\"Center");
        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type ComboBox}\">", "VerticalContentAlignment\" Value=\"Center");
        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type CheckBox}\">", "VerticalContentAlignment\" Value=\"Center");
        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type RadioButton}\">", "VerticalContentAlignment\" Value=\"Center");
        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type ListBoxItem}\">", "VerticalContentAlignment\" Value=\"Center");
        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type MenuItem}\">", "VerticalContentAlignment\" Value=\"Center");
        Assert.Contains("<Trigger Property=\"AcceptsReturn\" Value=\"True\">", xaml);
        Assert.Contains("<Setter Property=\"VerticalContentAlignment\" Value=\"Top\" />", xaml);
    }

    [Fact]
    public void CheckBoxTemplateCentersItsGlyphAndContentOnTheSameRow()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "Controls.xaml"));

        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type CheckBox}\">", "x:Name=\"CheckGlyphBox\"");
        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type CheckBox}\">", "x:Name=\"CheckContentPresenter\"");
        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type CheckBox}\">", "VerticalAlignment=\"{TemplateBinding VerticalContentAlignment}\"");
        AssertStyleContains(xaml, "<Style TargetType=\"{x:Type CheckBox}\">", "FontSize\" Value=\"{StaticResource FontSize.Body}");
    }

    [Fact]
    public void SettingsTypographyUsesExplicitLineMetricsAndPixelRoundedLayout()
    {
        var mainWindowXaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "MainWindow.xaml"));
        var typographyXaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "Typography.xaml"));
        var settingsXaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "Settings.xaml"));

        Assert.Contains("UseLayoutRounding=\"True\"", mainWindowXaml);
        Assert.Contains("SnapsToDevicePixels=\"True\"", mainWindowXaml);
        Assert.Contains("TextOptions.TextFormattingMode=\"Display\"", mainWindowXaml);
        Assert.Contains("LineStackingStrategy\" Value=\"BlockLineHeight", typographyXaml);
        Assert.Contains("LineStackingStrategy\" Value=\"BlockLineHeight", settingsXaml);
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("SearchPanel.xaml")]
    [InlineData("QuickSwitchBarWindow.xaml")]
    [InlineData("TaskManagerSearchWindow.xaml")]
    public void EveryApplicationWindowUsesPixelRoundedDisplayText(string fileName)
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", fileName));

        Assert.Contains("UseLayoutRounding=\"True\"", xaml);
        Assert.Contains("SnapsToDevicePixels=\"True\"", xaml);
        Assert.Contains("TextOptions.TextFormattingMode=\"Display\"", xaml);
    }

    [Fact]
    public void SettingsDescriptionsWrapWithoutAHeightCap()
    {
        var xaml = File.ReadAllText(GetRepositoryPath("src", "ListaryOpen.App", "Styles", "Settings.xaml"));
        var detailStyleStart = xaml.IndexOf("x:Key=\"SettingsDetailStyle\"", StringComparison.Ordinal);
        var detailStyleEnd = xaml.IndexOf("</Style>", detailStyleStart, StringComparison.Ordinal);
        var detailStyle = xaml[detailStyleStart..detailStyleEnd];

        Assert.Contains("TextWrapping\" Value=\"Wrap", detailStyle);
        Assert.DoesNotContain("MaxHeight", detailStyle);
        Assert.DoesNotContain("TextTrimming", detailStyle);
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

    private static void AssertStyleContains(string xaml, string styleMarker, string expected)
    {
        var styleStart = xaml.IndexOf(styleMarker, StringComparison.Ordinal);
        Assert.True(styleStart >= 0, $"Style marker was not found: {styleMarker}");
        var styleEnd = xaml.IndexOf("</Style>", styleStart, StringComparison.Ordinal);
        Assert.True(styleEnd > styleStart, $"Style end was not found: {styleMarker}");
        Assert.Contains(expected, xaml[styleStart..styleEnd]);
    }
}
