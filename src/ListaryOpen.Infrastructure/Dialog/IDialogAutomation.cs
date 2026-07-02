namespace ListaryOpen.Infrastructure.Dialog;

public enum DialogProbeStatus
{
    StandardDialog,
    UnsupportedDialog,
    PermissionLimited
}

public sealed record DialogProbeResult(DialogProbeStatus Status, string Message)
{
    public static DialogProbeResult StandardDialog() => new(DialogProbeStatus.StandardDialog, "Standard dialog active.");

    public static DialogProbeResult Unsupported(string message) => new(DialogProbeStatus.UnsupportedDialog, message);

    public static DialogProbeResult PermissionLimited(string message) => new(DialogProbeStatus.PermissionLimited, message);
}

public enum DialogJumpStatus
{
    Success,
    UnsupportedDialog,
    PermissionLimited,
    Failed
}

public sealed record DialogJumpResult(DialogJumpStatus Status, string Message);

public interface IDialogAutomation
{
    DialogProbeResult ProbeActiveDialog();

    Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken);
}
