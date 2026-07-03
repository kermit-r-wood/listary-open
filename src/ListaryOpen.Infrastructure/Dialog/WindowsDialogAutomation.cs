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
            GetOwnerWindow,
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

        if (!TryFindCommitButton(activeDialog, out var commitButton))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetInvokePattern(commitButton, out var invokePattern))
        {
            return false;
        }

        return await RunWithoutDialogRedrawAsync(
                activeDialogHandle,
                EnumerateDialogRedrawHandles,
                SetDialogRedrawEnabled,
                InvalidateDialogWindow,
                async () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TrySetValue(valuePattern, folderPath))
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
                })
            .ConfigureAwait(false);
    }

    internal static async Task<bool> RunWithoutDialogRedrawAsync(
        IntPtr dialogHandle,
        Action<IntPtr, bool> setRedrawEnabled,
        Action<IntPtr> redrawWindow,
        Func<Task<bool>> operation)
    {
        return await RunWithoutDialogRedrawAsync(
                dialogHandle,
                handle => new[] { handle },
                setRedrawEnabled,
                redrawWindow,
                operation)
            .ConfigureAwait(false);
    }

    internal static async Task<bool> RunWithoutDialogRedrawAsync(
        IntPtr dialogHandle,
        Func<IntPtr, IReadOnlyList<IntPtr>> redrawHandleProvider,
        Action<IntPtr, bool> setRedrawEnabled,
        Action<IntPtr> redrawWindow,
        Func<Task<bool>> operation)
    {
        ArgumentNullException.ThrowIfNull(redrawHandleProvider);
        ArgumentNullException.ThrowIfNull(setRedrawEnabled);
        ArgumentNullException.ThrowIfNull(redrawWindow);
        ArgumentNullException.ThrowIfNull(operation);

        if (dialogHandle == IntPtr.Zero)
        {
            return await operation().ConfigureAwait(false);
        }

        var redrawHandles = NormalizeRedrawHandles(redrawHandleProvider(dialogHandle), dialogHandle);
        foreach (var handle in redrawHandles)
        {
            setRedrawEnabled(handle, false);
        }

        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            for (var index = redrawHandles.Count - 1; index >= 0; index--)
            {
                setRedrawEnabled(redrawHandles[index], true);
            }

            redrawWindow(dialogHandle);
        }
    }

    private static IReadOnlyList<IntPtr> NormalizeRedrawHandles(
        IReadOnlyList<IntPtr>? handles,
        IntPtr fallbackHandle)
    {
        if (handles is null || handles.Count == 0)
        {
            return new[] { fallbackHandle };
        }

        var normalizedHandles = new List<IntPtr>(handles.Count);
        foreach (var handle in handles)
        {
            if (handle != IntPtr.Zero && !normalizedHandles.Contains(handle))
            {
                normalizedHandles.Add(handle);
            }
        }

        if (normalizedHandles.Count == 0)
        {
            normalizedHandles.Add(fallbackHandle);
        }

        return normalizedHandles;
    }

    private static IReadOnlyList<IntPtr> EnumerateDialogRedrawHandles(IntPtr dialogHandle)
    {
        var handles = new List<IntPtr> { dialogHandle };
        NativeMethods.EnumChildWindows(
            dialogHandle,
            (childHandle, _) =>
            {
                if (childHandle != IntPtr.Zero)
                {
                    handles.Add(childHandle);
                }

                return true;
            },
            IntPtr.Zero);

        return handles;
    }

    private static void SetDialogRedrawEnabled(IntPtr dialogHandle, bool enabled)
    {
        _ = NativeMethods.SendMessage(
            dialogHandle,
            NativeMethods.WM_SETREDRAW,
            enabled ? new UIntPtr(1) : UIntPtr.Zero,
            IntPtr.Zero);
    }

    private static void InvalidateDialogWindow(IntPtr dialogHandle)
    {
        _ = NativeMethods.RedrawWindow(
            dialogHandle,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_ALLCHILDREN | NativeMethods.RDW_UPDATENOW);
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

    private static IntPtr GetOwnerWindow(IntPtr handle)
    {
        return NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER);
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
        Func<IntPtr, IntPtr> ownerWindowProvider,
        Func<IEnumerable<IntPtr>> topLevelWindowProvider)
    {
        ArgumentNullException.ThrowIfNull(classNameProvider);
        ArgumentNullException.ThrowIfNull(processNameProvider);
        ArgumentNullException.ThrowIfNull(ownerWindowProvider);
        ArgumentNullException.ThrowIfNull(topLevelWindowProvider);

        if (foregroundHandle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var foregroundClassName = classNameProvider(foregroundHandle);
        if (IsSupportedFileDialogClass(foregroundClassName))
        {
            return foregroundHandle;
        }

        var foregroundProcessName = processNameProvider(foregroundHandle);
        var browserOwnedDialogs = IsListaryOpenWindow(foregroundClassName, foregroundProcessName)
            ? new List<IntPtr>()
            : null;
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
            if (ProcessNamesEqual(candidateProcessName, foregroundProcessName) &&
                ownerWindowProvider(candidateHandle) == foregroundHandle)
            {
                return candidateHandle;
            }

            if (browserOwnedDialogs is not null &&
                IsBrowserOwnedDialog(candidateHandle, candidateProcessName, ownerWindowProvider, processNameProvider))
            {
                browserOwnedDialogs.Add(candidateHandle);
            }
        }

        if (browserOwnedDialogs?.Count == 1)
        {
            return browserOwnedDialogs[0];
        }

        return IntPtr.Zero;
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

    private static bool IsListaryOpenWindow(string? className, string? processName)
    {
        return (!string.IsNullOrWhiteSpace(processName) &&
                processName.StartsWith("ListaryOpen", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(className) &&
                className.Contains("HwndWrapper[ListaryOpen", StringComparison.Ordinal));
    }

    private static bool IsBrowserOwnedDialog(
        IntPtr candidateHandle,
        string? candidateProcessName,
        Func<IntPtr, IntPtr> ownerWindowProvider,
        Func<IntPtr, string?> processNameProvider)
    {
        var ownerHandle = ownerWindowProvider(candidateHandle);
        if (ownerHandle == IntPtr.Zero)
        {
            return false;
        }

        var ownerProcessName = processNameProvider(ownerHandle);
        return IsKnownBrowserProcessName(candidateProcessName) &&
            IsKnownBrowserProcessName(ownerProcessName) &&
            ProcessNamesEqual(candidateProcessName, ownerProcessName);
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
        return TryFindFirstValueElement(
            activeDialog,
            Condition.TrueCondition,
            IsFileNameElement,
            out fileNameEdit);
    }

    private static bool TryFindCommitButton(
        AutomationElement activeDialog,
        [NotNullWhen(true)] out AutomationElement? commitButton)
    {
        return TryFindFirstInvokeElement(
            activeDialog,
            Condition.TrueCondition,
            IsCommitButtonElement,
            out commitButton);
    }

    private static bool TryFindFirstValueElement(
        AutomationElement root,
        Condition condition,
        Func<AutomationElement, bool> candidatePredicate,
        [NotNullWhen(true)] out AutomationElement? result)
    {
        ArgumentNullException.ThrowIfNull(candidatePredicate);

        if (!TryFindAll(root, TreeScope.Descendants, condition, out var elements))
        {
            result = null;
            return false;
        }

        foreach (AutomationElement element in elements)
        {
            if (candidatePredicate(element) &&
                TryGetValuePattern(element, out var valuePattern) &&
                !IsReadOnly(valuePattern))
            {
                result = element;
                return true;
            }
        }

        result = null;
        return false;
    }

    private static bool TryFindFirstInvokeElement(
        AutomationElement root,
        Condition condition,
        Func<AutomationElement, bool> candidatePredicate,
        [NotNullWhen(true)] out AutomationElement? result)
    {
        ArgumentNullException.ThrowIfNull(candidatePredicate);

        if (!TryFindAll(root, TreeScope.Descendants, condition, out var elements))
        {
            result = null;
            return false;
        }

        foreach (AutomationElement element in elements)
        {
            if (candidatePredicate(element) && TryGetInvokePattern(element, out _))
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

    private static bool IsFileNameElement(AutomationElement element)
    {
        try
        {
            return IsFileNameControlCandidate(
                element.Current.AutomationId,
                element.Current.Name,
                element.Current.ClassName,
                element.Current.ControlType.ProgrammaticName);
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return false;
        }
    }

    private static bool IsCommitButtonElement(AutomationElement element)
    {
        try
        {
            return IsCommitButtonCandidate(
                element.Current.AutomationId,
                element.Current.Name,
                element.Current.ClassName,
                element.Current.ControlType.ProgrammaticName);
        }
        catch (Exception exception) when (IsExpectedAutomationException(exception))
        {
            return false;
        }
    }

    internal static bool IsFileNameControlCandidate(
        string? automationId,
        string? name,
        string? className,
        string? controlTypeProgrammaticName)
    {
        if (string.Equals(automationId, "System.ItemNameDisplay", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(automationId, FileNameAutomationId, StringComparison.Ordinal))
        {
            return IsTextEntryClassOrType(className, controlTypeProgrammaticName);
        }

        if (!string.IsNullOrWhiteSpace(automationId) &&
            automationId.Contains("FileName", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsFileNameLabel(name) && IsTextEntryClassOrType(className, controlTypeProgrammaticName);
    }

    internal static bool IsCommitButtonCandidate(
        string? automationId,
        string? name,
        string? className,
        string? controlTypeProgrammaticName)
    {
        if (string.Equals(automationId, CommitButtonAutomationId, StringComparison.Ordinal) &&
            IsButtonClassOrType(className, controlTypeProgrammaticName))
        {
            return true;
        }

        return IsButtonClassOrType(className, controlTypeProgrammaticName) && IsCommitButtonName(name ?? string.Empty);
    }

    private static bool IsTextEntryClassOrType(string? className, string? controlTypeProgrammaticName)
    {
        return string.Equals(className, "Edit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "ComboBox", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(className, "ComboBoxEx32", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(controlTypeProgrammaticName, "ControlType.Edit", StringComparison.Ordinal) ||
            string.Equals(controlTypeProgrammaticName, "ControlType.ComboBox", StringComparison.Ordinal) ||
            string.Equals(controlTypeProgrammaticName, "ControlType.Pane", StringComparison.Ordinal);
    }

    private static bool IsButtonClassOrType(string? className, string? controlTypeProgrammaticName)
    {
        return string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(controlTypeProgrammaticName, "ControlType.Button", StringComparison.Ordinal) ||
            string.Equals(controlTypeProgrammaticName, "ControlType.Pane", StringComparison.Ordinal);
    }

    private static bool ContainsFileNameLabel(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
            (name.Contains("File name", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("文件名", StringComparison.OrdinalIgnoreCase));
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
