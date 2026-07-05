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
    TargetGone,
    Failed
}

public sealed record DialogJumpResult
{
    public DialogJumpResult(DialogJumpStatus status, string message, bool isDegradedSuccess = false)
    {
        Status = status;
        Message = message;
        IsDegradedSuccess = isDegradedSuccess;
    }

    public DialogJumpStatus Status { get; init; }

    public string Message { get; init; }

    public bool IsDegradedSuccess { get; init; }

    public void Deconstruct(out DialogJumpStatus status, out string message)
    {
        status = Status;
        message = Message;
    }
}

public interface IDialogAutomation
{
    DialogProbeResult ProbeActiveDialog();

    Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken);
}
