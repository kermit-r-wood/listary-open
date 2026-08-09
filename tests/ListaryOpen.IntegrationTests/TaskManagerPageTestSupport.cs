using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ListaryOpen.IntegrationTests;

internal static class TaskManagerPageTestSupport
{
    private const int SwRestore = 9;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkTab = 0x09;
    private const ushort Vk1 = 0x31;

    public static bool PrepareRowPage(
        IntPtr taskManagerWindow,
        Func<bool> rowsAreUsable,
        out string preparationAction)
    {
        ArgumentNullException.ThrowIfNull(rowsAreUsable);
        preparationAction = "wait-for-existing-row-page";
        if (WaitUntil(rowsAreUsable, TimeSpan.FromSeconds(8)))
        {
            return true;
        }

        if (!DesktopWindowActivator.TryActivate(taskManagerWindow, TimeSpan.FromSeconds(5)))
        {
            preparationAction = "task-manager-could-not-be-foregrounded";
            return false;
        }

        // Windows 11 Task Manager maps Ctrl+1 to Processes. Older tab-based
        // builds ignore it, so cycling Ctrl+Tab below remains deterministic on
        // those versions. This is test setup input to the real system window;
        // it is not a product navigation fallback.
        SendChord(Vk1);
        preparationAction = "Ctrl+1";
        if (WaitUntil(rowsAreUsable, TimeSpan.FromSeconds(4)))
        {
            return true;
        }

        for (var page = 1; page <= 8; page++)
        {
            SendChord(VkTab);
            preparationAction = $"Ctrl+1 then Ctrl+Tab x{page}";
            if (WaitUntil(rowsAreUsable, TimeSpan.FromSeconds(3)))
            {
                return true;
            }
        }

        return false;
    }

    private static void SendChord(ushort key)
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, keyUp: false),
            KeyboardInput(key, keyUp: false),
            KeyboardInput(key, keyUp: true),
            KeyboardInput(VkControl, keyUp: true)
        };
        var inputSize = Marshal.SizeOf<NativeInput>();
        var expectedInputSize = IntPtr.Size == 8 ? 40 : 28;
        if (inputSize != expectedInputSize)
        {
            throw new InvalidOperationException(
                $"Win32 INPUT has size {inputSize}; expected {expectedInputSize} for this process architecture.");
        }

        var sent = SendInput((uint)inputs.Length, inputs, inputSize);
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"Could not send the Task Manager page setup chord; sent {sent} of {inputs.Length} inputs.");
        }
    }

    private static NativeInput KeyboardInput(ushort key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Union = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = key,
                ScanCode = unchecked((ushort)MapVirtualKey(key, 0)),
                Flags = keyUp ? KeyEventKeyUp : 0
            }
        }
    };

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                if (condition())
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or COMException)
            {
            }

            Thread.Sleep(150);
        }

        try { return condition(); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or COMException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
