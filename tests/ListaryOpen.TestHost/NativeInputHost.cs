using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ListaryOpen.TestHost;

internal static class NativeInputHost
{
    private const uint ChildStyle = 0x40000000;
    private const uint VisibleStyle = 0x10000000;
    private const uint TabStopStyle = 0x00010000;
    private const uint OverlappedWindowStyle = 0x00CF0000;
    private const int UseDefault = unchecked((int)0x80000000);
    private const int ShowNormal = 1;
    private const uint WmDestroy = 0x0002;
    private static readonly WindowProcedure WindowProc = OnWindowMessage;

    public static int Run(
        string topLevelClass,
        string focusedClass,
        int focusedControlId,
        string title,
        Action<IntPtr, IntPtr> onReady)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topLevelClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(focusedClass);
        ArgumentNullException.ThrowIfNull(onReady);

        var module = GetModuleHandle(null);
        RegisterWindowClass(topLevelClass, module);
        if (!IsSystemWindowClass(focusedClass))
        {
            RegisterWindowClass(focusedClass, module);
        }

        var window = CreateWindowEx(
            0,
            topLevelClass,
            title,
            OverlappedWindowStyle | VisibleStyle,
            UseDefault,
            UseDefault,
            720,
            480,
            IntPtr.Zero,
            IntPtr.Zero,
            module,
            IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the integration host window.");
        }

        var focused = CreateWindowEx(
            0,
            focusedClass,
            string.Empty,
            ChildStyle | VisibleStyle | TabStopStyle,
            24,
            24,
            640,
            360,
            window,
            new IntPtr(focusedControlId),
            module,
            IntPtr.Zero);
        if (focused == IntPtr.Zero)
        {
            DestroyWindow(window);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the focused integration control.");
        }

        _ = ShowWindow(window, ShowNormal);
        _ = UpdateWindow(window);
        _ = SetForegroundWindow(window);
        _ = SetFocus(focused);
        onReady(window, focused);

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }

        return 0;
    }

    private static bool IsSystemWindowClass(string className) =>
        string.Equals(className, "Edit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "Static", StringComparison.OrdinalIgnoreCase);

    private static void RegisterWindowClass(string className, IntPtr module)
    {
        var windowClass = new WindowClass
        {
            WindowProcedure = WindowProc,
            Instance = module,
            ClassName = className
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            const int classAlreadyExists = 1410;
            var error = Marshal.GetLastWin32Error();
            if (error != classAlreadyExists)
            {
                throw new Win32Exception(error, $"Could not register integration window class '{className}'.");
            }
        }
    }

    private static IntPtr OnWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProcedure WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);
}
