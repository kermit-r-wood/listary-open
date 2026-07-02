using ListaryOpen.Infrastructure.Dialog;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class DialogBridgeTests
{
    [Fact]
    public async Task JumpToFolderReportsUnsupportedWhenNoStandardDialogIsActive()
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.Unsupported("No standard dialog"));
        var bridge = new DialogBridge(automation);

        var result = await bridge.JumpToFolderAsync("C:\\Docs", CancellationToken.None);

        Assert.Equal(DialogJumpStatus.UnsupportedDialog, result.Status);
    }

    [Fact]
    public async Task JumpToFolderReportsPermissionLimitedForElevatedTarget()
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.PermissionLimited("Elevated target"));
        var bridge = new DialogBridge(automation);

        var result = await bridge.JumpToFolderAsync("C:\\Docs", CancellationToken.None);

        Assert.Equal(DialogJumpStatus.PermissionLimited, result.Status);
    }

    [Fact]
    public async Task JumpToFolderChangesFolderForStandardDialog()
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(automation);

        var result = await bridge.JumpToFolderAsync("C:\\Docs", CancellationToken.None);

        Assert.Equal(DialogJumpStatus.Success, result.Status);
        Assert.Equal("C:\\Docs", automation.LastFolder);
    }
}

internal sealed class FakeDialogAutomation : IDialogAutomation
{
    private readonly DialogProbeResult _probe;

    public FakeDialogAutomation(DialogProbeResult probe)
    {
        _probe = probe;
    }

    public string? LastFolder { get; private set; }

    public DialogProbeResult ProbeActiveDialog() => _probe;

    public Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        LastFolder = folderPath;
        return Task.FromResult(true);
    }
}
