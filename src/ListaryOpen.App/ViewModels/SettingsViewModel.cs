using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Indexing;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ListaryOpen.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private string _indexingStatusText = "Indexing: idle";

    public SettingsViewModel()
        : this(AppSettings.Defaults())
    {
    }

    public SettingsViewModel(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; }

    public string IndexingStatusText
    {
        get => _indexingStatusText;
        private set
        {
            if (string.Equals(_indexingStatusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _indexingStatusText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateIndexingStatus(IndexingStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        IndexingStatusText = $"Indexing: {status.Message} ({status.IndexedCount})";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
