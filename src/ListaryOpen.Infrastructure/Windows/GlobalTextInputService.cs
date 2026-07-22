using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ListaryOpen.Infrastructure.Windows;

public enum GlobalTextInputHost
{
    Explorer,
    TaskManager,
    Dialog
}

public sealed class GlobalTextInputEventArgs : EventArgs
{
    public GlobalTextInputEventArgs(
        string text,
        IntPtr foregroundWindow,
        string focusedControlClass,
        GlobalTextInputHost host = GlobalTextInputHost.Explorer)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        Text = text;
        ForegroundWindow = foregroundWindow;
        FocusedControlClass = focusedControlClass ?? string.Empty;
        Host = host;
    }

    public string Text { get; }

    public IntPtr ForegroundWindow { get; }

    public string FocusedControlClass { get; }

    public GlobalTextInputHost Host { get; }

    public bool Handled { get; set; }
}

public sealed class GlobalEscapeInputEventArgs : EventArgs
{
    public GlobalEscapeInputEventArgs(IntPtr foregroundWindow, string focusedControlClass)
    {
        ForegroundWindow = foregroundWindow;
        FocusedControlClass = focusedControlClass ?? string.Empty;
    }

    public IntPtr ForegroundWindow { get; }

    public string FocusedControlClass { get; }
}

public enum GlobalPointerButton
{
    Left,
    Right,
    Middle,
    Other
}

public sealed class GlobalPointerInputEventArgs : EventArgs
{
    public GlobalPointerInputEventArgs(
        int screenX,
        int screenY,
        GlobalPointerButton button = GlobalPointerButton.Other)
    {
        ScreenX = screenX;
        ScreenY = screenY;
        Button = button;
    }

    public int ScreenX { get; }

    public int ScreenY { get; }

    public GlobalPointerButton Button { get; }
}

public enum ExplorerMenuGesture
{
    MiddleClick,
    LeftDoubleClick
}

public sealed class ExplorerMenuGestureInputEventArgs : EventArgs
{
    public ExplorerMenuGestureInputEventArgs(ExplorerMenuGesture gesture, int screenX, int screenY)
    {
        Gesture = gesture;
        ScreenX = screenX;
        ScreenY = screenY;
    }

    public ExplorerMenuGesture Gesture { get; }
    public int ScreenX { get; }
    public int ScreenY { get; }
}

public sealed class GlobalResultShortcutInputEventArgs : EventArgs
{
    public GlobalResultShortcutInputEventArgs(int resultIndex, IntPtr foregroundWindow, string focusedControlClass)
    {
        if (resultIndex is < 0 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(resultIndex));
        }

        ResultIndex = resultIndex;
        ForegroundWindow = foregroundWindow;
        FocusedControlClass = focusedControlClass ?? string.Empty;
    }

    public int ResultIndex { get; }
    public IntPtr ForegroundWindow { get; }
    public string FocusedControlClass { get; }
    public bool Handled { get; set; }
}

public sealed class GlobalNavigationInputEventArgs : EventArgs
{
    public GlobalNavigationInputEventArgs(int selectionDelta, IntPtr foregroundWindow, string focusedControlClass)
    {
        if (selectionDelta is not (-1 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(selectionDelta));
        }

        SelectionDelta = selectionDelta;
        ForegroundWindow = foregroundWindow;
        FocusedControlClass = focusedControlClass ?? string.Empty;
    }

    public int SelectionDelta { get; }

    public IntPtr ForegroundWindow { get; }

    public string FocusedControlClass { get; }

    public bool Handled { get; set; }
}

public sealed class GlobalConfirmInputEventArgs : EventArgs
{
    public GlobalConfirmInputEventArgs(IntPtr foregroundWindow, string focusedControlClass)
    {
        ForegroundWindow = foregroundWindow;
        FocusedControlClass = focusedControlClass ?? string.Empty;
    }

    public IntPtr ForegroundWindow { get; }

    public string FocusedControlClass { get; }

    public bool Handled { get; set; }
}

public enum GlobalEditCommand
{
    SelectAll,
    Copy,
    Cut,
    Backspace
}

public sealed class GlobalEditCommandInputEventArgs : EventArgs
{
    public GlobalEditCommandInputEventArgs(
        GlobalEditCommand command,
        IntPtr foregroundWindow,
        string focusedControlClass)
    {
        Command = command;
        ForegroundWindow = foregroundWindow;
        FocusedControlClass = focusedControlClass ?? string.Empty;
    }

    public GlobalEditCommand Command { get; }
    public IntPtr ForegroundWindow { get; }
    public string FocusedControlClass { get; }
    public bool Handled { get; set; }
}

public sealed class GlobalTextInputService : IDisposable
{
    private const int WhKeyboardLowLevel = 13;
    private const int WhMouseLowLevel = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSystemKeyDown = 0x0104;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const uint LlkhfLowerIlInjected = 0x00000002;
    private const uint LlkhfInjected = 0x00000010;
    private const uint LlmhfLowerIlInjected = 0x00000002;
    private const uint LlmhfInjected = 0x00000001;
    private const int VkControl = 0x11;
    private const int VkShift = 0x10;
    private const int VkEscape = 0x1B;
    private const int VkReturn = 0x0D;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkMenu = 0x12;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const uint ToUnicodeDoNotChangeKeyboardState = 0x0004;
    private const int FileNameEditControlId = 1148;
    private const int FolderNameEditControlId = 1152;

    private readonly LowLevelKeyboardProcedure _hookProcedure;
    private readonly LowLevelMouseProcedure _mouseHookProcedure;
    private int _acceptInjectedInputForTesting;
    private IntPtr _hookHandle;
    private IntPtr _mouseHookHandle;
    private bool _disposed;
    private int _captureOverlayInput;
    private int _captureExplorerMenuInput;
    private IntPtr _dialogInputWindow;
    private uint _lastLeftButtonTime;
    private NativePoint _lastLeftButtonPoint;

    public GlobalTextInputService()
        : this(acceptInjectedInputForTesting: false)
    {
    }

    internal GlobalTextInputService(bool acceptInjectedInputForTesting)
    {
        AcceptInjectedInputForTesting = acceptInjectedInputForTesting;
        _hookProcedure = OnKeyboardInput;
        _mouseHookProcedure = OnMouseInput;
    }

    internal bool AcceptInjectedInputForTesting
    {
        get => Volatile.Read(ref _acceptInjectedInputForTesting) != 0;
        set => Interlocked.Exchange(ref _acceptInjectedInputForTesting, value ? 1 : 0);
    }

    public event EventHandler<GlobalTextInputEventArgs>? TextInput;

    public event EventHandler<GlobalEscapeInputEventArgs>? EscapePressed;

    public event EventHandler<GlobalPointerInputEventArgs>? PointerPressed;

    public event EventHandler<GlobalNavigationInputEventArgs>? NavigationPressed;

    public event EventHandler<GlobalConfirmInputEventArgs>? ConfirmPressed;

    public event EventHandler<GlobalEditCommandInputEventArgs>? EditCommandPressed;

    public event EventHandler<GlobalResultShortcutInputEventArgs>? ResultShortcutPressed;

    public event EventHandler<ExplorerMenuGestureInputEventArgs>? ExplorerMenuGesturePressed;

    /// <summary>
    /// Enables the pointer, escape, and navigation paths only while the Explorer overlay is active.
    /// Keeping this flag in the hook service avoids dispatching every system click and arrow key to WPF.
    /// </summary>
    public bool CaptureOverlayInput
    {
        get => Volatile.Read(ref _captureOverlayInput) != 0;
        set
        {
            var enabled = value ? 1 : 0;
            if (Interlocked.Exchange(ref _captureOverlayInput, enabled) != enabled)
            {
                UpdateMouseHook(CaptureOverlayInput || CaptureExplorerMenuInput);
            }
        }
    }

    public bool CaptureExplorerMenuInput
    {
        get => Volatile.Read(ref _captureExplorerMenuInput) != 0;
        set
        {
            var enabled = value ? 1 : 0;
            if (Interlocked.Exchange(ref _captureExplorerMenuInput, enabled) != enabled)
            {
                UpdateMouseHook(CaptureOverlayInput || CaptureExplorerMenuInput);
            }
        }
    }

    /// <summary>
    /// Identifies the currently attached file dialog whose non-edit keystrokes
    /// should reopen the collapsed Quick Switch bar.
    /// </summary>
    public IntPtr DialogInputWindow
    {
        get => Interlocked.CompareExchange(ref _dialogInputWindow, IntPtr.Zero, IntPtr.Zero);
        set => Interlocked.Exchange(ref _dialogInputWindow, value);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _hookHandle = SetWindowsHookEx(
            WhKeyboardLowLevel,
            _hookProcedure,
            GetModuleHandle(null),
            0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the global text input hook.");
        }

        if (CaptureOverlayInput || CaptureExplorerMenuInput)
        {
            UpdateMouseHook(enabled: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_hookHandle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        if (_mouseHookHandle != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }

        _disposed = true;
    }

    private void UpdateMouseHook(bool enabled)
    {
        if (_disposed || _hookHandle == IntPtr.Zero)
        {
            return;
        }

        if (!enabled)
        {
            if (_mouseHookHandle != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }

            return;
        }

        if (_mouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        _mouseHookHandle = SetWindowsMouseHookEx(
            WhMouseLowLevel,
            _mouseHookProcedure,
            GetModuleHandle(null),
            0);
        if (_mouseHookHandle == IntPtr.Zero)
        {
            Trace.TraceWarning(
                "Could not install the Explorer overlay pointer hook: {0}",
                new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }

    public static bool IsTextEntryControlClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return className.Contains("Edit", StringComparison.OrdinalIgnoreCase)
            || className.Contains("TextBox", StringComparison.OrdinalIgnoreCase)
            || className.Contains("SearchBox", StringComparison.OrdinalIgnoreCase);
    }

    private IntPtr OnKeyboardInput(int code, IntPtr message, IntPtr dataPointer)
    {
        try
        {
            if (code >= 0 && IsKeyDownMessage(message))
            {
                var data = Marshal.PtrToStructure<LowLevelKeyboardInput>(dataPointer);
                if (AcceptInjectedInputForTesting ||
                    (data.Flags & (LlkhfInjected | LlkhfLowerIlInjected)) == 0)
                {
                    var shortcutIndex = GetOverlayResultShortcutIndex(
                        data.VirtualKey,
                        IsKeyDown(VkControl),
                        IsKeyDown(VkShift),
                        IsKeyDown(VkMenu),
                        IsKeyDown(VkLwin) || IsKeyDown(VkRwin));
                    if (CaptureOverlayInput && shortcutIndex >= 0)
                    {
                        if (RaiseResultShortcutPressed(shortcutIndex))
                        {
                            return new IntPtr(1);
                        }
                    }

                    else if (CaptureOverlayInput && !HasCommandModifier() && data.VirtualKey == VkReturn)
                    {
                        if (RaiseConfirmPressed())
                        {
                            return new IntPtr(1);
                        }
                    }

                    var editCommand = GetOverlayEditCommand(
                        data.VirtualKey,
                        IsKeyDown(VkControl),
                        IsKeyDown(VkShift),
                        IsKeyDown(VkMenu),
                        IsKeyDown(VkLwin) || IsKeyDown(VkRwin));
                    if (CaptureOverlayInput && shortcutIndex < 0 && editCommand is not null)
                    {
                        if (RaiseEditCommandPressed(editCommand.Value))
                        {
                            return new IntPtr(1);
                        }
                    }
                    else if (!HasCommandModifier() && CaptureOverlayInput && data.VirtualKey is VkUp or VkDown)
                    {
                        if (RaiseNavigationPressed(data.VirtualKey == VkUp ? -1 : 1))
                        {
                            return new IntPtr(1);
                        }
                    }
                    else if (ShouldCaptureOverlayEscape(
                        data.VirtualKey,
                        CaptureOverlayInput,
                        HasCommandModifier()))
                    {
                        RaiseEscapePressed();
                        return new IntPtr(1);
                    }
                    else if (!HasCommandModifier()
                        && TryGetSupportedInputContext(
                            out var foregroundWindow,
                            out var foregroundThreadId,
                            out var focusedControlClass,
                            out var focusedControlId,
                            out var host)
                        && ShouldCaptureTextInput(host, focusedControlClass, focusedControlId)
                        && TryTranslateText(
                            data.VirtualKey,
                            data.ScanCode,
                            foregroundThreadId,
                            CaptureOverlayInput,
                            out var text))
                    {
                        if (RaiseTextInput(text, foregroundWindow, focusedControlClass, host))
                        {
                            return new IntPtr(1);
                        }
                    }
                }
            }
        }
        catch
        {
            // A global hook must always return control to Windows, even if translation or a subscriber fails.
        }

        return CallNextHookEx(_hookHandle, code, message, dataPointer);
    }

    private IntPtr OnMouseInput(int code, IntPtr message, IntPtr dataPointer)
    {
        try
        {
            if ((CaptureOverlayInput || CaptureExplorerMenuInput) && code >= 0 && IsPointerDownMessage(message))
            {
                var data = Marshal.PtrToStructure<LowLevelMouseInput>(dataPointer);
                if (AcceptInjectedInputForTesting ||
                    (data.Flags & (LlmhfInjected | LlmhfLowerIlInjected)) == 0)
                {
                    if (CaptureOverlayInput)
                    {
                        PointerPressed?.Invoke(
                            this,
                            new GlobalPointerInputEventArgs(
                                data.Point.X,
                                data.Point.Y,
                                GetPointerButton(message)));
                    }

                    if (CaptureExplorerMenuInput && TryGetExplorerMenuGesture(message, data, out var gesture))
                    {
                        ExplorerMenuGesturePressed?.Invoke(
                            this,
                            new ExplorerMenuGestureInputEventArgs(gesture, data.Point.X, data.Point.Y));
                    }
                }
            }
        }
        catch
        {
            // A global hook must always return control to Windows, even if a subscriber fails.
        }

        return CallNextHookEx(_mouseHookHandle, code, message, dataPointer);
    }

    private bool RaiseTextInput(
        string text,
        IntPtr foregroundWindow,
        string focusedControlClass,
        GlobalTextInputHost host)
    {
        var input = new GlobalTextInputEventArgs(text, foregroundWindow, focusedControlClass, host);
        TextInput?.Invoke(this, input);
        return input.Handled;
    }

    private void RaiseEscapePressed()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow != IntPtr.Zero)
        {
            EscapePressed?.Invoke(
                this,
                new GlobalEscapeInputEventArgs(
                    foregroundWindow,
                    GetFocusedControlClass(foregroundWindow)));
        }
    }

    private bool RaiseNavigationPressed(int selectionDelta)
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        var input = new GlobalNavigationInputEventArgs(
            selectionDelta,
            foregroundWindow,
            GetFocusedControlClass(foregroundWindow));
        NavigationPressed?.Invoke(this, input);
        return input.Handled;
    }

    private bool RaiseConfirmPressed()
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        var input = new GlobalConfirmInputEventArgs(
            foregroundWindow,
            GetFocusedControlClass(foregroundWindow));
        ConfirmPressed?.Invoke(this, input);
        return input.Handled;
    }

    private bool RaiseEditCommandPressed(GlobalEditCommand command)
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        var input = new GlobalEditCommandInputEventArgs(
            command,
            foregroundWindow,
            GetFocusedControlClass(foregroundWindow));
        EditCommandPressed?.Invoke(this, input);
        return input.Handled;
    }

    private bool RaiseResultShortcutPressed(int resultIndex)
    {
        var foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        var input = new GlobalResultShortcutInputEventArgs(
            resultIndex,
            foregroundWindow,
            GetFocusedControlClass(foregroundWindow));
        ResultShortcutPressed?.Invoke(this, input);
        return input.Handled;
    }

    internal static int GetOverlayResultShortcutIndex(
        uint virtualKey,
        bool controlDown,
        bool shiftDown,
        bool altDown,
        bool windowsDown)
    {
        if (!controlDown || shiftDown || altDown || windowsDown)
        {
            return -1;
        }

        return virtualKey switch
        {
            >= 0x31 and <= 0x39 => (int)(virtualKey - 0x31),
            >= 0x61 and <= 0x69 => (int)(virtualKey - 0x61),
            _ => -1
        };
    }

    private bool TryGetExplorerMenuGesture(
        IntPtr message,
        LowLevelMouseInput input,
        out ExplorerMenuGesture gesture)
    {
        if (message.ToInt64() == WmMiddleButtonDown)
        {
            gesture = ExplorerMenuGesture.MiddleClick;
            return true;
        }

        gesture = ExplorerMenuGesture.LeftDoubleClick;
        if (message.ToInt64() != WmLeftButtonDown)
        {
            return false;
        }

        var withinTime = _lastLeftButtonTime != 0 &&
            unchecked(input.Time - _lastLeftButtonTime) <= GetDoubleClickTime();
        var withinDistance = Math.Abs(input.Point.X - _lastLeftButtonPoint.X) <= GetSystemMetrics(36) &&
            Math.Abs(input.Point.Y - _lastLeftButtonPoint.Y) <= GetSystemMetrics(37);
        _lastLeftButtonTime = input.Time;
        _lastLeftButtonPoint = input.Point;
        if (!withinTime || !withinDistance)
        {
            return false;
        }

        _lastLeftButtonTime = 0;
        return true;
    }

    internal static GlobalEditCommand? GetOverlayEditCommand(
        uint virtualKey,
        bool controlDown,
        bool shiftDown,
        bool altDown,
        bool windowsDown)
    {
        if (!controlDown && !shiftDown && !altDown && !windowsDown && virtualKey == 0x08)
        {
            return GlobalEditCommand.Backspace;
        }

        if (!controlDown || shiftDown || altDown || windowsDown)
        {
            return null;
        }

        return virtualKey switch
        {
            0x41 => GlobalEditCommand.SelectAll,
            0x43 => GlobalEditCommand.Copy,
            0x58 => GlobalEditCommand.Cut,
            _ => null
        };
    }

    internal static bool ShouldCaptureOverlayEscape(
        uint virtualKey,
        bool captureOverlayInput,
        bool hasCommandModifier) =>
        captureOverlayInput && !hasCommandModifier && virtualKey == VkEscape;

    private static bool IsKeyDownMessage(IntPtr message)
    {
        var value = message.ToInt64();
        return value is WmKeyDown or WmSystemKeyDown;
    }

    private static bool IsPointerDownMessage(IntPtr message)
    {
        var value = message.ToInt64();
        return value is WmLeftButtonDown or WmRightButtonDown or WmMiddleButtonDown or WmXButtonDown;
    }

    private static GlobalPointerButton GetPointerButton(IntPtr message) => message.ToInt64() switch
    {
        WmLeftButtonDown => GlobalPointerButton.Left,
        WmRightButtonDown => GlobalPointerButton.Right,
        WmMiddleButtonDown => GlobalPointerButton.Middle,
        _ => GlobalPointerButton.Other
    };

    private static bool HasCommandModifier()
    {
        return IsKeyDown(VkControl)
            || IsKeyDown(VkMenu)
            || IsKeyDown(VkLwin)
            || IsKeyDown(VkRwin);
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool TryTranslateText(
        uint virtualKey,
        uint scanCode,
        uint foregroundThreadId,
        bool allowWhitespace,
        out string text)
    {
        text = string.Empty;
        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState) || virtualKey >= keyboardState.Length)
        {
            return false;
        }

        keyboardState[virtualKey] |= 0x80;
        var buffer = new StringBuilder(8);
        var characterCount = ToUnicodeEx(
            virtualKey,
            scanCode,
            keyboardState,
            buffer,
            buffer.Capacity,
            ToUnicodeDoNotChangeKeyboardState,
            GetKeyboardLayout(foregroundThreadId));
        if (characterCount <= 0)
        {
            return false;
        }

        text = buffer.ToString(0, Math.Min(characterCount, buffer.Length));
        return IsSupportedTranslatedText(text, allowWhitespace);
    }

    internal static bool IsSupportedTranslatedText(string text, bool allowWhitespace) =>
        !string.IsNullOrEmpty(text) &&
        text.Any(character =>
            !char.IsControl(character) &&
            (allowWhitespace || !char.IsWhiteSpace(character)));

    private bool TryGetSupportedInputContext(
        out IntPtr foregroundWindow,
        out uint foregroundThreadId,
        out string focusedControlClass,
        out int focusedControlId,
        out GlobalTextInputHost host)
    {
        foregroundWindow = NativeMethods.GetForegroundWindow();
        foregroundThreadId = 0;
        focusedControlClass = string.Empty;
        focusedControlId = 0;
        host = GlobalTextInputHost.Explorer;
        if (foregroundWindow == IntPtr.Zero || !TryGetSupportedHost(foregroundWindow, out host))
        {
            return false;
        }

        foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        if (foregroundThreadId == 0)
        {
            return false;
        }

        focusedControlClass = GetFocusedControlClass(
            foregroundWindow,
            foregroundThreadId,
            out focusedControlId);
        return true;
    }

    internal static bool ShouldCaptureTextInput(
        GlobalTextInputHost host,
        string? focusedControlClass,
        int focusedControlId)
    {
        if (host == GlobalTextInputHost.Dialog &&
            focusedControlId is FileNameEditControlId or FolderNameEditControlId &&
            string.Equals(focusedControlClass, "Edit", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !IsTextEntryControlClass(focusedControlClass);
    }

    private bool TryGetSupportedHost(IntPtr window, out GlobalTextInputHost host)
    {
        host = GlobalTextInputHost.Explorer;
        if (IsConfiguredDialogInputWindow(window, DialogInputWindow))
        {
            host = GlobalTextInputHost.Dialog;
            return true;
        }

        var buffer = new char[64];
        var length = NativeMethods.GetClassName(window, buffer, buffer.Length);
        if (length <= 0)
        {
            return false;
        }

        var className = new string(buffer, 0, length);
        if (IsExplorerTopLevelWindowClass(className))
        {
            return true;
        }

        if (IsTaskManagerTopLevelWindowClass(className) && IsTaskManagerProcess(window))
        {
            host = GlobalTextInputHost.TaskManager;
            return true;
        }

        return false;
    }

    public static bool IsConfiguredDialogInputWindow(IntPtr window, IntPtr configuredDialogWindow) =>
        IsConfiguredDialogInputWindow(
            window,
            configuredDialogWindow,
            handle => NativeMethods.GetAncestor(handle, NativeMethods.GA_ROOT),
            handle => NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER));

    internal static bool IsConfiguredDialogInputWindow(
        IntPtr window,
        IntPtr configuredDialogWindow,
        Func<IntPtr, IntPtr> getRootWindow,
        Func<IntPtr, IntPtr> getOwnerWindow)
    {
        ArgumentNullException.ThrowIfNull(getRootWindow);
        ArgumentNullException.ThrowIfNull(getOwnerWindow);
        if (window == IntPtr.Zero || configuredDialogWindow == IntPtr.Zero)
        {
            return false;
        }

        if (window == configuredDialogWindow)
        {
            return true;
        }

        var windowRoot = getRootWindow(window);
        var configuredRoot = getRootWindow(configuredDialogWindow);
        if (windowRoot == configuredDialogWindow ||
            configuredRoot == window ||
            (windowRoot != IntPtr.Zero && windowRoot == configuredRoot))
        {
            return true;
        }

        var owner = getOwnerWindow(windowRoot != IntPtr.Zero ? windowRoot : window);
        for (var depth = 0; owner != IntPtr.Zero && depth < 8; depth++)
        {
            if (owner == configuredDialogWindow || owner == configuredRoot)
            {
                return true;
            }

            owner = getOwnerWindow(owner);
        }

        return false;
    }

    internal static bool IsExplorerTopLevelWindowClass(string? className) =>
        string.Equals(className, "CabinetWClass", StringComparison.Ordinal)
        || string.Equals(className, "ExploreWClass", StringComparison.Ordinal);

    internal static bool IsTaskManagerTopLevelWindowClass(string? className) =>
        string.Equals(className, "TaskManagerWindow", StringComparison.Ordinal);

    internal static bool IsTaskManagerProcessName(string? processName) =>
        string.Equals(processName, "Taskmgr", StringComparison.OrdinalIgnoreCase)
        || string.Equals(processName, "Taskmgr.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsTaskManagerProcess(IntPtr window)
    {
        try
        {
            _ = NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById(unchecked((int)processId));
            return IsTaskManagerProcessName(process.ProcessName);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static string GetFocusedControlClass(IntPtr foregroundWindow)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
        if (threadId == 0)
        {
            return string.Empty;
        }

        return GetFocusedControlClass(foregroundWindow, threadId);
    }

    private static string GetFocusedControlClass(IntPtr foregroundWindow, uint threadId)
    {
        return GetFocusedControlClass(foregroundWindow, threadId, out _);
    }

    private static string GetFocusedControlClass(
        IntPtr foregroundWindow,
        uint threadId,
        out int focusedControlId)
    {
        focusedControlId = 0;
        var threadInfo = new NativeMethods.GuiThreadInfo
        {
            Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>()
        };
        if (!NativeMethods.GetGUIThreadInfo(threadId, ref threadInfo))
        {
            return string.Empty;
        }

        var focusedWindow = threadInfo.FocusWindow != IntPtr.Zero
            ? threadInfo.FocusWindow
            : threadInfo.ActiveWindow;
        if (focusedWindow == IntPtr.Zero)
        {
            return string.Empty;
        }

        focusedControlId = NativeMethods.GetDlgCtrlID(focusedWindow);
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(focusedWindow, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardInput
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelMouseInput
    {
        public readonly NativePoint Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly UIntPtr ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelKeyboardProcedure(int code, IntPtr message, IntPtr dataPointer);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelMouseProcedure(int code, IntPtr message, IntPtr dataPointer);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProcedure hookProcedure,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsMouseHookEx(
        int hookId,
        LowLevelMouseProcedure hookProcedure,
        IntPtr moduleHandle,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hookHandle,
        int code,
        IntPtr message,
        IntPtr dataPointer);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] keyboardState);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint virtualKey,
        uint scanCode,
        byte[] keyboardState,
        [Out] StringBuilder buffer,
        int bufferSize,
        uint flags,
        IntPtr keyboardLayout);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
