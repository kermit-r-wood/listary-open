using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Windows;

internal static partial class NativeMethods
{
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    internal const uint GW_OWNER = 4;
    internal const uint GA_ROOT = 2;
    internal const int InputKeyboard = 1;
    internal const uint KeyEventFKeyUp = 0x0002;
    internal const uint KeyEventFUnicode = 0x0004;
    internal const ushort VkReturn = 0x0D;
    internal const ushort VkA = 0x41;
    internal const ushort VkControl = 0x11;
    internal const ushort VkL = 0x4C;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public int Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HardwareInput
    {
        public uint Message;
        public ushort LowParameter;
        public ushort HighParameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public Rect CaretRectangle;
    }

    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const ushort ImageFileMachineUnknown = 0x0000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("user32.dll")]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo guiThreadInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassName(IntPtr hWnd, char[] className, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    internal static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial int GetWindowText(IntPtr hWnd, char[] text, int maxCount);

    [LibraryImport("user32.dll")]
    internal static partial int GetDlgCtrlID(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        UIntPtr wParam,
        [Out] char[] lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
