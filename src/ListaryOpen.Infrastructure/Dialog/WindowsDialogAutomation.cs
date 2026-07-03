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
    private static readonly TimeSpan NavigationConfirmationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NavigationConfirmationPollInterval = TimeSpan.FromMilliseconds(100);
    private const string FileNameAutomationId = "1148";
    private const string CommitButtonAutomationId = "1";
    private const string StandardDialogClassName = "#32770";
    private const string FirefoxDialogClassName = "MozillaDialogClass";

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

        var dialogHandle = ResolveActiveDialogHandle(
            handle,
            GetClassName,
            GetProcessName,
            EnumerateVisibleTopLevelWindows);
        if (dialogHandle == IntPtr.Zero)
        {
            return DialogProbeResult.Unsupported("The active window is not a standard dialog.");
        }

        NativeMethods.GetWindowThreadProcessId(dialogHandle, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);

            var activeDialog = AutomationElement.FromHandle(dialogHandle);
            if (activeDialog is null)
            {
                return DialogProbeResult.Unsupported("Dialog automation tree is unavailable.");
            }

            _ = activeDialog.Current.ControlType;
            _activeDialog = activeDialog;
            _activeDialogHandle = dialogHandle;
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

        if (!TryFindFileNameEdit(activeDialog, out var fileNameEdit))
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

        if (!TryFindCommitButton(activeDialog, out var commitButton))
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
                valuePattern,
                folderPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> ConfirmFolderNavigationAsync(
        ValuePattern fileNameValuePattern,
        string targetFolderPath,
        CancellationToken cancellationToken)
    {
        var normalizedTargetFolderPath = NormalizeFolderPath(targetFolderPath);
        var deadline = DateTimeOffset.UtcNow + NavigationConfirmationTimeout;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetValue(fileNameValuePattern, out var currentFileNameValue))
            {
                return false;
            }

            if (!MatchesSubmittedFolderValue(currentFileNameValue, normalizedTargetFolderPath))
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

    private static string? GetProcessName(IntPtr handle)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<IntPtr> EnumerateVisibleTopLevelWindows()
    {
        var windows = new List<IntPtr>();
        NativeMethods.EnumWindows(
            (handle, _) =>
            {
                if (NativeMethods.IsWindowVisible(handle))
                {
                    windows.Add(handle);
                }

                return true;
            },
            IntPtr.Zero);

        return windows;
    }

    internal static IntPtr ResolveActiveDialogHandle(
        IntPtr foregroundHandle,
        Func<IntPtr, string> classNameProvider,
        Func<IntPtr, string?> processNameProvider,
        Func<IEnumerable<IntPtr>> topLevelWindowProvider)
    {
        ArgumentNullException.ThrowIfNull(classNameProvider);
        ArgumentNullException.ThrowIfNull(processNameProvider);
        ArgumentNullException.ThrowIfNull(topLevelWindowProvider);

        if (foregroundHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (IsSupportedFileDialogClass(classNameProvider(foregroundHandle)))
        {
            return foregroundHandle;
        }

        var foregroundProcessName = processNameProvider(foregroundHandle);
        var browserDialogFallback = IntPtr.Zero;
        foreach (var candidateHandle in topLevelWindowProvider())
        {
            if (candidateHandle == IntPtr.Zero || candidateHandle == foregroundHandle)
            {
                continue;
            }

            if (!IsSupportedFileDialogClass(classNameProvider(candidateHandle)))
            {
                continue;
            }

            var candidateProcessName = processNameProvider(candidateHandle);
            if (ProcessNamesEqual(candidateProcessName, foregroundProcessName))
            {
                return candidateHandle;
            }

            if (browserDialogFallback == IntPtr.Zero && IsKnownBrowserProcessName(candidateProcessName))
            {
                browserDialogFallback = candidateHandle;
            }
        }

        return IsListaryProcessName(foregroundProcessName)
            ? browserDialogFallback
            : IntPtr.Zero;
    }

    internal static bool IsSupportedFileDialogClass(string? className)
    {
        return string.Equals(className, StandardDialogClassName, StringComparison.Ordinal) ||
            string.Equals(className, FirefoxDialogClassName, StringComparison.Ordinal);
    }

    internal static bool IsKnownBrowserProcessName(string? processName)
    {
        return string.Equals(processName, "firefox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "chrome", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "msedge", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsListaryProcessName(string? processName)
    {
        return string.Equals(processName, "ListaryOpen.App", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProcessNamesEqual(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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

    private static bool TryFindFileNameEdit(
        AutomationElement activeDialog,
        [NotNullWhen(true)] out AutomationElement? fileNameEdit)
    {
        if (TryFindFirst(
                activeDialog,
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, FileNameAutomationId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)),
                out fileNameEdit))
        {
            return true;
        }

        return TryFindFirstValueElement(
            activeDialog,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            out fileNameEdit);
    }

    private static bool TryFindCommitButton(
        AutomationElement activeDialog,
        [NotNullWhen(true)] out AutomationElement? commitButton)
    {
        if (TryFindFirst(
                activeDialog,
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.AutomationIdProperty, CommitButtonAutomationId),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)),
                out commitButton))
        {
            return true;
        }

        if (!TryFindAll(
                activeDialog,
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                out var buttons))
        {
            return false;
        }

        foreach (AutomationElement button in buttons)
        {
            if (IsCommitButtonName(GetAutomationName(button)) &&
                TryGetInvokePattern(button, out _))
            {
                commitButton = button;
                return true;
            }
        }

        commitButton = null;
        return false;
    }

    private static bool TryFindFirstValueElement(
        AutomationElement root,
        Condition condition,
        [NotNullWhen(true)] out AutomationElement? result)
    {
        if (!TryFindAll(root, TreeScope.Descendants, condition, out var elements))
        {
            result = null;
            return false;
        }

        foreach (AutomationElement element in elements)
        {
            if (TryGetValuePattern(element, out var valuePattern) && !IsReadOnly(valuePattern))
            {
                result = element;
                return true;
            }
        }

        result = null;
        return false;
    }

    private static bool TryFindAll(
        AutomationElement element,
        TreeScope scope,
        Condition condition,
        [NotNullWhen(true)] out AutomationElementCollection? results)
    {
        try
        {
            results = element.FindAll(scope, condition);
            return results.Count > 0;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            results = null;
            return false;
        }
    }

    private static string GetAutomationName(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return string.Empty;
        }
    }

    private static bool IsCommitButtonName(string name)
    {
        return name.Contains("Open", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Choose", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Select", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("打开", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("选择", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("保存", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsReadOnly(ValuePattern valuePattern)
    {
        try
        {
            return valuePattern.Current.IsReadOnly;
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return true;
        }
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
