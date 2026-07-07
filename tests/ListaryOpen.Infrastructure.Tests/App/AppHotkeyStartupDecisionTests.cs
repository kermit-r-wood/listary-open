using System.Windows;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppHotkeyStartupDecisionTests
{
    [Theory]
    [InlineData(false, "Activate")]
    [InlineData(true, "Hide")]
    public void SearchHotkeyTogglesSearchPanelVisibility(bool panelIsVisible, string expected)
    {
        Assert.Equal(expected, ListaryOpen.App.App.GetSearchHotkeyPanelAction(panelIsVisible).ToString());
    }

    [Fact]
    public void CreateHotkeyStartupDecisionContinuesSilentlyWhenAllHotkeysRegister()
    {
        var result = new HotkeyRegistrationResult(new[]
        {
            new HotkeyRegistration("Ctrl+Space", true),
            new HotkeyRegistration("Ctrl+G", true)
        });

        var decision = ListaryOpen.App.App.CreateHotkeyStartupDecision(result);

        Assert.True(decision.ShouldContinue);
        Assert.Null(decision.Message);
    }

    [Fact]
    public void CreateHotkeyStartupDecisionWarnsAndContinuesWhenOneHotkeyFails()
    {
        var result = new HotkeyRegistrationResult(new[]
        {
            new HotkeyRegistration("Ctrl+Space", false),
            new HotkeyRegistration("Ctrl+G", true)
        });

        var decision = ListaryOpen.App.App.CreateHotkeyStartupDecision(result);

        Assert.True(decision.ShouldContinue);
        Assert.Equal(MessageBoxImage.Warning, decision.Image);
        Assert.Contains("Ctrl+Space", decision.Message);
        Assert.Contains("Ctrl+G", decision.Message);
    }

    [Fact]
    public void CreateHotkeyStartupDecisionStopsStartupWhenNoHotkeysRegister()
    {
        var result = new HotkeyRegistrationResult(new[]
        {
            new HotkeyRegistration("Ctrl+Space", false),
            new HotkeyRegistration("Ctrl+G", false)
        });

        var decision = ListaryOpen.App.App.CreateHotkeyStartupDecision(result);

        Assert.False(decision.ShouldContinue);
        Assert.Equal(MessageBoxImage.Error, decision.Image);
        Assert.Contains("Ctrl+Space", decision.Message);
        Assert.Contains("Ctrl+G", decision.Message);
        Assert.Contains("will exit", decision.Message, StringComparison.OrdinalIgnoreCase);
    }
}
