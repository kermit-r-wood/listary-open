using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SettingsHookQuickSwitchTests
{
    [Fact]
    public void ConstructorStartsWithHookDisabledStatus()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        Assert.Equal("Hook quick switch: disabled", viewModel.HookQuickSwitchStatusText);
        Assert.True(viewModel.EnableHookQuickSwitchCommand.CanExecute(null));
    }

    [Fact]
    public void EnableHookCommandInvokesCallbackAndUpdatesStatus()
    {
        var enableCount = 0;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: () => enableCount++);

        viewModel.UpdateHookQuickSwitchStatus(new HookQuickSwitchStatus(
            true,
            new HookArchitectureStatus(HookArchitecture.X64, true, true, true, "x64 running"),
            new HookArchitectureStatus(HookArchitecture.X86, true, false, true, "x86 stopped")));
        viewModel.EnableHookQuickSwitchCommand.Execute(null);

        Assert.Equal(1, enableCount);
        Assert.Contains("partial", viewModel.HookQuickSwitchStatusText);
    }
}
