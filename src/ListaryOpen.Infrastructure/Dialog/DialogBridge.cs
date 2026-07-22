using System.IO;
using System.Diagnostics;
using ListaryOpen.Infrastructure.Hooks;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Dialog;

public sealed class DialogBridge
{
    private readonly IHookQuickSwitchBridge? _hookBridge;
    private readonly IReadOnlyDictionary<string, IDialogJumpPlugin> _plugins;
    private readonly IDialogWindowIdentityReader _windowIdentityReader;

    public DialogBridge(
        IHookQuickSwitchBridge? hookBridge,
        IReadOnlyList<IDialogJumpPlugin>? plugins = null)
        : this(hookBridge, plugins, new Win32DialogWindowIdentityReader())
    {
    }

    internal DialogBridge(
        IHookQuickSwitchBridge? hookBridge,
        IReadOnlyList<IDialogJumpPlugin>? plugins,
        IDialogWindowIdentityReader windowIdentityReader)
    {
        _hookBridge = hookBridge;
        _windowIdentityReader = windowIdentityReader ?? throw new ArgumentNullException(nameof(windowIdentityReader));
        var pluginList = plugins?.Where(plugin => plugin is not null).ToArray()
            ?? Array.Empty<IDialogJumpPlugin>();
        var duplicate = pluginList
            .GroupBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate dialog plugin id: {duplicate.Key}", nameof(plugins));
        }

        _plugins = pluginList.ToDictionary(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<DialogJumpTarget?> TryCaptureActiveTargetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hookBridge is not null)
        {
            var hookDialog = await _hookBridge.GetActiveDialogAsync(cancellationToken).ConfigureAwait(false);
            if (hookDialog is not null)
            {
                return new NativeHookDialogTarget(hookDialog);
            }
        }

        var matches = _plugins.Values
            .Select(plugin => plugin.TryCaptureActiveTarget())
            .Where(target => target is not null)
            .Cast<DialogPluginTarget>()
            .Where(IsExactForegroundPluginTarget)
            .ToArray();
        if (matches.Any(target => string.Equals(target.Window.ClassName, "#32770", StringComparison.Ordinal)))
        {
            return null;
        }

        return matches.Length == 1 ? matches[0] : null;
    }

    public Task<DialogJumpResult> JumpToFolderAsync(
        DialogJumpTarget target,
        string folderPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(folderPath))
        {
            return Task.FromResult(new DialogJumpResult(
                DialogJumpStatus.TargetGone,
                $"The selected folder no longer exists: {folderPath}"));
        }

        return target switch
        {
            NativeHookDialogTarget native => JumpNativeHookTargetAsync(native, folderPath, cancellationToken),
            DialogPluginTarget plugin => JumpPluginTargetAsync(plugin, folderPath, cancellationToken),
            _ => Task.FromResult(new DialogJumpResult(DialogJumpStatus.Failed, "Unknown dialog jump target."))
        };
    }

    public Task<DialogJumpResult> JumpToFolderAsync(
        HookDialogContext dialog,
        string folderPath,
        CancellationToken cancellationToken) =>
        JumpToFolderAsync(new NativeHookDialogTarget(dialog), folderPath, cancellationToken);

    public async Task<DialogJumpResult> JumpToFolderAsync(
        string folderPath,
        CancellationToken cancellationToken)
    {
        var target = await TryCaptureActiveTargetAsync(cancellationToken).ConfigureAwait(false);
        return target is null
            ? new DialogJumpResult(
                DialogJumpStatus.Failed,
                "No native-hook or dialog-plugin target is available.")
            : await JumpToFolderAsync(target, folderPath, cancellationToken).ConfigureAwait(false);
    }

    public Task<DialogJumpResult> JumpToFolderAsync(
        IntPtr dialogWindow,
        string folderPath,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DialogJumpResult(
            DialogJumpStatus.Failed,
            "A captured native-hook or dialog-plugin target is required."));

    private async Task<DialogJumpResult> JumpNativeHookTargetAsync(
        NativeHookDialogTarget target,
        string folderPath,
        CancellationToken cancellationToken)
    {
        if (_hookBridge is null)
        {
            return new DialogJumpResult(DialogJumpStatus.Failed, "Native hook integration is not available.");
        }

        try
        {
            var result = await _hookBridge
                .JumpDialogToFolderAsync(target.Dialog, folderPath, cancellationToken)
                .ConfigureAwait(false);
            return MapHookResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DialogJumpResult(DialogJumpStatus.Failed, $"Native hook failed: {exception.Message}");
        }
    }

    private async Task<DialogJumpResult> JumpPluginTargetAsync(
        DialogPluginTarget target,
        string folderPath,
        CancellationToken cancellationToken)
    {
        if (string.Equals(target.Window.ClassName, "#32770", StringComparison.Ordinal))
        {
            return new DialogJumpResult(
                DialogJumpStatus.UnsupportedDialog,
                "Standard dialogs must use the native hook.");
        }

        if (!_plugins.TryGetValue(target.PluginId, out var plugin))
        {
            return new DialogJumpResult(DialogJumpStatus.Failed, $"Dialog plugin is not loaded: {target.PluginId}");
        }

        if (!IsExactForegroundPluginTarget(target))
        {
            return new DialogJumpResult(
                DialogJumpStatus.TargetGone,
                "The plugin target is no longer the exact foreground process and window captured by the plugin.");
        }

        try
        {
            return await plugin.JumpToFolderAsync(target, folderPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DialogJumpResult(DialogJumpStatus.Failed, $"{plugin.Name} failed: {exception.Message}");
        }
    }

    private bool IsExactForegroundPluginTarget(DialogPluginTarget target)
    {
        var expected = target.Window;
        if (expected.WindowHandle == IntPtr.Zero ||
            expected.ProcessId == 0 ||
            _windowIdentityReader.GetForegroundWindow() != expected.WindowHandle ||
            !_windowIdentityReader.TryRead(expected.WindowHandle, out var actual))
        {
            return false;
        }

        return actual.WindowHandle == expected.WindowHandle &&
            actual.ProcessId == expected.ProcessId &&
            string.Equals(
                NormalizeProcessName(actual.ProcessName),
                NormalizeProcessName(expected.ProcessName),
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(actual.ClassName, expected.ClassName, StringComparison.Ordinal);
    }

    private static string NormalizeProcessName(string? processName)
    {
        var fileName = Path.GetFileName(processName?.Trim() ?? string.Empty);
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    private static DialogJumpResult MapHookResult(HookJumpResult result)
    {
        var status = result.Status switch
        {
            HookJumpStatus.Success => DialogJumpStatus.Success,
            HookJumpStatus.AccessDenied => DialogJumpStatus.PermissionLimited,
            HookJumpStatus.TargetGone => DialogJumpStatus.TargetGone,
            HookJumpStatus.UnsupportedDialog => DialogJumpStatus.UnsupportedDialog,
            _ => DialogJumpStatus.Failed
        };
        return new DialogJumpResult(status, result.Message);
    }
}

internal sealed record DialogWindowIdentity(
    IntPtr WindowHandle,
    uint ProcessId,
    string ProcessName,
    string ClassName);

internal interface IDialogWindowIdentityReader
{
    IntPtr GetForegroundWindow();

    bool TryRead(IntPtr windowHandle, out DialogWindowIdentity identity);
}

internal sealed class Win32DialogWindowIdentityReader : IDialogWindowIdentityReader
{
    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public bool TryRead(IntPtr windowHandle, out DialogWindowIdentity identity)
    {
        identity = new DialogWindowIdentity(IntPtr.Zero, 0, string.Empty, string.Empty);
        if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            return false;
        }

        var className = new char[256];
        var classNameLength = NativeMethods.GetClassName(windowHandle, className, className.Length);
        if (classNameLength <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            identity = new DialogWindowIdentity(
                windowHandle,
                processId,
                process.ProcessName,
                new string(className, 0, classNameLength));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or System.ComponentModel.Win32Exception
                                           or NotSupportedException)
        {
            return false;
        }
    }
}
