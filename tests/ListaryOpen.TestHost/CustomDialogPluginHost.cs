using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ListaryOpen.TestHost;

internal static class CustomDialogPluginHost
{
    internal const string WindowClassName = "ListaryOpenPluginFixtureBrowser";
    internal const string WindowTitle = "ListaryOpen Plugin Fixture Browser";
    private const uint WmCopyData = 0x004A;
    private const uint WmDestroy = 0x0002;
    private const uint ProtocolMarker = 0x4C4F5046;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsVisible = 0x10000000;
    private const uint WsChild = 0x40000000;
    private const uint SsLeft = 0x00000000;
    private const int SwShow = 5;
    private static readonly WindowProcedureCallback WindowProcedureDelegate = HandleWindowMessage;
    private static Action<IntPtr, string, bool>? _stateChanged;
    private static IntPtr _pathLabel;

    internal static int Run(string initialDirectory, Action<IntPtr, string, bool> stateChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialDirectory);
        ArgumentNullException.ThrowIfNull(stateChanged);
        var normalizedInitialDirectory = Path.GetFullPath(initialDirectory);
        if (!Directory.Exists(normalizedInitialDirectory))
        {
            throw new DirectoryNotFoundException(normalizedInitialDirectory);
        }

        _stateChanged = stateChanged;
        var module = GetModuleHandle(null);
        RegisterWindowClass(module);
        var window = CreateWindowEx(
            0,
            WindowClassName,
            WindowTitle,
            WsOverlappedWindow | WsVisible,
            180,
            140,
            720,
            220,
            IntPtr.Zero,
            IntPtr.Zero,
            module,
            IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not create custom plugin fixture window: {Marshal.GetLastWin32Error()}");
        }

        _pathLabel = CreateWindowEx(
            0,
            "STATIC",
            normalizedInitialDirectory,
            WsChild | WsVisible | SsLeft,
            24,
            62,
            650,
            36,
            window,
            new IntPtr(1001),
            module,
            IntPtr.Zero);
        if (_pathLabel == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not create custom browser breadcrumb: {Marshal.GetLastWin32Error()}");
        }

        _ = ShowWindow(window, SwShow);
        _ = SetForegroundWindow(window);
        stateChanged(window, normalizedInitialDirectory, false);
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }

        _stateChanged = null;
        _pathLabel = IntPtr.Zero;
        return 0;
    }

    private static IntPtr HandleWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmCopyData)
        {
            return HandleCopyData(window, lParam);
        }

        if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private static IntPtr HandleCopyData(IntPtr window, IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            var copyData = Marshal.PtrToStructure<CopyDataStruct>(value);
            if (copyData.Data != new UIntPtr(ProtocolMarker) ||
                copyData.DataPointer == IntPtr.Zero ||
                copyData.ByteCount is < 4 or > 65_536 ||
                copyData.ByteCount % sizeof(char) != 0)
            {
                return IntPtr.Zero;
            }

            var characterCount = copyData.ByteCount / sizeof(char);
            var json = Marshal.PtrToStringUni(copyData.DataPointer, characterCount)?.TrimEnd('\0');
            var command = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<SetFolderCommand>(json);
            var requestedFolder = command?.FolderPath;
            if (command is not { Version: 1, Command: "SetFolder" } ||
                string.IsNullOrWhiteSpace(requestedFolder) ||
                !Directory.Exists(requestedFolder))
            {
                return IntPtr.Zero;
            }

            var folderPath = Path.GetFullPath(requestedFolder);
            if (_pathLabel == IntPtr.Zero || !SetWindowText(_pathLabel, folderPath))
            {
                return IntPtr.Zero;
            }

            _stateChanged?.Invoke(window, folderPath, true);
            return new IntPtr(1);
        }
        catch (Exception exception) when (exception is JsonException
                                           or ArgumentException
                                           or IOException
                                           or NotSupportedException
                                           or UnauthorizedAccessException)
        {
            return IntPtr.Zero;
        }
    }

    private static void RegisterWindowClass(IntPtr module)
    {
        var windowClass = new NativeWindowClass
        {
            Instance = module,
            ClassName = WindowClassName,
            WindowProcedure = WindowProcedureDelegate
        };
        if (RegisterClass(ref windowClass) == 0)
        {
            throw new InvalidOperationException($"Could not register custom browser class: {Marshal.GetLastWin32Error()}");
        }
    }

    private sealed record SetFolderCommand(int Version, string Command, string FolderPath);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public UIntPtr Data;
        public int ByteCount;
        public IntPtr DataPointer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWindowClass
    {
        public uint Style;
        public WindowProcedureCallback WindowProcedure;
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WindowProcedureCallback(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref NativeWindowClass windowClass);

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr window, string text);
}
