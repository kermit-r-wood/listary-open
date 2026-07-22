using System.Runtime.InteropServices;

namespace ListaryOpen.App;

internal static class VisibleWindowBounds
{
    private const int DwmExtendedFrameBounds = 9;
    private const uint MonitorDefaultToNearest = 2;

    internal static bool TryGet(IntPtr windowHandle, out NativeRectangle rectangle)
    {
        rectangle = default;
        if (windowHandle == IntPtr.Zero || !GetWindowRect(windowHandle, out var fallbackRectangle))
        {
            return false;
        }

        NativeRectangle visibleRectangle;
        int dwmResult;
        try
        {
            dwmResult = DwmGetWindowAttribute(
                windowHandle,
                DwmExtendedFrameBounds,
                out visibleRectangle,
                Marshal.SizeOf<NativeRectangle>());
        }
        catch (DllNotFoundException)
        {
            dwmResult = -1;
            visibleRectangle = default;
        }
        catch (EntryPointNotFoundException)
        {
            dwmResult = -1;
            visibleRectangle = default;
        }

        rectangle = SelectBest(fallbackRectangle, dwmResult, visibleRectangle);
        return true;
    }

    internal static bool IsAlive(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero && IsWindow(windowHandle);

    internal static bool IsAvailable(IntPtr windowHandle) =>
        IsAlive(windowHandle) && IsWindowVisible(windowHandle);

    internal static NativeRectangle SelectBest(
        NativeRectangle fallbackRectangle,
        int dwmResult,
        NativeRectangle visibleRectangle) =>
        dwmResult == 0 && visibleRectangle.HasArea
            ? visibleRectangle
            : fallbackRectangle;

    internal static bool TryGetMonitorWorkArea(IntPtr windowHandle, out NativeRectangle workArea)
    {
        workArea = default;
        if (windowHandle == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        workArea = monitorInfo.WorkArea;
        return workArea.HasArea;
    }

    internal static double GetWindowDpiScale(IntPtr windowHandle, double fallbackScale = 1)
    {
        try
        {
            var dpi = GetDpiForWindow(windowHandle);
            return dpi > 0 ? dpi / 96d : fallbackScale;
        }
        catch (DllNotFoundException)
        {
            return fallbackScale;
        }
        catch (EntryPointNotFoundException)
        {
            return fallbackScale;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out NativeRectangle attributeValue,
        int attributeSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRectangle
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public NativeRectangle(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly bool HasArea => Right > Left && Bottom > Top;
}
