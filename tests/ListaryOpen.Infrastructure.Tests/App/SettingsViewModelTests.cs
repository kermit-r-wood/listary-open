using System.ComponentModel;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void ConstructorStartsWithIdleIndexingStatus()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        Assert.Equal("Indexing: idle", viewModel.IndexingStatusText);
    }

    [Fact]
    public void UpdateIndexingStatusFormatsStatusAndRaisesPropertyChanged()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        viewModel.UpdateIndexingStatus(new IndexingStatus(IndexingRunState.Indexing, "Indexing started.", 7));

        Assert.Equal("Indexing: Indexing started. (7)", viewModel.IndexingStatusText);
        Assert.Contains(nameof(SettingsViewModel.IndexingStatusText), changedProperties);
        Assert.IsAssignableFrom<INotifyPropertyChanged>(viewModel);
    }
}
