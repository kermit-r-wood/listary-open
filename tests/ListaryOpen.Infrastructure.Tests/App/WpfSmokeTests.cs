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
            WpfApp? application = null;
            MainWindow? mainWindow = null;
            SearchPanel? searchPanel = null;

            try
            {
                application = new WpfApp();
                application.InitializeComponent();

                Assert.NotNull(application.FindResource("Brush.AppBackground"));
                Assert.NotNull(application.FindResource("Brush.Surface"));
                Assert.NotNull(application.FindResource("Brush.TextPrimary"));
                Assert.NotNull(application.FindResource("SearchPanelResultListStyle"));
                Assert.NotNull(application.FindResource("SettingsSectionStyle"));

                mainWindow = new MainWindow(new SettingsViewModel(AppSettings.Defaults()));
                Assert.NotNull(mainWindow.FindName("SettingsContentRoot"));

                searchPanel = new SearchPanel();
                Assert.NotNull(searchPanel.FindName("QueryBox"));
                Assert.NotNull(searchPanel.FindName("ResultsList"));
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                searchPanel?.Hide();
                mainWindow?.Hide();
                application?.Shutdown();
                completed = true;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        var finishedInTime = thread.Join(TimeSpan.FromSeconds(10));

        Assert.True(finishedInTime, "WPF smoke test STA thread did not finish within 10 seconds.");
        Assert.True(completed, "WPF smoke test STA thread did not complete.");
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
