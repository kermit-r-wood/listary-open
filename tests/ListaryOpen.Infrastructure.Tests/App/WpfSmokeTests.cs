using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Settings;
using WpfApp = ListaryOpen.App.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class WpfSmokeTests
{
    [Fact]
    public void UiResourcesAndWindowsLoadOnStaThread()
    {
        Exception? exception = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new WpfApp();

                Assert.NotNull(application.FindResource("Brush.AppBackground"));
                Assert.NotNull(application.FindResource("Brush.Surface"));
                Assert.NotNull(application.FindResource("Brush.TextPrimary"));
                Assert.NotNull(application.FindResource("SearchPanelResultListStyle"));
                Assert.NotNull(application.FindResource("SettingsSectionStyle"));

                var mainWindow = new MainWindow(new SettingsViewModel(AppSettings.Defaults()));
                Assert.NotNull(mainWindow.FindName("SettingsContentRoot"));

                var searchPanel = new SearchPanel();
                Assert.NotNull(searchPanel.FindName("QueryBox"));
                Assert.NotNull(searchPanel.FindName("ResultsList"));

                mainWindow.Hide();
                searchPanel.Hide();
                application.Shutdown();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                completed = true;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));

        Assert.True(completed);
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
