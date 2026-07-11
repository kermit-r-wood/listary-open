namespace ListaryOpen.Infrastructure.Hooks;

public interface IHookQuickSwitchBridge : IDisposable
{
    HookQuickSwitchStatus Status { get; }

    event EventHandler<HookQuickSwitchStatus>? StatusChanged;

    Task EnableAsync(CancellationToken cancellationToken);

    Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken);

    Task<HookJumpResult> JumpDialogToFolderAsync(
        HookDialogContext dialog,
        string folderPath,
        CancellationToken cancellationToken);

    Task<HookJumpResult> JumpActiveDialogToFolderAsync(string folderPath, CancellationToken cancellationToken);
}
