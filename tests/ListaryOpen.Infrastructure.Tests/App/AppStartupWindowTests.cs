using WpfApp = ListaryOpen.App.App;
using ListaryOpen.Infrastructure.AppData;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppStartupWindowTests
{
    [Fact]
    public void ShouldShowSettingsOnStartupReturnsFalseForNormalStartup()
    {
        Assert.False(WpfApp.ShouldShowSettingsOnStartup(hasBlockingStartupMessage: false));
    }

    [Fact]
    public void ShouldShowSettingsOnStartupReturnsTrueForBlockingStartupMessage()
    {
        Assert.True(WpfApp.ShouldShowSettingsOnStartup(hasBlockingStartupMessage: true));
    }

    [Fact]
    public void CreateAppDataStartupFailureMessageIncludesProgramDataPaths()
    {
        var paths = AppDataPaths.CreateUnderProgramDirectory("C:\\Tools\\ListaryOpen");

        var message = WpfApp.CreateAppDataStartupFailureMessage(paths, new IOException("Access denied."));

        Assert.Contains("C:\\Tools\\ListaryOpen\\data", message);
        Assert.Contains("C:\\Tools\\ListaryOpen\\data\\index.db", message);
        Assert.Contains("Access denied.", message);
    }

    [Fact]
    public void StartHookQuickSwitchEnablementOnStartupInvokesEnablementCallback()
    {
        var bridge = new FakeHookQuickSwitchBridge();
        IHookQuickSwitchBridge? observedBridge = null;
        var enablementCallCount = 0;

        WpfApp.StartHookQuickSwitchEnablementOnStartup(
            bridge,
            candidate =>
            {
                observedBridge = candidate;
                enablementCallCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, enablementCallCount);
        Assert.Same(bridge, observedBridge);
    }

    [Fact]
    public void StartHookQuickSwitchEnablementOnStartupSkipsMissingBridge()
    {
        var enablementCallCount = 0;

        WpfApp.StartHookQuickSwitchEnablementOnStartup(
            null,
            _ =>
            {
                enablementCallCount++;
                return Task.CompletedTask;
            });

        Assert.Equal(0, enablementCallCount);
    }

    private sealed class FakeHookQuickSwitchBridge : IHookQuickSwitchBridge
    {
        public HookQuickSwitchStatus Status => HookQuickSwitchStatus.Disabled();

        public event EventHandler<HookQuickSwitchStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task EnableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<HookDialogContext?>(null);

        public Task<HookJumpResult> JumpDialogToFolderAsync(
            HookDialogContext dialog,
            string folderPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."));

        public Task<HookJumpResult> JumpActiveDialogToFolderAsync(string folderPath, CancellationToken cancellationToken) =>
            Task.FromResult(new HookJumpResult(HookJumpStatus.NoActiveDialog, "No active dialog."));

        public void Dispose()
        {
        }
    }
}
