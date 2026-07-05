using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookQuickSwitchBridgeTests
{
    [Fact]
    public async Task JumpUsesActiveDialogArchitectureClient()
    {
        var dialog = new HookDialogContext(
            "dlg-1",
            new IntPtr(100),
            200,
            300,
            HookArchitecture.X86,
            "MobaXterm",
            "#32770",
            "Choose which file(s) to upload...",
            DateTimeOffset.UtcNow);
        var client = new RecordingHookClient(dialog, HookJumpResult.Success("Jumped."));
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog.")),
                [HookArchitecture.X86] = client
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.Success, result.Status);
        Assert.Equal("dlg-1", client.LastDialogId);
        Assert.Equal("C:\\Users\\paulx", client.LastFolderPath);
    }

    [Fact]
    public async Task JumpReturnsNoActiveDialogWhenNoClientHasDialog()
    {
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog.")),
                [HookArchitecture.X86] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."))
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.NoActiveDialog, result.Status);
    }

    private sealed class RecordingHookClient : IHookIpcClient
    {
        private readonly HookDialogContext? _activeDialog;
        private readonly HookJumpResult _jumpResult;

        public RecordingHookClient(HookDialogContext? activeDialog, HookJumpResult jumpResult)
        {
            _activeDialog = activeDialog;
            _jumpResult = jumpResult;
        }

        public string? LastDialogId { get; private set; }

        public string? LastFolderPath { get; private set; }

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_activeDialog);

        public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken)
        {
            LastDialogId = dialogId;
            LastFolderPath = folderPath;
            return Task.FromResult(_jumpResult);
        }
    }
}
