using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Automation;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Dialog;

public sealed class WindowsDialogAutomation : IDialogAutomation
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    private AutomationElement? _activeDialog;

    public DialogProbeResult ProbeActiveDialog()
    {
        _activeDialog = null;

        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return DialogProbeResult.Unsupported("No foreground window.");
        }

        var className = GetClassName(handle);
        if (!string.Equals(className, "#32770", StringComparison.Ordinal))
        {
            return DialogProbeResult.Unsupported("The active window is not a standard dialog.");
        }

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        try
        {
            var process = Process.GetProcessById((int)processId);
            if (process.MainWindowHandle == IntPtr.Zero)
            {
                return DialogProbeResult.Unsupported("Dialog process has no main window.");
            }

            var activeDialog = AutomationElement.FromHandle(handle);
            _ = activeDialog.Current.ControlType;
            _activeDialog = activeDialog;
        }
        catch (ArgumentException)
        {
            return DialogProbeResult.Unsupported("Dialog process exited.");
        }
        catch (Exception exception) when (IsPermissionException(exception))
        {
            return DialogProbeResult.PermissionLimited("The active standard dialog is permission-limited.");
        }

        return _activeDialog is null
            ? DialogProbeResult.Unsupported("Dialog automation tree is unavailable.")
            : DialogProbeResult.StandardDialog();
    }

    public Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeDialog = _activeDialog;
        if (activeDialog is null || !Directory.Exists(folderPath))
        {
            return Task.FromResult(false);
        }

        var edit = activeDialog.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        if (edit is null)
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
        {
            valuePattern.SetValue(folderPath);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private static string GetClassName(IntPtr handle)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(handle, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static bool IsPermissionException(Exception exception)
    {
        return exception is UnauthorizedAccessException
            or SecurityException
            or Win32Exception { NativeErrorCode: 5 }
            or COMException { HResult: AccessDeniedHResult };
    }
}
