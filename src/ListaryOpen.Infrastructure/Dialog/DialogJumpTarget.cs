using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Dialog;

public abstract record DialogJumpTarget(string Id, IntPtr WindowHandle);

public sealed record NativeHookDialogTarget(HookDialogContext Dialog)
    : DialogJumpTarget(Dialog.DialogId, Dialog.WindowHandle);

public sealed record DialogPluginTarget(
    string PluginId,
    DialogWindowSnapshot Window)
    : DialogJumpTarget($"plugin:{PluginId}:{Window.WindowHandle.ToInt64():X}", Window.WindowHandle);

public sealed record DialogWindowSnapshot(
    IntPtr WindowHandle,
    uint ProcessId,
    string ProcessName,
    string ClassName,
    string Title,
    DateTimeOffset CapturedAt);

public interface IDialogJumpPlugin
{
    string Id { get; }

    string Name { get; }

    DialogPluginTarget? TryCaptureActiveTarget();

    Task<DialogJumpResult> JumpToFolderAsync(
        DialogPluginTarget target,
        string folderPath,
        CancellationToken cancellationToken);
}
