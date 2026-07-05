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

    [Fact]
    public async Task JumpToFolderFallsBackWhenHookThrowsNonCancellationException()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-hook-exception-fallback-");
        var hook = new FakeHookBridge(new InvalidOperationException("Hook host failed."));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        try
        {
            var result = await bridge.JumpToFolderAsync(folder.FullName, CancellationToken.None);

            Assert.Equal(DialogJumpStatus.Success, result.Status);
            Assert.Contains("fallback", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hook", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Hook host failed.", result.Message, StringComparison.Ordinal);
            Assert.Equal(1, hook.JumpCount);
            Assert.Equal(1, fallback.ProbeCallCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JumpToFolderPropagatesCancellationFromHookWhenTokenIsCanceled()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-hook-cancellation-");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var hook = new FakeHookBridge(new OperationCanceledException(cancellation.Token));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => bridge.JumpToFolderAsync(folder.FullName, cancellation.Token));

            Assert.Equal(1, hook.JumpCount);
            Assert.Equal(0, fallback.ProbeCallCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JumpToFolderMapsHookAccessDeniedWithoutFallback()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-hook-access-denied-");
        var hook = new FakeHookBridge(new HookJumpResult(HookJumpStatus.AccessDenied, "Hook access denied."));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        try
        {
            var result = await bridge.JumpToFolderAsync(folder.FullName, CancellationToken.None);

            Assert.Equal(DialogJumpStatus.PermissionLimited, result.Status);
            Assert.Equal("Hook access denied.", result.Message);
            Assert.Equal(1, hook.JumpCount);
            Assert.Equal(0, fallback.ProbeCallCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JumpToFolderMapsHookTargetGoneWithoutFallback()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-hook-target-gone-");
        var hook = new FakeHookBridge(new HookJumpResult(HookJumpStatus.TargetGone, "Hook target is gone."));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        try
        {
            var result = await bridge.JumpToFolderAsync(folder.FullName, CancellationToken.None);

            Assert.Equal(DialogJumpStatus.TargetGone, result.Status);
            Assert.Equal("Hook target is gone.", result.Message);
            Assert.Equal(1, hook.JumpCount);
            Assert.Equal(0, fallback.ProbeCallCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(HookJumpStatus.Failed)]
    [InlineData(HookJumpStatus.UnsupportedDialog)]
    public async Task JumpToFolderFallsBackWhenHookCannotJump(HookJumpStatus hookStatus)
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-hook-status-fallback-");
        var hook = new FakeHookBridge(new HookJumpResult(hookStatus, "Hook could not jump."));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        try
        {
            var result = await bridge.JumpToFolderAsync(folder.FullName, CancellationToken.None);

            Assert.Equal(DialogJumpStatus.Success, result.Status);
            Assert.Contains("fallback", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hook", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(hookStatus.ToString(), result.Message, StringComparison.Ordinal);
            Assert.Contains("Hook could not jump.", result.Message, StringComparison.Ordinal);
            Assert.Equal(1, hook.JumpCount);
            Assert.Equal(1, fallback.ProbeCallCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JumpToFolderSkipsHookWhenFolderIsMissing()
    {
        var missingFolder = Path.Combine(Path.GetTempPath(), "listary-open-missing-hook-" + Guid.NewGuid());
        var hook = new FakeHookBridge(HookJumpResult.Success("Hook changed folder."));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        var result = await bridge.JumpToFolderAsync(missingFolder, CancellationToken.None);

        Assert.Equal(DialogJumpStatus.TargetGone, result.Status);
        Assert.Contains("no longer exists", result.Message);
        Assert.Equal(0, hook.JumpCount);
        Assert.Equal(1, fallback.ProbeCallCount);
    }

    private sealed class FakeHookBridge : IHookQuickSwitchBridge
    {
        private readonly Exception? _exception;
        private readonly HookJumpResult? _jumpResult;

        public FakeHookBridge(HookJumpResult jumpResult)
        {
            _jumpResult = jumpResult;
        }

        public FakeHookBridge(Exception exception)
        {
            _exception = exception;
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
            return _exception is null
                ? Task.FromResult(_jumpResult!)
                : Task.FromException<HookJumpResult>(_exception);
        }

        public void Dispose()
        {
        }
    }
}
