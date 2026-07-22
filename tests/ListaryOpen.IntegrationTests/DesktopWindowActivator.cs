using System.Runtime.InteropServices;

namespace ListaryOpen.IntegrationTests;

internal static class DesktopWindowActivator
{
    private const int SwRestore = 9;
    private const uint SetWindowPosNoMove = 0x0002;
    private const uint SetWindowPosNoSize = 0x0001;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const uint SetWindowPosShowWindow = 0x0040;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const uint GaRoot = 2;
    private const uint WmNcHitTest = 0x0084;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtBottomRight = 17;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    public static bool TryActivate(IntPtr window, TimeSpan? timeout = null)
    {
        if (window == IntPtr.Zero || !IsWindow(window))
        {
            return false;
        }

        var wasTopmost = (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExTopmost) != 0;
        var cursorCaptured = GetCursorPos(out var originalCursor);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                _ = ShowWindow(window, SwRestore);
                _ = SetWindowPos(
                    window,
                    HwndTopmost,
                    0,
                    0,
                    0,
                    0,
                    SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
                _ = SetForegroundWindow(window);

                if (TryGetSafeNonClientPoint(window, out var activationPoint)
                    && SetCursorPos(activationPoint.X, activationPoint.Y)
                    && GetCursorPos(out var actualCursor)
                    && actualCursor.X == activationPoint.X
                    && actualCursor.Y == activationPoint.Y
                    && RootWindowAt(actualCursor) == window)
                {
                    mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
                }

                var observationDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
                while (DateTime.UtcNow < observationDeadline)
                {
                    if (GetForegroundWindow() == window)
                    {
                        return true;
                    }

                    Thread.Sleep(20);
                }
            }

            return GetForegroundWindow() == window;
        }
        finally
        {
            if (IsWindow(window))
            {
                _ = SetWindowPos(
                    window,
                    wasTopmost ? HwndTopmost : HwndNotTopmost,
                    0,
                    0,
                    0,
                    0,
                    SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosNoActivate);
            }

            if (cursorCaptured)
            {
                _ = SetCursorPos(originalCursor.X, originalCursor.Y);
            }
        }
    }

    private static bool TryGetSafeNonClientPoint(IntPtr window, out NativePoint point)
    {
        point = default;
        if (!GetWindowRect(window, out var windowBounds)
            || !GetClientRect(window, out var clientBounds))
        {
            return false;
        }

        var clientTopLeft = new NativePoint { X = clientBounds.Left, Y = clientBounds.Top };
        var clientBottomRight = new NativePoint { X = clientBounds.Right, Y = clientBounds.Bottom };
        if (!ClientToScreen(window, ref clientTopLeft)
            || !ClientToScreen(window, ref clientBottomRight))
        {
            return false;
        }

        var middleY = clientTopLeft.Y + Math.Max(1, (clientBottomRight.Y - clientTopLeft.Y) / 2);
        var candidates = new[]
        {
            new NativePoint
            {
                X = windowBounds.Left + Math.Max(1, (clientTopLeft.X - windowBounds.Left) / 2),
                Y = middleY
            },
            new NativePoint
            {
                X = clientBottomRight.X + Math.Max(1, (windowBounds.Right - clientBottomRight.X) / 2),
                Y = middleY
            },
            new NativePoint
            {
                X = clientTopLeft.X + Math.Max(1, (clientBottomRight.X - clientTopLeft.X) / 2),
                Y = clientBottomRight.Y + Math.Max(1, (windowBounds.Bottom - clientBottomRight.Y) / 2)
            }
        };

        foreach (var candidate in candidates)
        {
            if (candidate.X < windowBounds.Left || candidate.X >= windowBounds.Right
                || candidate.Y < windowBounds.Top || candidate.Y >= windowBounds.Bottom
                || IsInsideClient(candidate, clientTopLeft, clientBottomRight)
                || RootWindowAt(candidate) != window
                || !TryGetNonClientHit(window, candidate, out var hit)
                || (hit != HtCaption && (hit < HtLeft || hit > HtBottomRight)))
            {
                continue;
            }

            point = candidate;
            return true;
        }

        return TryFindSafeNonClientHit(window, windowBounds, out point);
    }

    private static bool TryFindSafeNonClientHit(IntPtr window, NativeRect bounds, out NativePoint point)
    {
        point = default;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        if (width < 3 || height < 3)
        {
            return false;
        }

        var candidates = new List<NativePoint>();
        var middleX = bounds.Left + width / 2;
        var middleY = bounds.Top + height / 2;
        candidates.Add(new NativePoint { X = bounds.Left + 1, Y = middleY });
        candidates.Add(new NativePoint { X = bounds.Right - 2, Y = middleY });
        candidates.Add(new NativePoint { X = middleX, Y = bounds.Top + 1 });
        candidates.Add(new NativePoint { X = middleX, Y = bounds.Bottom - 2 });

        var topBandBottom = Math.Min(bounds.Bottom - 1, bounds.Top + Math.Min(72, height / 3));
        for (var y = bounds.Top + 4; y < topBandBottom; y += 8)
        {
            for (var x = bounds.Left + 16; x < bounds.Right - 16; x += 24)
            {
                candidates.Add(new NativePoint { X = x, Y = y });
            }
        }

        foreach (var candidate in candidates)
        {
            if (RootWindowAt(candidate) != window
                || !TryGetNonClientHit(window, candidate, out var hit)
                || (hit != HtCaption && (hit < HtLeft || hit > HtBottomRight)))
            {
                continue;
            }

            point = candidate;
            return true;
        }

        return false;
    }

    private static bool TryGetNonClientHit(IntPtr window, NativePoint point, out int hit)
    {
        var packedPoint = new IntPtr(unchecked((point.Y << 16) | (point.X & 0xFFFF)));
        var sent = SendMessageTimeout(
            window,
            WmNcHitTest,
            UIntPtr.Zero,
            packedPoint,
            SmtoAbortIfHung,
            100,
            out var result);
        hit = result.ToInt32();
        return sent != IntPtr.Zero;
    }

    private static bool IsInsideClient(NativePoint point, NativePoint topLeft, NativePoint bottomRight) =>
        point.X >= topLeft.X && point.X < bottomRight.X
        && point.Y >= topLeft.Y && point.Y < bottomRight.Y;

    private static IntPtr RootWindowAt(NativePoint point)
    {
        var hit = WindowFromPoint(point);
        return hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, GaRoot);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
}
