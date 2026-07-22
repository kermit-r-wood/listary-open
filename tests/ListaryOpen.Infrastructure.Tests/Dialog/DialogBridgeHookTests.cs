using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class DialogBridgeHookTests
{
    public static TheoryData<HookJumpStatus, DialogJumpStatus> TerminalStatuses => new()
    {
        { HookJumpStatus.Success, DialogJumpStatus.Success },
        { HookJumpStatus.AccessDenied, DialogJumpStatus.PermissionLimited },
        { HookJumpStatus.TargetGone, DialogJumpStatus.TargetGone },
        { HookJumpStatus.UnsupportedDialog, DialogJumpStatus.UnsupportedDialog },
        { HookJumpStatus.NoActiveDialog, DialogJumpStatus.Failed },
        { HookJumpStatus.HostUnavailable, DialogJumpStatus.Failed },
        { HookJumpStatus.Timeout, DialogJumpStatus.Failed },
        { HookJumpStatus.Failed, DialogJumpStatus.Failed }
    };

    [Theory]
    [MemberData(nameof(TerminalStatuses))]
    public async Task CapturedNativeResultIsTerminalWithoutPluginExecution(
        HookJumpStatus hookStatus,
        DialogJumpStatus expectedStatus)
    {
        using var folder = new TemporaryDirectory();
        var hook = new FakeHookBridge(new HookJumpResult(hookStatus, "native result"));
        var plugin = new CountingPlugin();
        var bridge = new DialogBridge(hook, [plugin]);

        var result = await bridge.JumpToFolderAsync(Target(), folder.Path, CancellationToken.None);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal("native result", result.Message);
        Assert.Equal(1, hook.CapturedJumpCount);
        Assert.Equal(0, hook.ActiveJumpCount);
        Assert.Equal(0, plugin.JumpCount);
    }

    [Fact]
    public async Task NativeExceptionIsTerminalWithoutPluginExecution()
    {
        using var folder = new TemporaryDirectory();
        var hook = new FakeHookBridge(new InvalidOperationException("host broke"));
        var plugin = new CountingPlugin();
        var bridge = new DialogBridge(hook, [plugin]);

        var result = await bridge.JumpToFolderAsync(Target(), folder.Path, CancellationToken.None);

        Assert.Equal(DialogJumpStatus.Failed, result.Status);
        Assert.Contains("host broke", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, plugin.JumpCount);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutTryingPlugin()
    {
        using var folder = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var hook = new FakeHookBridge(new OperationCanceledException(cancellation.Token));
        var plugin = new CountingPlugin();
        var bridge = new DialogBridge(hook, [plugin]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => bridge.JumpToFolderAsync(Target(), folder.Path, cancellation.Token));
        Assert.Equal(0, plugin.JumpCount);
    }

    [Fact]
    public async Task CapturePrefersNativeHookAndDoesNotProbePlugins()
    {
        var hook = new FakeHookBridge(HookJumpResult.Success("ok"), Target());
        var plugin = new CountingPlugin();
        var bridge = new DialogBridge(hook, [plugin]);

        var target = await bridge.TryCaptureActiveTargetAsync(CancellationToken.None);

        Assert.IsType<NativeHookDialogTarget>(target);
        Assert.Equal(0, plugin.CaptureCount);
    }

    private static HookDialogContext Target() => new(
        "captured", new IntPtr(0x4567), 12, 34, HookArchitecture.X64,
        "firefox.exe", "#32770", "File Upload", DateTimeOffset.UtcNow);

    private sealed class FakeHookBridge : IHookQuickSwitchBridge
    {
        private readonly Exception? _exception;
        private readonly HookJumpResult? _result;
        private readonly HookDialogContext? _active;

        public FakeHookBridge(HookJumpResult result, HookDialogContext? active = null)
        {
            _result = result;
            _active = active;
        }

        public FakeHookBridge(Exception exception) => _exception = exception;
        public HookQuickSwitchStatus Status => HookQuickSwitchStatus.Disabled();
        public int ActiveJumpCount { get; private set; }
        public int CapturedJumpCount { get; private set; }
        public event EventHandler<HookQuickSwitchStatus>? StatusChanged { add { } remove { } }
        public Task EnableAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) => Task.FromResult(_active);
        public Task<HookJumpResult> JumpDialogToFolderAsync(HookDialogContext dialog, string folderPath, CancellationToken cancellationToken)
        {
            CapturedJumpCount++;
            return _exception is null ? Task.FromResult(_result!) : Task.FromException<HookJumpResult>(_exception);
        }
        public Task<HookJumpResult> JumpActiveDialogToFolderAsync(string folderPath, CancellationToken cancellationToken)
        {
            ActiveJumpCount++;
            return _exception is null ? Task.FromResult(_result!) : Task.FromException<HookJumpResult>(_exception);
        }
        public void Dispose() { }
    }

    private sealed class CountingPlugin : IDialogJumpPlugin
    {
        public string Id => "plugin";
        public string Name => "Plugin";
        public int CaptureCount { get; private set; }
        public int JumpCount { get; private set; }
        public DialogPluginTarget? TryCaptureActiveTarget() { CaptureCount++; return null; }
        public Task<DialogJumpResult> JumpToFolderAsync(DialogPluginTarget target, string folderPath, CancellationToken cancellationToken)
        { JumpCount++; return Task.FromResult(new DialogJumpResult(DialogJumpStatus.Success, "plugin")); }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("listary-hook-").FullName;
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
