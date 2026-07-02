using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Automation;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Dialog;

public sealed class WindowsDialogAutomation : IDialogAutomation
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const string FileNameAutomationId = "1148";
    private const string CommitButtonAutomationId = "1";

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
            using var process = Process.GetProcessById((int)processId);

            var activeDialog = AutomationElement.FromHandle(handle);
            if (activeDialog is null)
            {
                return DialogProbeResult.Unsupported("Dialog automation tree is unavailable.");
            }

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

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryFindFirst(
                activeDialog,
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, FileNameAutomationId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)),
                out var fileNameEdit))
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetValuePattern(fileNameEdit, out var valuePattern))
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TrySetValue(valuePattern, folderPath))
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryFindFirst(
                activeDialog,
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, CommitButtonAutomationId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)),
                out var commitButton))
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetInvokePattern(commitButton, out var invokePattern))
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(TryInvoke(invokePattern));
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

    private static bool TryFindFirst(
        AutomationElement element,
        TreeScope scope,
        Condition condition,
        [NotNullWhen(true)] out AutomationElement? result)
    {
        try
        {
            result = element.FindFirst(scope, condition);
            return result is not null;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            result = null;
            return false;
        }
    }

    private static bool TryGetValuePattern(AutomationElement element, [NotNullWhen(true)] out ValuePattern? valuePattern)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern typedPattern)
            {
                valuePattern = typedPattern;
                return true;
            }
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
        }

        valuePattern = null;
        return false;
    }

    private static bool TryGetInvokePattern(AutomationElement element, [NotNullWhen(true)] out InvokePattern? invokePattern)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) && pattern is InvokePattern typedPattern)
            {
                invokePattern = typedPattern;
                return true;
            }
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
        }

        invokePattern = null;
        return false;
    }

    private static bool TrySetValue(ValuePattern valuePattern, string value)
    {
        try
        {
            valuePattern.SetValue(value);
            return true;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return false;
        }
    }

    private static bool TryInvoke(InvokePattern invokePattern)
    {
        try
        {
            invokePattern.Invoke();
            return true;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return false;
        }
    }

    private static bool IsExpectedAutomationException(Exception exception)
    {
        return exception is ElementNotAvailableException
            or InvalidOperationException
            or UnauthorizedAccessException
            or SecurityException
            or Win32Exception { NativeErrorCode: 5 }
            or COMException;
    }
}
