using System.IO;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Dialog;

public sealed class DialogBridge
{
    private readonly IDialogAutomation _automation;
    private readonly IHookQuickSwitchBridge? _hookBridge;

    public DialogBridge(IDialogAutomation automation, IHookQuickSwitchBridge? hookBridge = null)
    {
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _hookBridge = hookBridge;
    }

    public async Task<DialogJumpResult> JumpToFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path cannot be empty or whitespace.", nameof(folderPath));
        }

        string? hookFallbackContext = null;
        if (_hookBridge is not null && Directory.Exists(folderPath))
        {
            try
            {
                var hookResult = await _hookBridge.JumpActiveDialogToFolderAsync(folderPath, cancellationToken).ConfigureAwait(false);
                if (hookResult.Status == HookJumpStatus.Success)
                {
                    return new DialogJumpResult(DialogJumpStatus.Success, hookResult.Message);
                }

                if (hookResult.Status is HookJumpStatus.AccessDenied)
                {
                    return new DialogJumpResult(DialogJumpStatus.PermissionLimited, hookResult.Message);
                }

                if (hookResult.Status is HookJumpStatus.TargetGone)
                {
                    return new DialogJumpResult(DialogJumpStatus.TargetGone, hookResult.Message);
                }

                hookFallbackContext = CreateHookFallbackContext(hookResult);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                hookFallbackContext = $"hook failure: {exception.Message}";
                // Hook quick switch is optional; existing automation remains the fallback boundary.
            }
        }

        var probe = _automation.ProbeActiveDialog();
        switch (probe.Status)
        {
            case DialogProbeStatus.UnsupportedDialog:
                return new DialogJumpResult(DialogJumpStatus.UnsupportedDialog, probe.Message);

            case DialogProbeStatus.PermissionLimited:
                return new DialogJumpResult(DialogJumpStatus.PermissionLimited, probe.Message);

            case DialogProbeStatus.StandardDialog:
                if (!Directory.Exists(folderPath))
                {
                    return new DialogJumpResult(DialogJumpStatus.TargetGone, $"The selected folder no longer exists: {folderPath}");
                }

                var success = await _automation.SetFolderAsync(folderPath, cancellationToken);
                var degradedSuccess = !string.IsNullOrWhiteSpace(hookFallbackContext);
                return success
                    ? new DialogJumpResult(
                        DialogJumpStatus.Success,
                        CreateSuccessMessage(hookFallbackContext),
                        isDegradedSuccess: degradedSuccess)
                    : new DialogJumpResult(DialogJumpStatus.Failed, "Dialog folder could not be changed.");

            default:
                return new DialogJumpResult(DialogJumpStatus.Failed, $"Unknown dialog probe status: {(int)probe.Status}.");
        }
    }

    private static string CreateSuccessMessage(string? hookFallbackContext) =>
        string.IsNullOrWhiteSpace(hookFallbackContext)
            ? "Dialog folder changed."
            : $"Dialog folder changed via fallback automation after {hookFallbackContext}";

    private static string CreateHookFallbackContext(HookJumpResult hookResult) =>
        string.IsNullOrWhiteSpace(hookResult.Message)
            ? $"hook {hookResult.Status}."
            : $"hook {hookResult.Status}: {hookResult.Message}";
}
