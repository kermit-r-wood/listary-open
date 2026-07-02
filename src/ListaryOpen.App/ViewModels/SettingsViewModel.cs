using ListaryOpen.Core.Settings;

namespace ListaryOpen.App.ViewModels;

public sealed class SettingsViewModel
{
    public SettingsViewModel()
        : this(AppSettings.Defaults())
    {
    }

    public SettingsViewModel(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; }
}
