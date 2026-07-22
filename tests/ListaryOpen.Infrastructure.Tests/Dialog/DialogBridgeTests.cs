using ListaryOpen.Infrastructure.Dialog;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class DialogBridgeTests
{
    [Fact]
    public async Task NoCapturedExecutorFailsWithoutTryingAnotherPath()
    {
        var bridge = new DialogBridge(hookBridge: null);

        var result = await bridge.JumpToFolderAsync(Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(DialogJumpStatus.Failed, result.Status);
        Assert.Contains("native-hook or dialog-plugin", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapturedPluginTargetUsesOnlyItsOwningPlugin()
    {
        using var folder = new TemporaryDirectory();
        var owner = new FakeDialogPlugin("owner", "GHOST_WindowClass", canCapture: true);
        var other = new FakeDialogPlugin("other", "OtherWindow", canCapture: false);
        var bridge = CreateBridge([owner, other]);
        var target = Assert.IsType<DialogPluginTarget>(
            await bridge.TryCaptureActiveTargetAsync(CancellationToken.None));

        var result = await bridge.JumpToFolderAsync(target, folder.Path, CancellationToken.None);

        Assert.Equal(DialogJumpStatus.Success, result.Status);
        Assert.Equal(1, owner.JumpCount);
        Assert.Equal(0, other.JumpCount);
        Assert.Equal(target.Window.WindowHandle, owner.LastTarget?.Window.WindowHandle);
    }

    [Fact]
    public async Task StandardDialogCannotBeClaimedByPlugin()
    {
        var plugin = new FakeDialogPlugin("unsafe", "#32770", canCapture: true);
        var bridge = CreateBridge([plugin]);

        var target = await bridge.TryCaptureActiveTargetAsync(CancellationToken.None);

        Assert.Null(target);
        Assert.Equal(0, plugin.JumpCount);
    }

    [Fact]
    public async Task MultiplePluginMatchesAreRejectedInsteadOfChoosingAnExecutor()
    {
        var first = new FakeDialogPlugin("first", "Custom", canCapture: true);
        var second = new FakeDialogPlugin("second", "Custom", canCapture: true);
        var bridge = CreateBridge([first, second]);

        Assert.Null(await bridge.TryCaptureActiveTargetAsync(CancellationToken.None));
    }

    [Fact]
    public void DuplicatePluginIdsAreRejected()
    {
        var plugins = new[]
        {
            new FakeDialogPlugin("duplicate", "One", false),
            new FakeDialogPlugin("DUPLICATE", "Two", false)
        };

        Assert.Throws<ArgumentException>(() => new DialogBridge(null, plugins));
    }

    [Fact]
    public async Task PluginExceptionIsTerminalAndDoesNotTryAnotherPlugin()
    {
        using var folder = new TemporaryDirectory();
        var owner = new FakeDialogPlugin("owner", "Custom", true, new InvalidOperationException("broken"));
        var other = new FakeDialogPlugin("other", "Other", false);
        var bridge = CreateBridge([owner, other]);
        var target = Assert.IsType<DialogPluginTarget>(await bridge.TryCaptureActiveTargetAsync(CancellationToken.None));

        var result = await bridge.JumpToFolderAsync(target, folder.Path, CancellationToken.None);

        Assert.Equal(DialogJumpStatus.Failed, result.Status);
        Assert.Contains("broken", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, owner.JumpCount);
        Assert.Equal(0, other.JumpCount);
    }

    [Fact]
    public async Task MissingFolderIsTerminalBeforeExecutorRuns()
    {
        var plugin = new FakeDialogPlugin("owner", "Custom", true);
        var bridge = CreateBridge([plugin]);
        var target = Assert.IsType<DialogPluginTarget>(await bridge.TryCaptureActiveTargetAsync(CancellationToken.None));
        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var result = await bridge.JumpToFolderAsync(target, missing, CancellationToken.None);

        Assert.Equal(DialogJumpStatus.TargetGone, result.Status);
        Assert.Equal(0, plugin.JumpCount);
    }

    [Fact]
    public async Task PluginCaptureRejectsTargetThatIsNotTheExactForegroundWindow()
    {
        var plugin = new FakeDialogPlugin("owner", "Custom", true);
        var reader = new FakeDialogWindowIdentityReader(new IntPtr(0x9999), Identity("Custom"));
        var bridge = new DialogBridge(null, [plugin], reader);

        Assert.Null(await bridge.TryCaptureActiveTargetAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(43u, "fixture.exe", "Custom")]
    [InlineData(42u, "other.exe", "Custom")]
    [InlineData(42u, "fixture.exe", "OtherClass")]
    public async Task PluginCaptureRejectsPidProcessOrClassIdentityMismatch(
        uint processId,
        string processName,
        string className)
    {
        var plugin = new FakeDialogPlugin("owner", "Custom", true);
        var reader = new FakeDialogWindowIdentityReader(
            new IntPtr(0x1234),
            new DialogWindowIdentity(new IntPtr(0x1234), processId, processName, className));
        var bridge = new DialogBridge(null, [plugin], reader);

        Assert.Null(await bridge.TryCaptureActiveTargetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PluginJumpRevalidatesCapturedTargetIdentityBeforeExecution()
    {
        using var folder = new TemporaryDirectory();
        var plugin = new FakeDialogPlugin("owner", "Custom", true);
        var reader = new FakeDialogWindowIdentityReader(new IntPtr(0x1234), Identity("Custom"));
        var bridge = new DialogBridge(null, [plugin], reader);
        var target = Assert.IsType<DialogPluginTarget>(
            await bridge.TryCaptureActiveTargetAsync(CancellationToken.None));
        reader.ForegroundWindow = new IntPtr(0x9999);

        var result = await bridge.JumpToFolderAsync(target, folder.Path, CancellationToken.None);

        Assert.Equal(DialogJumpStatus.TargetGone, result.Status);
        Assert.Equal(0, plugin.JumpCount);
    }

    private static DialogBridge CreateBridge(IReadOnlyList<FakeDialogPlugin> plugins)
    {
        var captured = plugins.FirstOrDefault(plugin => plugin.CanCapture);
        return new DialogBridge(
            null,
            plugins,
            new FakeDialogWindowIdentityReader(
                new IntPtr(0x1234),
                Identity(captured?.ClassName ?? "Custom")));
    }

    private static DialogWindowIdentity Identity(string className) => new(
        new IntPtr(0x1234),
        42,
        "fixture.exe",
        className);

    private sealed class FakeDialogPlugin : IDialogJumpPlugin
    {
        private readonly string _className;
        private readonly bool _canCapture;
        private readonly Exception? _exception;

        public FakeDialogPlugin(string id, string className, bool canCapture, Exception? exception = null)
        {
            Id = id;
            Name = id;
            _className = className;
            _canCapture = canCapture;
            _exception = exception;
        }

        public string Id { get; }
        public string Name { get; }
        public string ClassName => _className;
        public bool CanCapture => _canCapture;
        public int JumpCount { get; private set; }
        public DialogPluginTarget? LastTarget { get; private set; }

        public DialogPluginTarget? TryCaptureActiveTarget() => _canCapture
            ? new DialogPluginTarget(Id, new DialogWindowSnapshot(
                new IntPtr(0x1234), 42, "fixture.exe", _className, "Fixture", DateTimeOffset.UtcNow))
            : null;

        public Task<DialogJumpResult> JumpToFolderAsync(
            DialogPluginTarget target,
            string folderPath,
            CancellationToken cancellationToken)
        {
            JumpCount++;
            LastTarget = target;
            return _exception is null
                ? Task.FromResult(new DialogJumpResult(DialogJumpStatus.Success, "Plugin verified direct navigation."))
                : Task.FromException<DialogJumpResult>(_exception);
        }
    }

    private sealed class FakeDialogWindowIdentityReader(
        IntPtr foregroundWindow,
        DialogWindowIdentity identity) : IDialogWindowIdentityReader
    {
        public IntPtr ForegroundWindow { get; set; } = foregroundWindow;
        public DialogWindowIdentity Identity { get; set; } = identity;

        public IntPtr GetForegroundWindow() => ForegroundWindow;

        public bool TryRead(IntPtr windowHandle, out DialogWindowIdentity value)
        {
            value = Identity;
            return windowHandle == Identity.WindowHandle;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("listary-dialog-").FullName;
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
