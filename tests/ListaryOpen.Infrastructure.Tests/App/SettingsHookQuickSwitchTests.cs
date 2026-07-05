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

        viewModel.UpdateHookQuickSwitchStatus(CreatePartialStatus());
        viewModel.EnableHookQuickSwitchCommand.Execute(null);

        Assert.Equal(1, enableCount);
        Assert.Contains("partial", viewModel.HookQuickSwitchStatusText);
    }

    [Fact]
    public void EnableHookCommandDisablesUntilStatusUpdate()
    {
        var enableCount = 0;
        var canExecuteChangedCount = 0;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: () => enableCount++);
        viewModel.EnableHookQuickSwitchCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        viewModel.EnableHookQuickSwitchCommand.Execute(null);
        viewModel.EnableHookQuickSwitchCommand.Execute(null);

        Assert.Equal(1, enableCount);
        Assert.False(viewModel.EnableHookQuickSwitchCommand.CanExecute(null));
        Assert.Equal(1, canExecuteChangedCount);
    }

    [Fact]
    public void UpdateHookQuickSwitchStatusReenablesEnableHookCommand()
    {
        var canExecuteChangedCount = 0;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: () => { });
        viewModel.EnableHookQuickSwitchCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        viewModel.EnableHookQuickSwitchCommand.Execute(null);
        viewModel.UpdateHookQuickSwitchStatus(HookQuickSwitchStatus.Disabled());

        Assert.True(viewModel.EnableHookQuickSwitchCommand.CanExecute(null));
        Assert.Equal(2, canExecuteChangedCount);
    }

    [Fact]
    public void EnableHookCommandReenablesWhenCallbackThrows()
    {
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: () => throw new InvalidOperationException("enable failed"));

        Assert.Throws<InvalidOperationException>(() => viewModel.EnableHookQuickSwitchCommand.Execute(null));

        Assert.True(viewModel.EnableHookQuickSwitchCommand.CanExecute(null));
    }

    [Fact]
    public void UpdateHookQuickSwitchStatusRaisesPropertyChanged()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.UpdateHookQuickSwitchStatus(CreatePartialStatus());

        Assert.Contains(nameof(SettingsViewModel.HookQuickSwitchStatusText), changedProperties);
    }

    private static HookQuickSwitchStatus CreatePartialStatus()
    {
        return new HookQuickSwitchStatus(
            true,
            new HookArchitectureStatus(HookArchitecture.X64, true, true, true, "x64 running"),
            new HookArchitectureStatus(HookArchitecture.X86, true, false, true, "x86 stopped"));
    }
}
