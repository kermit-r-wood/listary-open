using System.Runtime.InteropServices;

namespace ListaryOpen.IntegrationTests;

internal static class DesktopInput
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    public static void SendChord(ushort modifier, ushort key)
    {
        var inputs = new[]
        {
            KeyboardInput(modifier, keyUp: false),
            KeyboardInput(key, keyUp: false),
            KeyboardInput(key, keyUp: true),
            KeyboardInput(modifier, keyUp: true)
        };
        var inputSize = Marshal.SizeOf<NativeInput>();
        var expectedSize = IntPtr.Size == 8 ? 40 : 28;
        if (inputSize != expectedSize)
        {
            throw new InvalidOperationException($"Win32 INPUT has size {inputSize}; expected {expectedSize}.");
        }
        var sent = SendInput((uint)inputs.Length, inputs, inputSize);
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException(
                $"SendInput delivered only {sent} of {inputs.Length} chord events; Win32={Marshal.GetLastWin32Error()}.");
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
        public IntPtr ExtraInfo;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
