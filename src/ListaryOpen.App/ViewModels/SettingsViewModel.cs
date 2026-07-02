using ListaryOpen.Core.Settings;

namespace ListaryOpen.App.ViewModels;

public sealed class SettingsViewModel
{
    public AppSettings Settings { get; } = AppSettings.Defaults();
}
