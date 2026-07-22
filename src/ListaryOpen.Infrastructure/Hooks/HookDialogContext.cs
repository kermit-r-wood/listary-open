namespace ListaryOpen.Infrastructure.Hooks;

public sealed record HookDialogContext(
    string DialogId,
    IntPtr WindowHandle,
    uint ProcessId,
    uint ThreadId,
    HookArchitecture Architecture,
    string ProcessName,
    string ClassName,
    string Title,
    DateTimeOffset ObservedAt,
    bool FirefoxFileDialogUtility = false,
    bool PreloadConfirmedBeforeDialog = false);
