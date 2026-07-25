using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Windows;

/// <summary>
/// Imm32-based detection of active IME composition (shared by UI and low-level hooks).
/// </summary>
public static class ImeComposition
{
    private const int GcsCompStr = 0x0008;
    private const int GuiThreadInfoSize = 72;

    /// <summary>
    /// True when the focused (or foreground) window has an open IME with composition text.
    /// Uses GUI thread focus so low-level keyboard hooks (non-UI thread) still work.
    /// </summary>
    public static bool IsComposing()
    {
        var hwnd = ResolveFocusWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var context = ImmGetContext(hwnd);
        if (context == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!ImmGetOpenStatus(context))
            {
                return false;
            }

            var length = ImmGetCompositionStringW(context, GcsCompStr, null, 0);
            return length > 0;
        }
        finally
        {
            ImmReleaseContext(hwnd, context);
        }
    }

    private static IntPtr ResolveFocusWindow()
    {
        // Prefer GUI-thread focus (valid from hook threads); fall back to process focus/foreground.
        var info = new GuiThreadInfo { CbSize = Marshal.SizeOf<GuiThreadInfo>() };
        if (GetGUIThreadInfo(0, ref info) && info.HwndFocus != IntPtr.Zero)
        {
            return info.HwndFocus;
        }

        var hwnd = GetFocus();
        if (hwnd != IntPtr.Zero)
        {
            return hwnd;
        }

        return GetForegroundWindow();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int CbSize;
        public int Flags;
        public IntPtr HwndActive;
        public IntPtr HwndFocus;
        public IntPtr HwndCapture;
        public IntPtr HwndMenuOwner;
        public IntPtr HwndMoveSize;
        public IntPtr HwndCaret;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr windowHandle);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr windowHandle, IntPtr inputContext);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmGetOpenStatus(IntPtr inputContext);

    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    private static extern int ImmGetCompositionStringW(
        IntPtr inputContext,
        int index,
        byte[]? buffer,
        int bufferLength);
}
