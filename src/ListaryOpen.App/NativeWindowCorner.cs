using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ListaryOpen.App;

internal static class NativeWindowCorner
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceDefault = 0;
    private const int DwmWindowCornerPreferenceRound = 2;
    internal const int DefaultRadiusDip = 10;

    internal static void ApplyRounded(Window window, int radiusDip = DefaultRadiusDip)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (radiusDip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusDip));
        }

        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            handle = helper.EnsureHandle();
        }

        if (handle == IntPtr.Zero)
        {
            return;
        }

        // Standard chrome windows keep the system border; clear any custom region.
        if (window.WindowStyle != WindowStyle.None)
        {
            ClearRoundedRegion(handle);
            TrySetDwmCornerPreference(handle, DwmWindowCornerPreferenceDefault);
            return;
        }

        // Windows 11+ can round borderless windows via DWM.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            ClearRoundedRegion(handle);
            TrySetDwmCornerPreference(handle, DwmWindowCornerPreferenceRound);
            return;
        }

        // Windows 10 and older: clip the HWND to a rounded rect so the window
        // itself is not a hard rectangle (DWM corner preference is ignored).
        TrySetDwmCornerPreference(handle, DwmWindowCornerPreferenceDefault);
        ApplyRoundedRegion(window, handle, radiusDip);
    }

    private static void ApplyRoundedRegion(Window window, IntPtr handle, int radiusDip)
    {
        if (window.ActualWidth <= 0 || window.ActualHeight <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
        var diameter = Math.Max(2, (int)Math.Round(radiusDip * 2 * Math.Min(dpi.DpiScaleX, dpi.DpiScaleY)));

        // CreateRoundRectRgn right/bottom are exclusive; +1 avoids a one-pixel clip.
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == IntPtr.Zero)
        {
            return;
        }

        // SetWindowRgn takes ownership of the region handle on success.
        if (SetWindowRgn(handle, region, redraw: true) == 0)
        {
            _ = DeleteObject(region);
        }
    }

    private static void ClearRoundedRegion(IntPtr handle)
    {
        _ = SetWindowRgn(handle, IntPtr.Zero, redraw: true);
    }

    private static void TrySetDwmCornerPreference(IntPtr handle, int preference)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var value = preference;
        _ = DwmSetWindowAttribute(
            handle,
            DwmWindowCornerPreference,
            ref value,
            Marshal.SizeOf<int>());
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int widthEllipse,
        int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr windowHandle, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);
}
