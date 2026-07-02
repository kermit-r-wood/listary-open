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
        Assert.Equal("No standard dialog", result.Message);
        Assert.Equal(0, automation.SetFolderCallCount);
    }

    [Fact]
    public async Task JumpToFolderReportsPermissionLimitedForElevatedTarget()
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.PermissionLimited("Elevated target"));
        var bridge = new DialogBridge(automation);

        var result = await bridge.JumpToFolderAsync("C:\\Docs", CancellationToken.None);

        Assert.Equal(DialogJumpStatus.PermissionLimited, result.Status);
        Assert.Equal("Elevated target", result.Message);
        Assert.Equal(0, automation.SetFolderCallCount);
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

    [Fact]
    public async Task JumpToFolderReportsFailedAndSkipsAutomationForUnknownProbeStatus()
    {
        var automation = new FakeDialogAutomation(new DialogProbeResult((DialogProbeStatus)999, "Unknown dialog probe"));
        var bridge = new DialogBridge(automation);

        var result = await bridge.JumpToFolderAsync("C:\\Docs", CancellationToken.None);

        Assert.Equal(DialogJumpStatus.Failed, result.Status);
        Assert.Equal("Unknown dialog probe status: 999.", result.Message);
        Assert.Equal(0, automation.SetFolderCallCount);
    }

    [Fact]
    public async Task JumpToFolderReportsFailedWhenSetFolderCannotChangeDialog()
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.StandardDialog(), setFolderResult: false);
        var bridge = new DialogBridge(automation);

        var result = await bridge.JumpToFolderAsync("C:\\Docs", CancellationToken.None);

        Assert.Equal(DialogJumpStatus.Failed, result.Status);
        Assert.Equal("Dialog folder could not be changed.", result.Message);
        Assert.Equal(1, automation.SetFolderCallCount);
    }

    [Fact]
    public void ConstructorRejectsNullAutomation()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new DialogBridge(null!));

        Assert.Equal("automation", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task JumpToFolderRejectsBlankFolderPathBeforeAutomation(string folderPath)
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(automation);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => bridge.JumpToFolderAsync(folderPath, CancellationToken.None));

        Assert.Equal("folderPath", exception.ParamName);
        Assert.Equal(0, automation.ProbeCallCount);
        Assert.Equal(0, automation.SetFolderCallCount);
    }

    [Fact]
    public async Task JumpToFolderRejectsNullFolderPathBeforeAutomation()
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(automation);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => bridge.JumpToFolderAsync(null!, CancellationToken.None));

        Assert.Equal("folderPath", exception.ParamName);
        Assert.Equal(0, automation.ProbeCallCount);
        Assert.Equal(0, automation.SetFolderCallCount);
    }
}

internal sealed class FakeDialogAutomation : IDialogAutomation
{
    private readonly DialogProbeResult _probe;
    private readonly bool _setFolderResult;

    public FakeDialogAutomation(DialogProbeResult probe, bool setFolderResult = true)
    {
        _probe = probe;
        _setFolderResult = setFolderResult;
    }

    public string? LastFolder { get; private set; }

    public int ProbeCallCount { get; private set; }

    public int SetFolderCallCount { get; private set; }

    public DialogProbeResult ProbeActiveDialog()
    {
        ProbeCallCount++;
        return _probe;
    }

    public Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        SetFolderCallCount++;
        LastFolder = folderPath;
        return Task.FromResult(_setFolderResult);
    }
}
