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
