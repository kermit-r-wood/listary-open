namespace ListaryOpen.Infrastructure.Hooks;

public sealed record HookActiveDialogResult(
    HookJumpStatus Status,
    string Message,
    HookDialogContext? Dialog)
{
    public static HookActiveDialogResult Active(HookDialogContext dialog) =>
        new(HookJumpStatus.Success, "Active hook-controlled dialog found.", dialog);

    public static HookActiveDialogResult NoActiveDialog(string message) =>
        new(HookJumpStatus.NoActiveDialog, message, null);

    public static HookActiveDialogResult FromJumpResult(HookJumpResult result) =>
        new(result.Status, result.Message, null);
}
