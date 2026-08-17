using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Windows;

/// <summary>
/// Owns the low-level keyboard hook on a small dedicated message-pump thread.
/// WPF layout, search, and Explorer synchronization must never delay hook
/// delivery; Windows can otherwise pass a key while the UI thread is busy.
/// </summary>
internal sealed class GlobalTextInputThread : IDisposable
{
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(5);
    private readonly GlobalTextInputService _service;
    private readonly ManualResetEventSlim _ready = new(initialState: false);
    private readonly Thread _thread;
    private Exception? _startupFailure;
    private uint _threadId;
    private int _started;
    private int _disposed;

    public GlobalTextInputThread(GlobalTextInputService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ListaryOpen global input hook"
        };
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _thread.Start();
        if (!_ready.Wait(StartupTimeout))
        {
            Dispose();
            throw new InvalidOperationException("The global input hook thread did not start in time.");
        }

        if (_startupFailure is not null)
        {
            Dispose();
            throw new InvalidOperationException("The global input hook could not start.", _startupFailure);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Volatile.Read(ref _started) == 0)
        {
            _service.Dispose();
            return;
        }

        var threadId = Volatile.Read(ref _threadId);
        if (_thread.IsAlive && threadId != 0)
        {
            _ = PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        }

        if (_thread.IsAlive && Thread.CurrentThread != _thread && !_thread.Join(TimeSpan.FromSeconds(10)))
        {
            // Unhooking is safe from another thread and prevents a stale hook
            // from contaminating the rest of shutdown if the pump is wedged.
            _service.Dispose();
            if (threadId != 0)
            {
                _ = PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
            }

            if (!_thread.Join(TimeSpan.FromSeconds(2)))
            {
                Trace.TraceWarning("The global input hook thread did not stop before shutdown completed.");
            }
        }

        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            // Force creation of the thread message queue before Start returns,
            // so shutdown can always post WM_QUIT without a race.
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
            _service.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _startupFailure = exception;
            TrySignalReady();
            _service.Dispose();
            return;
        }

        TrySignalReady();
        try
        {
            while (true)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0)
                {
                    break;
                }

                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        finally
        {
            _service.Dispose();
        }
    }

    private void TrySignalReady()
    {
        try
        {
            _ready.Set();
        }
        catch (ObjectDisposedException)
        {
        }
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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        UIntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
}
