namespace ListaryOpen.Infrastructure.Dialog;

public enum DialogJumpStatus
{
    Success,
    UnsupportedDialog,
    PermissionLimited,
    TargetGone,
    Failed
}

public sealed record DialogJumpResult(DialogJumpStatus Status, string Message);
