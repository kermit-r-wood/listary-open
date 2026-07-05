using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class DialogBridgeHookTests
{
    [Fact]
    public async Task JumpToFolderUsesHookBeforeFallbackAutomation()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-hook-jump-");
        var hook = new FakeHookBridge(HookJumpResult.Success("Hook changed folder."));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        try
        {
            var result = await bridge.JumpToFolderAsync(folder.FullName, CancellationToken.None);

            Assert.Equal(DialogJumpStatus.Success, result.Status);
            Assert.Equal(1, hook.JumpCount);
            Assert.Equal(0, fallback.ProbeCallCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JumpToFolderFallsBackWhenHookHasNoActiveDialog()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-hook-fallback-");
        var hook = new FakeHookBridge(new HookJumpResult(HookJumpStatus.NoActiveDialog, "No active hook dialog."));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        try
        {
            var result = await bridge.JumpToFolderAsync(folder.FullName, CancellationToken.None);

            Assert.Equal(DialogJumpStatus.Success, result.Status);
            Assert.Equal(1, hook.JumpCount);
            Assert.Equal(1, fallback.ProbeCallCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private sealed class FakeHookBridge : IHookQuickSwitchBridge
    {
        private readonly HookJumpResult _jumpResult;

        public FakeHookBridge(HookJumpResult jumpResult)
        {
            _jumpResult = jumpResult;
        }

        public HookQuickSwitchStatus Status => HookQuickSwitchStatus.Disabled();

        public int JumpCount { get; private set; }

        public event EventHandler<HookQuickSwitchStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task EnableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) => Task.FromResult<HookDialogContext?>(null);

        public Task<HookJumpResult> JumpActiveDialogToFolderAsync(string folderPath, CancellationToken cancellationToken)
        {
            JumpCount++;
            return Task.FromResult(_jumpResult);
        }

        public void Dispose()
        {
        }
    }
}
