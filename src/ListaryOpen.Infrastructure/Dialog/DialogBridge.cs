namespace ListaryOpen.Infrastructure.Dialog;

public sealed class DialogBridge
{
    private readonly IDialogAutomation _automation;

    public DialogBridge(IDialogAutomation automation)
    {
        _automation = automation;
    }

    public async Task<DialogJumpResult> JumpToFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        var probe = _automation.ProbeActiveDialog();
        if (probe.Status == DialogProbeStatus.UnsupportedDialog)
        {
            return new DialogJumpResult(DialogJumpStatus.UnsupportedDialog, probe.Message);
        }

        if (probe.Status == DialogProbeStatus.PermissionLimited)
        {
            return new DialogJumpResult(DialogJumpStatus.PermissionLimited, probe.Message);
        }

        var success = await _automation.SetFolderAsync(folderPath, cancellationToken);
        return success
            ? new DialogJumpResult(DialogJumpStatus.Success, "Dialog folder changed.")
            : new DialogJumpResult(DialogJumpStatus.Failed, "Dialog folder could not be changed.");
    }
}
