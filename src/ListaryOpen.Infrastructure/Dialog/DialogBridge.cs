using System.IO;
using System.Diagnostics;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Dialog;

public sealed class DialogBridge
{
    private readonly IDialogAutomation _automation;
    private readonly IHookQuickSwitchBridge? _hookBridge;
    private readonly IReadOnlyList<ICustomDialogAdapter> _customAdapters;

    public DialogBridge(
        IDialogAutomation automation,
        IHookQuickSwitchBridge? hookBridge = null,
        IReadOnlyList<ICustomDialogAdapter>? customAdapters = null)
    {
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _hookBridge = hookBridge;
        _customAdapters = customAdapters?.Where(adapter => adapter is not null).ToArray()
            ?? Array.Empty<ICustomDialogAdapter>();
    }

    public async Task<DialogJumpResult> JumpToFolderAsync(
        HookDialogContext dialog,
        string folderPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        if (!Directory.Exists(folderPath))
        {
            return new DialogJumpResult(
                DialogJumpStatus.TargetGone,
                $"The selected folder no longer exists: {folderPath}");
        }

        if (_hookBridge is null)
        {
            return new DialogJumpResult(DialogJumpStatus.Failed, "Hook dialog integration is not available.");
        }

        HookJumpResult hookResult;
        try
        {
            hookResult = await _hookBridge
                .JumpDialogToFolderAsync(dialog, folderPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DialogJumpResult(DialogJumpStatus.Failed, $"Hook dialog jump failed: {exception.Message}");
        }

        var status = hookResult.Status switch
        {
            HookJumpStatus.Success => DialogJumpStatus.Success,
            HookJumpStatus.AccessDenied => DialogJumpStatus.PermissionLimited,
            HookJumpStatus.TargetGone => DialogJumpStatus.TargetGone,
            HookJumpStatus.UnsupportedDialog => DialogJumpStatus.UnsupportedDialog,
            _ => DialogJumpStatus.Failed
        };
        return new DialogJumpResult(status, hookResult.Message);
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

        foreach (var adapter in _customAdapters)
        {
            var adapterResult = await TryJumpWithCustomAdapterAsync(
                    adapter,
                    folderPath,
                    hookFallbackContext,
                    cancellationToken)
                .ConfigureAwait(false);
            if (adapterResult is not null)
            {
                return adapterResult;
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

    private static async Task<DialogJumpResult?> TryJumpWithCustomAdapterAsync(
        ICustomDialogAdapter adapter,
        string folderPath,
        string? hookFallbackContext,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!adapter.CanHandleActiveWindow())
            {
                return null;
            }

            if (!Directory.Exists(folderPath))
            {
                return new DialogJumpResult(
                    DialogJumpStatus.TargetGone,
                    $"The selected folder no longer exists: {folderPath}");
            }

            var success = await adapter.SetFolderAsync(folderPath, cancellationToken).ConfigureAwait(false);
            return success
                ? new DialogJumpResult(
                    DialogJumpStatus.Success,
                    CreateAdapterSuccessMessage(adapter.Name, hookFallbackContext),
                    isDegradedSuccess: !string.IsNullOrWhiteSpace(hookFallbackContext))
                : new DialogJumpResult(
                    DialogJumpStatus.Failed,
                    $"{adapter.Name} folder could not be changed.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
            return new DialogJumpResult(
                DialogJumpStatus.Failed,
                $"{adapter.Name} adapter failed: {exception.Message}");
        }
    }

    private static string CreateAdapterSuccessMessage(string adapterName, string? hookFallbackContext)
    {
        var message = $"Dialog folder changed via {adapterName}.";
        return string.IsNullOrWhiteSpace(hookFallbackContext)
            ? message
            : $"{message.TrimEnd('.')} after {hookFallbackContext}";
    }
}
