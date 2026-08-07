using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Search;

/// <summary>
/// Temporarily puts a synchronous indexing section into Windows background
/// mode. This lowers CPU, disk-I/O, and memory priority without lowering the
/// interactive application process as a whole.
/// </summary>
internal sealed class WindowsBackgroundMode : IDisposable
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    private readonly bool _enabled;
    private bool _disposed;

    private WindowsBackgroundMode(bool enabled)
    {
        _enabled = enabled;
    }

    public static WindowsBackgroundMode EnterCurrentThread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsBackgroundMode(enabled: false);
        }

        var enabled = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        return new WindowsBackgroundMode(enabled);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_enabled)
        {
            _ = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr threadHandle, int priority);
}
