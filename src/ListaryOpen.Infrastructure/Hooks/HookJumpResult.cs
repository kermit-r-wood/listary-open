namespace ListaryOpen.Infrastructure.Hooks;

public enum HookJumpStatus
{
    Success,
    NoActiveDialog,
    UnsupportedDialog,
    HostUnavailable,
    AccessDenied,
    Timeout,
    TargetGone,
    Failed
}

public sealed record HookJumpResult(HookJumpStatus Status, string Message)
{
    public bool Succeeded => Status == HookJumpStatus.Success;

    public static HookJumpResult Success(string message) => new(HookJumpStatus.Success, message);
}
