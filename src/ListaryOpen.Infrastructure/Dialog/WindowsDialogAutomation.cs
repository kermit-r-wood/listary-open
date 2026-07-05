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
    private static readonly TimeSpan KeyboardFocusDelay = TimeSpan.FromMilliseconds(200);
    private const string FileNameAutomationId = "1148";
    private const string FolderNameAutomationId = "1152";
    private const string CommitButtonAutomationId = "1";
    private const string AddressBarAutomationId = "1001";
    private const int AddressBarEditControlId = 41477;
    private const string StandardDialogClassName = "#32770";
    private const string FirefoxDialogClassName = "MozillaDialogClass";
    private static readonly int[] PathEditDialogItemIds =
    {
        int.Parse(FileNameAutomationId)
    };

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
            EnumerateVisibleTopLevelWindows,
            GetProcessId,
            GetChildControlClassNames);
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
            var dialogShape = ClassifyFileDialogControls(GetChildControlClassNames(dialogHandle));
            if (string.Equals(GetClassName(dialogHandle), StandardDialogClassName, StringComparison.Ordinal) &&
                (!IsAutomatableFileDialogShape(dialogShape) ||
                    (dialogShape == FileDialogControlShape.Unsupported && !LooksLikeAutomatableFileDialog(activeDialog))))
            {
                return DialogProbeResult.Unsupported("The active standard dialog is not a file dialog.");
            }

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

        var keyboardStrategy = () => SubmitFolderNavigationWithKeyboardAsync(
                activeDialogHandle,
                folderPath,
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TryFocusFileNameEditWindow(activeDialogHandle);
                },
                path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TrySendKeyboardText(path);
                },
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TrySendKeyboardCommit();
                },
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ConfirmDialogStillOpen(activeDialogHandle);
                });

        var directAddressBarStrategy = () => SubmitFolderNavigationWithKeyboardAsync(
                activeDialogHandle,
                folderPath,
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TryFocusAddressBarEditWindow(activeDialogHandle);
                },
                path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TrySendKeyboardText(path);
                },
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TrySendKeyboardCommit();
                },
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ConfirmDialogStillOpen(activeDialogHandle);
                });

        var addressBarShortcutStrategy = () => SubmitFolderNavigationWithAddressBarAsync(
                activeDialogHandle,
                folderPath,
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TryFocusDialogWindow(activeDialogHandle);
                },
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TrySendAddressBarShortcut();
                },
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TryConfirmAddressBarFocus(activeDialogHandle);
                },
                path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TrySendKeyboardText(path);
                },
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return TrySendKeyboardCommit();
                },
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ConfirmDialogStillOpen(activeDialogHandle);
                });

        return await TryFolderNavigationStrategiesAsync(
                keyboardStrategy,
                directAddressBarStrategy,
                addressBarShortcutStrategy)
            .ConfigureAwait(false);
    }

    internal static Task<bool> SubmitFolderNavigationWithKeyboardAsync(
        IntPtr dialogHandle,
        string folderPath,
        Func<bool> focusFileNameEdit,
        Func<string, bool> sendFolderPath,
        Func<bool> sendCommit,
        Func<bool>? confirmNavigation = null)
    {
        ArgumentNullException.ThrowIfNull(focusFileNameEdit);
        ArgumentNullException.ThrowIfNull(sendFolderPath);
        ArgumentNullException.ThrowIfNull(sendCommit);

        if (!focusFileNameEdit() ||
            !sendFolderPath(folderPath) ||
            !sendCommit())
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(confirmNavigation?.Invoke() ?? true);
    }

    internal static Task<bool> SubmitFolderNavigationWithAddressBarAsync(
        IntPtr dialogHandle,
        string folderPath,
        Func<bool> focusDialog,
        Func<bool> focusAddressBar,
        Func<bool> confirmAddressBarFocus,
        Func<string, bool> sendFolderPath,
        Func<bool> sendCommit,
        Func<bool>? confirmNavigation = null)
    {
        ArgumentNullException.ThrowIfNull(focusDialog);
        ArgumentNullException.ThrowIfNull(focusAddressBar);
        ArgumentNullException.ThrowIfNull(confirmAddressBarFocus);
        ArgumentNullException.ThrowIfNull(sendFolderPath);
        ArgumentNullException.ThrowIfNull(sendCommit);

        if (dialogHandle == IntPtr.Zero ||
            !focusDialog() ||
            !focusAddressBar() ||
            !confirmAddressBarFocus() ||
            !sendFolderPath(folderPath) ||
            !sendCommit())
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(confirmNavigation?.Invoke() ?? true);
    }

    internal static async Task<bool> TryFolderNavigationStrategiesAsync(params Func<Task<bool>>[] strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        foreach (var strategy in strategies)
        {
            ArgumentNullException.ThrowIfNull(strategy);

            if (await strategy().ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFocusDialogWindow(IntPtr dialogHandle)
    {
        if (dialogHandle == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.SetForegroundWindow(dialogHandle);
        Thread.Sleep(KeyboardFocusDelay);
        return IsDialogOrOwnedWindow(NativeMethods.GetForegroundWindow(), dialogHandle, GetOwnerWindow);
    }

    private static bool TryConfirmAddressBarFocus(IntPtr dialogHandle)
    {
        return TryConfirmAddressBarFocus(
            dialogHandle,
            NativeMethods.GetForegroundWindow,
            GetOwnerWindow,
            NativeMethods.GetDlgItem,
            () => GetFocusedWindowForDialog(dialogHandle),
            NativeMethods.GetParent);
    }

    internal static bool TryConfirmAddressBarFocus(
        IntPtr dialogHandle,
        Func<IntPtr> getForegroundWindow,
        Func<IntPtr, IntPtr> getOwnerWindow,
        Func<IntPtr, int, IntPtr> getDlgItem,
        Func<IntPtr> getFocusedWindow,
        Func<IntPtr, IntPtr> getParentWindow)
    {
        ArgumentNullException.ThrowIfNull(getForegroundWindow);
        ArgumentNullException.ThrowIfNull(getOwnerWindow);
        ArgumentNullException.ThrowIfNull(getDlgItem);
        ArgumentNullException.ThrowIfNull(getFocusedWindow);
        ArgumentNullException.ThrowIfNull(getParentWindow);

        if (!IsDialogOrOwnedWindow(getForegroundWindow(), dialogHandle, getOwnerWindow))
        {
            return false;
        }

        var addressToolbarHandle = getDlgItem(dialogHandle, int.Parse(AddressBarAutomationId));
        var focusedWindow = getFocusedWindow();
        return addressToolbarHandle != IntPtr.Zero &&
            IsWindowOrParent(focusedWindow, addressToolbarHandle, getParentWindow);
    }

    private static bool TryFocusFileNameEditWindow(IntPtr dialogHandle)
    {
        if (!TryFindFileNameEditWindowHandle(dialogHandle, out var fileNameEditHandle))
        {
            return false;
        }

        return TryFocusFileNameEditWindow(
            dialogHandle,
            fileNameEditHandle,
            NativeMethods.SetForegroundWindow,
            NativeMethods.SetFocus,
            NativeMethods.GetForegroundWindow,
            () => GetFocusedWindowForDialog(dialogHandle),
            GetOwnerWindow,
            () => Thread.Sleep(KeyboardFocusDelay),
            NativeMethods.GetCurrentThreadId,
            handle => NativeMethods.GetWindowThreadProcessId(handle, out _),
            NativeMethods.AttachThreadInput);
    }

    private static bool TryFocusAddressBarEditWindow(IntPtr dialogHandle)
    {
        if (!TryFindAddressBarEditWindowHandle(
                dialogHandle,
                EnumerateChildWindowHandles,
                GetClassName,
                NativeMethods.GetDlgCtrlID,
                out var addressBarEditHandle))
        {
            return false;
        }

        return TryFocusFileNameEditWindow(
            dialogHandle,
            addressBarEditHandle,
            NativeMethods.SetForegroundWindow,
            NativeMethods.SetFocus,
            NativeMethods.GetForegroundWindow,
            () => GetFocusedWindowForDialog(dialogHandle),
            GetOwnerWindow,
            () => Thread.Sleep(KeyboardFocusDelay),
            NativeMethods.GetCurrentThreadId,
            handle => NativeMethods.GetWindowThreadProcessId(handle, out _),
            NativeMethods.AttachThreadInput);
    }

    internal static bool TryFocusFileNameEditWindow(
        IntPtr dialogHandle,
        IntPtr fileNameEditHandle,
        Func<IntPtr, bool> setForegroundWindow,
        Func<IntPtr, IntPtr> setFocus,
        Func<IntPtr> getForegroundWindow,
        Func<IntPtr> getFocusedWindow,
        Func<IntPtr, IntPtr> getOwnerWindow,
        Action waitForFocus,
        Func<uint>? getCurrentThreadId = null,
        Func<IntPtr, uint>? getWindowThreadId = null,
        Func<uint, uint, bool, bool>? attachThreadInput = null)
    {
        ArgumentNullException.ThrowIfNull(setForegroundWindow);
        ArgumentNullException.ThrowIfNull(setFocus);
        ArgumentNullException.ThrowIfNull(getForegroundWindow);
        ArgumentNullException.ThrowIfNull(getFocusedWindow);
        ArgumentNullException.ThrowIfNull(getOwnerWindow);
        ArgumentNullException.ThrowIfNull(waitForFocus);

        if (dialogHandle == IntPtr.Zero || fileNameEditHandle == IntPtr.Zero)
        {
            return false;
        }

        var inputQueuesAttached = false;
        var currentThreadId = getCurrentThreadId?.Invoke() ?? 0;
        var dialogThreadId = getWindowThreadId?.Invoke(dialogHandle) ?? 0;
        if (attachThreadInput is not null &&
            currentThreadId != 0 &&
            dialogThreadId != 0 &&
            currentThreadId != dialogThreadId)
        {
            inputQueuesAttached = attachThreadInput(currentThreadId, dialogThreadId, true);
        }

        try
        {
            var foregroundRequested = setForegroundWindow(dialogHandle);
            waitForFocus();
            if (!foregroundRequested &&
                !IsDialogOrOwnedWindow(getForegroundWindow(), dialogHandle, getOwnerWindow))
            {
                return false;
            }

            if (!IsDialogOrOwnedWindow(getForegroundWindow(), dialogHandle, getOwnerWindow))
            {
                return false;
            }

            _ = setFocus(fileNameEditHandle);
            waitForFocus();
            return getFocusedWindow() == fileNameEditHandle;
        }
        finally
        {
            if (inputQueuesAttached)
            {
                _ = attachThreadInput?.Invoke(currentThreadId, dialogThreadId, false);
            }
        }
    }

    private static bool IsDialogOrOwnedWindow(
        IntPtr windowHandle,
        IntPtr dialogHandle,
        Func<IntPtr, IntPtr> getOwnerWindow)
    {
        var currentHandle = windowHandle;
        for (var depth = 0; depth < 16 && currentHandle != IntPtr.Zero; depth++)
        {
            if (currentHandle == dialogHandle)
            {
                return true;
            }

            currentHandle = getOwnerWindow(currentHandle);
        }

        return false;
    }

    private static IntPtr GetFocusedWindowForDialog(IntPtr dialogHandle)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(dialogHandle, out _);
        if (threadId == 0)
        {
            return IntPtr.Zero;
        }

        var guiThreadInfo = new NativeMethods.GuiThreadInfo
        {
            Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };

        return NativeMethods.GetGUIThreadInfo(threadId, ref guiThreadInfo)
            ? guiThreadInfo.FocusWindow
            : IntPtr.Zero;
    }

    private static bool IsWindowOrParent(
        IntPtr windowHandle,
        IntPtr parentHandle,
        Func<IntPtr, IntPtr> getParentWindow)
    {
        var currentHandle = windowHandle;
        for (var depth = 0; depth < 16 && currentHandle != IntPtr.Zero; depth++)
        {
            if (currentHandle == parentHandle)
            {
                return true;
            }

            currentHandle = getParentWindow(currentHandle);
        }

        return false;
    }

    private static bool TryFindFileNameEditWindowHandle(IntPtr dialogHandle, out IntPtr fileNameEditHandle)
    {
        return TryFindPathEditWindowHandle(
            dialogHandle,
            NativeMethods.GetDlgItem,
            EnumerateChildWindowHandles,
            GetClassName,
            out fileNameEditHandle);
    }

    internal static bool TryFindPathEditWindowHandle(
        IntPtr dialogHandle,
        Func<IntPtr, int, IntPtr> getDlgItem,
        Func<IntPtr, IEnumerable<IntPtr>> childWindowProvider,
        Func<IntPtr, string> classNameProvider,
        out IntPtr pathEditHandle)
    {
        ArgumentNullException.ThrowIfNull(getDlgItem);
        ArgumentNullException.ThrowIfNull(childWindowProvider);
        ArgumentNullException.ThrowIfNull(classNameProvider);

        pathEditHandle = IntPtr.Zero;
        foreach (var controlId in PathEditDialogItemIds)
        {
            var containerHandle = getDlgItem(dialogHandle, controlId);
            if (containerHandle == IntPtr.Zero)
            {
                continue;
            }

            if (IsEditControl(containerHandle, classNameProvider))
            {
                pathEditHandle = containerHandle;
                return true;
            }

            foreach (var childHandle in childWindowProvider(containerHandle))
            {
                if (IsEditControl(childHandle, classNameProvider))
                {
                    pathEditHandle = childHandle;
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool TryFindAddressBarEditWindowHandle(
        IntPtr dialogHandle,
        Func<IntPtr, IEnumerable<IntPtr>> childWindowProvider,
        Func<IntPtr, string> classNameProvider,
        Func<IntPtr, int> controlIdProvider,
        out IntPtr addressBarEditHandle)
    {
        ArgumentNullException.ThrowIfNull(childWindowProvider);
        ArgumentNullException.ThrowIfNull(classNameProvider);
        ArgumentNullException.ThrowIfNull(controlIdProvider);

        addressBarEditHandle = IntPtr.Zero;
        foreach (var childHandle in childWindowProvider(dialogHandle))
        {
            if (controlIdProvider(childHandle) == AddressBarEditControlId &&
                IsEditControl(childHandle, classNameProvider))
            {
                addressBarEditHandle = childHandle;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<IntPtr> EnumerateChildWindowHandles(IntPtr parentHandle)
    {
        var childHandles = new List<IntPtr>();
        NativeMethods.EnumChildWindows(
            parentHandle,
            (childHandle, _) =>
            {
                childHandles.Add(childHandle);
                return true;
            },
            IntPtr.Zero);
        return childHandles;
    }

    private static bool IsEditControl(IntPtr handle, Func<IntPtr, string> classNameProvider)
    {
        return string.Equals(classNameProvider(handle), "Edit", StringComparison.Ordinal);
    }

    private static bool ConfirmDialogStillOpen(IntPtr dialogHandle)
    {
        Thread.Sleep(KeyboardFocusDelay);
        return dialogHandle != IntPtr.Zero && NativeMethods.IsWindow(dialogHandle);
    }

    private static bool TrySendKeyboardText(string text)
    {
        var inputs = new List<NativeMethods.Input>();
        AddVirtualKey(inputs, NativeMethods.VkControl, keyUp: false);
        AddVirtualKey(inputs, NativeMethods.VkA, keyUp: false);
        AddVirtualKey(inputs, NativeMethods.VkA, keyUp: true);
        AddVirtualKey(inputs, NativeMethods.VkControl, keyUp: true);

        foreach (var character in text)
        {
            AddUnicodeKey(inputs, character, keyUp: false);
            AddUnicodeKey(inputs, character, keyUp: true);
        }

        return TrySendKeyboardInputs(inputs);
    }

    private static bool TrySendKeyboardCommit()
    {
        var inputs = new List<NativeMethods.Input>();
        AddVirtualKey(inputs, NativeMethods.VkReturn, keyUp: false);
        AddVirtualKey(inputs, NativeMethods.VkReturn, keyUp: true);
        return TrySendKeyboardInputs(inputs);
    }

    private static bool TrySendAddressBarShortcut()
    {
        var inputs = new List<NativeMethods.Input>();
        AddVirtualKey(inputs, NativeMethods.VkControl, keyUp: false);
        AddVirtualKey(inputs, NativeMethods.VkL, keyUp: false);
        AddVirtualKey(inputs, NativeMethods.VkL, keyUp: true);
        AddVirtualKey(inputs, NativeMethods.VkControl, keyUp: true);
        return TrySendKeyboardInputs(inputs);
    }

    private static void AddVirtualKey(List<NativeMethods.Input> inputs, ushort virtualKey, bool keyUp)
    {
        inputs.Add(new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? NativeMethods.KeyEventFKeyUp : 0
                }
            }
        });
    }

    private static void AddUnicodeKey(List<NativeMethods.Input> inputs, char character, bool keyUp)
    {
        inputs.Add(new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    ScanCode = character,
                    Flags = NativeMethods.KeyEventFUnicode | (keyUp ? NativeMethods.KeyEventFKeyUp : 0)
                }
            }
        });
    }

    private static bool TrySendKeyboardInputs(IReadOnlyList<NativeMethods.Input> inputs)
    {
        if (inputs.Count == 0)
        {
            return true;
        }

        try
        {
            var inputArray = inputs.ToArray();
            var sent = NativeMethods.SendInput(
                (uint)inputArray.Length,
                inputArray,
                Marshal.SizeOf<NativeMethods.Input>());
            return sent == inputArray.Length;
        }
        catch (Exception exception) when (exception is Win32Exception or ExternalException)
        {
            Trace.TraceError(exception.ToString());
            return false;
        }
    }

    private static string GetClassName(IntPtr handle)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(handle, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static IReadOnlyList<string> GetChildControlClassNames(IntPtr dialogHandle)
    {
        var classNames = new List<string>();
        NativeMethods.EnumChildWindows(
            dialogHandle,
            (childHandle, _) =>
            {
                var className = GetClassName(childHandle);
                if (!string.IsNullOrWhiteSpace(className))
                {
                    classNames.Add(className);
                }

                return true;
            },
            IntPtr.Zero);

        return classNames;
    }

    internal static FileDialogControlShape ClassifyFileDialogControls(IEnumerable<string> controlClassNames)
    {
        ArgumentNullException.ThrowIfNull(controlClassNames);

        var controls = controlClassNames
            .Where(control => !string.IsNullOrWhiteSpace(control))
            .ToArray();

        var hasEdit = HasControl(controls, "Edit");
        var hasToolbar = HasControl(controls, "ToolbarWindow32");
        var hasSysListView = HasControl(controls, "SysListView32");
        var hasDirectUi = HasControl(controls, "DirectUIHWND");
        var hasShellDefView = HasControl(controls, "SHELLDLL_DefView");
        var hasSysHeader = HasControl(controls, "SysHeader32");

        if (controls.Any(control => control.Contains("SHBrowseForFolder ShellNameSpace Control", StringComparison.Ordinal)))
        {
            return FileDialogControlShape.BrowseFolder;
        }

        if (hasDirectUi && hasToolbar && hasEdit)
        {
            return FileDialogControlShape.ModernGeneral;
        }

        if ((hasSysListView && hasToolbar && hasEdit) ||
            (hasSysListView && hasShellDefView && hasSysHeader && hasEdit))
        {
            return FileDialogControlShape.LegacySysListView;
        }

        return FileDialogControlShape.Unsupported;
    }

    internal static bool IsAutomatableFileDialogShape(FileDialogControlShape shape)
    {
        return shape is FileDialogControlShape.ModernGeneral
            or FileDialogControlShape.LegacySysListView
            or FileDialogControlShape.Unsupported;
    }

    private static bool HasControl(IEnumerable<string> controls, string classNamePrefix)
    {
        return controls.Any(control => control.StartsWith(classNamePrefix, StringComparison.Ordinal));
    }

    private static bool LooksLikeAutomatableFileDialog(AutomationElement activeDialog)
    {
        return TryFindFileNameEdit(activeDialog, out _) &&
            TryFindCommitButton(activeDialog, out _);
    }

    private static uint? GetProcessId(IntPtr handle)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        return processId == 0 ? null : processId;
    }

    private static string? GetProcessName(IntPtr handle)
    {
        var processId = GetProcessId(handle);
        if (processId is null)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId.Value);
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
        Func<IEnumerable<IntPtr>> topLevelWindowProvider,
        Func<IntPtr, uint?>? processIdProvider = null,
        Func<IntPtr, IEnumerable<string>>? childControlClassNamesProvider = null)
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
        var foregroundProcessId = processIdProvider?.Invoke(foregroundHandle);
        var listaryIsForeground = IsListaryOpenWindow(foregroundClassName, foregroundProcessName);
        var foregroundBrowserOwnedDialogs = !listaryIsForeground && IsKnownBrowserProcessName(foregroundProcessName)
            ? new List<IntPtr>()
            : null;
        var visibleDialogsWhenListaryIsForeground = listaryIsForeground
            ? new List<IntPtr>()
            : null;
        foreach (var candidateHandle in topLevelWindowProvider())
        {
            if (candidateHandle == IntPtr.Zero || candidateHandle == foregroundHandle)
            {
                continue;
            }

            var candidateClassName = classNameProvider(candidateHandle);
            if (!IsSupportedFileDialogClass(candidateClassName) ||
                !LooksLikeFileDialogByChildControls(candidateHandle, candidateClassName, childControlClassNamesProvider))
            {
                continue;
            }

            var candidateProcessName = processNameProvider(candidateHandle);
            var candidateProcessId = processIdProvider?.Invoke(candidateHandle);
            if (ProcessesEqual(candidateProcessId, foregroundProcessId, candidateProcessName, foregroundProcessName) &&
                ownerWindowProvider(candidateHandle) == foregroundHandle)
            {
                return candidateHandle;
            }

            if (foregroundBrowserOwnedDialogs is not null &&
                ProcessesEqual(candidateProcessId, foregroundProcessId, candidateProcessName, foregroundProcessName) &&
                IsBrowserOwnedDialog(
                    candidateHandle,
                    candidateProcessName,
                    candidateProcessId,
                    ownerWindowProvider,
                    processNameProvider,
                    processIdProvider))
            {
                foregroundBrowserOwnedDialogs.Add(candidateHandle);
            }

            if (visibleDialogsWhenListaryIsForeground is not null &&
                IsApplicationOwnedDialog(
                    candidateHandle,
                    candidateProcessName,
                    candidateProcessId,
                    ownerWindowProvider,
                    processNameProvider,
                    processIdProvider))
            {
                visibleDialogsWhenListaryIsForeground.Add(candidateHandle);
            }
        }

        if (foregroundBrowserOwnedDialogs?.Count == 1)
        {
            return foregroundBrowserOwnedDialogs[0];
        }

        if (visibleDialogsWhenListaryIsForeground?.Count == 1)
        {
            return visibleDialogsWhenListaryIsForeground[0];
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
        uint? candidateProcessId,
        Func<IntPtr, IntPtr> ownerWindowProvider,
        Func<IntPtr, string?> processNameProvider,
        Func<IntPtr, uint?>? processIdProvider)
    {
        return IsKnownBrowserProcessName(candidateProcessName) &&
            IsApplicationOwnedDialog(
                candidateHandle,
                candidateProcessName,
                candidateProcessId,
                ownerWindowProvider,
                processNameProvider,
                processIdProvider);
    }

    private static bool IsApplicationOwnedDialog(
        IntPtr candidateHandle,
        string? candidateProcessName,
        uint? candidateProcessId,
        Func<IntPtr, IntPtr> ownerWindowProvider,
        Func<IntPtr, string?> processNameProvider,
        Func<IntPtr, uint?>? processIdProvider)
    {
        var ownerHandle = ownerWindowProvider(candidateHandle);
        if (ownerHandle == IntPtr.Zero)
        {
            return false;
        }

        var ownerProcessName = processNameProvider(ownerHandle);
        var ownerProcessId = processIdProvider?.Invoke(ownerHandle);
        return ProcessesEqual(candidateProcessId, ownerProcessId, candidateProcessName, ownerProcessName);
    }

    private static bool LooksLikeFileDialogByChildControls(
        IntPtr candidateHandle,
        string? candidateClassName,
        Func<IntPtr, IEnumerable<string>>? childControlClassNamesProvider)
    {
        if (childControlClassNamesProvider is null ||
            !string.Equals(candidateClassName, StandardDialogClassName, StringComparison.Ordinal))
        {
            return true;
        }

        return ClassifyFileDialogControls(childControlClassNamesProvider(candidateHandle)) is
            FileDialogControlShape.ModernGeneral or
            FileDialogControlShape.LegacySysListView;
    }

    private static bool ProcessesEqual(
        uint? leftProcessId,
        uint? rightProcessId,
        string? leftProcessName,
        string? rightProcessName)
    {
        if (leftProcessId.HasValue || rightProcessId.HasValue)
        {
            return leftProcessId.HasValue &&
                rightProcessId.HasValue &&
                leftProcessId.Value == rightProcessId.Value;
        }

        return ProcessNamesEqual(leftProcessName, rightProcessName);
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
        return TryFindFirstElement(
            activeDialog,
            Condition.TrueCondition,
            IsFileNameElement,
            out fileNameEdit);
    }

    private static bool TryFindCommitButton(
        AutomationElement activeDialog,
        [NotNullWhen(true)] out AutomationElement? commitButton)
    {
        return TryFindFirstElement(
            activeDialog,
            Condition.TrueCondition,
            IsCommitButtonElement,
            out commitButton);
    }

    private static bool TryFindFirstElement(
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
            if (candidatePredicate(element))
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

        if (string.Equals(automationId, FileNameAutomationId, StringComparison.Ordinal) ||
            string.Equals(automationId, FolderNameAutomationId, StringComparison.Ordinal))
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
                name.Contains("Folder", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("文件夹", StringComparison.OrdinalIgnoreCase) ||
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

internal enum FileDialogControlShape
{
    Unsupported,
    ModernGeneral,
    LegacySysListView,
    BrowseFolder
}
