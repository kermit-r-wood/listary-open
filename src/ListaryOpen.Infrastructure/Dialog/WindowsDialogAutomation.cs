using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Windows.Automation;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Dialog;

public sealed class WindowsDialogAutomation : IDialogAutomation
{
    private enum CurrentFolderQueryStatus
    {
        Unavailable,
        Failed,
        Available
    }

    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int WM_USER = 0x0400;
    private const int CDM_FIRST = WM_USER + 100;
    private const int CDM_GETFOLDERPATH = CDM_FIRST + 0x0002;
    private const int FolderPathBufferCapacity = 32768;
    private static readonly TimeSpan NavigationConfirmationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NavigationConfirmationPollInterval = TimeSpan.FromMilliseconds(100);
    private const string FileNameAutomationId = "1148";
    private const string CommitButtonAutomationId = "1";

    private AutomationElement? _activeDialog;
    private IntPtr _activeDialogHandle;

    public DialogProbeResult ProbeActiveDialog()
    {
        _activeDialog = null;
        _activeDialogHandle = IntPtr.Zero;

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
            _activeDialogHandle = handle;
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

    public async Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeDialog = _activeDialog;
        var activeDialogHandle = _activeDialogHandle;
        if (activeDialog is null || activeDialogHandle == IntPtr.Zero || !Directory.Exists(folderPath))
        {
            return false;
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
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetValuePattern(fileNameEdit, out var valuePattern))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TrySetValue(valuePattern, folderPath))
        {
            return false;
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
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetInvokePattern(commitButton, out var invokePattern))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryInvoke(invokePattern))
        {
            return false;
        }

        return await ConfirmFolderNavigationAsync(
                activeDialogHandle,
                valuePattern,
                folderPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> ConfirmFolderNavigationAsync(
        IntPtr dialogHandle,
        ValuePattern fileNameValuePattern,
        string targetFolderPath,
        CancellationToken cancellationToken)
    {
        var normalizedTargetFolderPath = NormalizeFolderPath(targetFolderPath);
        var deadline = DateTimeOffset.UtcNow + NavigationConfirmationTimeout;
        var currentFolderQueryReturnedPath = false;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentFolderQueryStatus = TryGetCurrentFolderPath(dialogHandle, out var currentFolderPath);
            if (currentFolderQueryStatus == CurrentFolderQueryStatus.Available)
            {
                currentFolderQueryReturnedPath = true;
                if (MatchesSubmittedFolderValue(currentFolderPath!, normalizedTargetFolderPath))
                {
                    return true;
                }
            }
            else if (currentFolderQueryStatus == CurrentFolderQueryStatus.Failed)
            {
                return false;
            }
            else if (!currentFolderQueryReturnedPath
                && TryGetValue(fileNameValuePattern, out var currentFileNameValue)
                && !MatchesSubmittedFolderValue(currentFileNameValue, normalizedTargetFolderPath))
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(NavigationConfirmationPollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (true);
    }

    private static CurrentFolderQueryStatus TryGetCurrentFolderPath(
        IntPtr dialogHandle,
        out string? folderPath)
    {
        try
        {
            var buffer = new StringBuilder(FolderPathBufferCapacity);
            var result = NativeMethods.SendMessage(
                dialogHandle,
                CDM_GETFOLDERPATH,
                new IntPtr(buffer.Capacity),
                buffer);

            if (result.ToInt64() > 0 && result.ToInt64() < buffer.Capacity)
            {
                folderPath = buffer.ToString();
                return string.IsNullOrWhiteSpace(folderPath)
                    ? CurrentFolderQueryStatus.Unavailable
                    : CurrentFolderQueryStatus.Available;
            }
        }
        catch (Exception exception) when (IsPermissionException(exception) || exception is COMException)
        {
            folderPath = null;
            return CurrentFolderQueryStatus.Failed;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
        }

        folderPath = null;
        return CurrentFolderQueryStatus.Unavailable;
    }

    private static string NormalizeFolderPath(string folderPath)
    {
        var fullPath = Path.GetFullPath(folderPath);
        var root = Path.GetPathRoot(fullPath);
        var trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.IsNullOrEmpty(trimmedPath) && !string.IsNullOrEmpty(root)
            ? root
            : trimmedPath;
    }

    private static bool MatchesSubmittedFolderValue(string value, string normalizedTargetFolderPath)
    {
        try
        {
            return string.Equals(
                NormalizeFolderPath(value),
                normalizedTargetFolderPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(
                value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalizedTargetFolderPath,
                StringComparison.OrdinalIgnoreCase);
        }
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

    private static bool TryGetValue(ValuePattern valuePattern, [NotNullWhen(true)] out string? value)
    {
        try
        {
            value = valuePattern.Current.Value;
            return value is not null;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            value = null;
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
