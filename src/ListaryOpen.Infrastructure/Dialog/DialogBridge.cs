namespace ListaryOpen.Infrastructure.Dialog;

public sealed class DialogBridge
{
    private readonly IDialogAutomation _automation;

    public DialogBridge(IDialogAutomation automation)
    {
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
    }

    public async Task<DialogJumpResult> JumpToFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path cannot be empty or whitespace.", nameof(folderPath));
        }

        var probe = _automation.ProbeActiveDialog();
        switch (probe.Status)
        {
            case DialogProbeStatus.UnsupportedDialog:
                return new DialogJumpResult(DialogJumpStatus.UnsupportedDialog, probe.Message);

            case DialogProbeStatus.PermissionLimited:
                return new DialogJumpResult(DialogJumpStatus.PermissionLimited, probe.Message);

            case DialogProbeStatus.StandardDialog:
                var success = await _automation.SetFolderAsync(folderPath, cancellationToken);
                return success
                    ? new DialogJumpResult(DialogJumpStatus.Success, "Dialog folder changed.")
                    : new DialogJumpResult(DialogJumpStatus.Failed, "Dialog folder could not be changed.");

            default:
                return new DialogJumpResult(DialogJumpStatus.Failed, $"Unknown dialog probe status: {(int)probe.Status}.");
        }
    }
}
