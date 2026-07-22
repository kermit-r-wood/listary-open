using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ListaryOpen.IntegrationTests;

internal static class FirefoxFixtureUi
{
    private const uint CaptureBlt = 0x40000000;
    private const uint SourceCopy = 0x00CC0020;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    public static Rect ClickOpenFilePicker(
        IntPtr firefoxWindow,
        Func<IntPtr, bool> activateWindow,
        TimeSpan timeout)
    {
        BitmapSource? screenshot = null;
        NativeRect windowBounds = default;
        Int32Rect buttonBounds = Int32Rect.Empty;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout && buttonBounds.IsEmpty)
        {
            if (!activateWindow(firefoxWindow))
            {
                throw new InvalidOperationException("Could not activate the isolated Firefox fixture before visual targeting.");
            }

            Thread.Sleep(200);
            screenshot = CaptureWindow(firefoxWindow, out windowBounds);
            buttonBounds = FindPurpleFixtureButton(screenshot);
            if (buttonBounds.IsEmpty)
            {
                Thread.Sleep(100);
            }
        }

        if (screenshot is null || buttonBounds.IsEmpty)
        {
            throw new InvalidOperationException(
                "Firefox did not visibly render the unique purple file-picker button; " +
                "the test refuses to guess a coordinate or use a keyboard substitute.");
        }

        SaveVisualEvidence(screenshot, "firefox-before-physical-click.png");
        var x = windowBounds.Left + buttonBounds.X + buttonBounds.Width / 2;
        var y = windowBounds.Top + buttonBounds.Y + buttonBounds.Height / 2;
        if (!SetCursorPos(x, y))
        {
            throw new InvalidOperationException($"Could not move the real pointer to the visually located Firefox button at ({x}, {y}).");
        }

        Thread.Sleep(350);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(120);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        return new Rect(
            windowBounds.Left + buttonBounds.X,
            windowBounds.Top + buttonBounds.Y,
            buttonBounds.Width,
            buttonBounds.Height);
    }

    public static void CaptureWindowEvidence(IntPtr window, string fileName)
    {
        var screenshot = CaptureWindow(window, out _);
        SaveVisualEvidence(screenshot, fileName);
    }

    private static Int32Rect FindPurpleFixtureButton(BitmapSource source)
        => FindColorRegion(
            source,
            (red, green, blue) => red is >= 82 and <= 125 && green is >= 28 and <= 82 && blue is >= 205 and <= 255);

    private static Int32Rect FindColorRegion(
        BitmapSource source,
        Func<byte, byte, byte, bool> matchesColor)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        var minX = converted.PixelWidth;
        var minY = converted.PixelHeight;
        var maxX = -1;
        var maxY = -1;
        var matches = 0;
        for (var y = 0; y < converted.PixelHeight; y++)
        {
            var row = y * stride;
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                var offset = row + x * 4;
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                if (!matchesColor(red, green, blue))
                {
                    continue;
                }

                matches++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (matches < 15_000 || maxX - minX < 300 || maxY - minY < 70)
        {
            return Int32Rect.Empty;
        }

        return new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static BitmapSource CaptureWindow(IntPtr window, out NativeRect bounds)
    {
        if (!GetWindowRect(window, out bounds) || bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            throw new InvalidOperationException("Could not read the isolated Firefox window bounds.");
        }

        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var desktop = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(desktop);
        var bitmap = CreateCompatibleBitmap(desktop, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, desktop, bounds.Left, bounds.Top, SourceCopy | CaptureBlt))
            {
                throw new InvalidOperationException("Could not capture the Firefox fixture for visual targeting.");
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            _ = SelectObject(memory, previous);
            _ = DeleteObject(bitmap);
            _ = DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, desktop);
        }
    }

    private static void SaveVisualEvidence(BitmapSource source, string fileName)
    {
        var directory = Environment.GetEnvironmentVariable("LISTARYOPEN_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "ListaryOpen.sln")))
            {
                current = current.Parent;
            }
            directory = Path.Combine(current?.FullName ?? AppContext.BaseDirectory, "artifacts", "acceptance-results", "visual-evidence");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
