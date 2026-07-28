using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ListaryOpen.App;

internal sealed class ShellPreviewHost : HwndHost
{
    private const string PreviewHandlerShellExtension = "{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const int StgmReadShareDenyNone = 0x40;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;

    private IntPtr _hostWindow;
    private IPreviewHandler? _previewHandler;
    private object? _previewHandlerObject;
    private object? _previewStream;

    internal bool TryPreview(string path)
    {
        ClearPreview();
        if (_hostWindow == IntPtr.Zero || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var handlerId = FindPreviewHandler(path);
            if (handlerId is null)
            {
                return false;
            }

            var handlerType = Type.GetTypeFromCLSID(handlerId.Value, throwOnError: false);
            _previewHandlerObject = handlerType is null ? null : Activator.CreateInstance(handlerType);
            _previewHandler = _previewHandlerObject as IPreviewHandler;
            if (_previewHandler is null || _previewHandlerObject is null || !TryInitialize(_previewHandlerObject, path))
            {
                ClearPreview();
                return false;
            }

            var bounds = GetHostBounds();
            Marshal.ThrowExceptionForHR(_previewHandler.SetWindow(_hostWindow, ref bounds));
            Marshal.ThrowExceptionForHR(_previewHandler.DoPreview());
            return true;
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or InvalidOperationException or
            UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException or
            System.Reflection.TargetInvocationException)
        {
            ClearPreview();
            return false;
        }
    }

    internal void ClearPreview()
    {
        if (_previewHandler is not null)
        {
            try
            {
                _previewHandler.Unload();
            }
            catch (Exception)
            {
                // Third-party handlers must never make selection changes or pane teardown fail.
            }
        }

        _previewHandler = null;
        TryReleaseComObject(ref _previewStream);
        TryReleaseComObject(ref _previewHandlerObject);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hostWindow = CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipChildren,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
        if (_hostWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not create the preview host window.");
        }

        return new HandleRef(this, _hostWindow);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        ClearPreview();
        if (hwnd.Handle != IntPtr.Zero)
        {
            DestroyWindow(hwnd.Handle);
        }

        _hostWindow = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_previewHandler is null)
        {
            return;
        }

        try
        {
            var bounds = GetHostBounds();
            _previewHandler.SetRect(ref bounds);
        }
        catch (COMException)
        {
        }
    }

    private bool TryInitialize(object handler, string path)
    {
        if (handler is IInitializeWithStream initializeWithStream &&
            SHCreateStreamOnFileEx(path, StgmReadShareDenyNone, 0, false, null, out var stream) >= 0)
        {
            _previewStream = stream;
            if (initializeWithStream.Initialize(stream, StgmReadShareDenyNone) >= 0)
            {
                return true;
            }

            ReleaseComObject(ref _previewStream);
        }

        return handler is IInitializeWithFile initializeWithFile &&
            initializeWithFile.Initialize(path, StgmReadShareDenyNone) >= 0;
    }

    private PreviewRect GetHostBounds()
    {
        GetClientRect(_hostWindow, out var bounds);
        return bounds;
    }

    private static Guid? FindPreviewHandler(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var handler = ReadHandler($@"{extension}\shellex\{PreviewHandlerShellExtension}") ??
            ReadHandler($@"SystemFileAssociations\{extension}\shellex\{PreviewHandlerShellExtension}");
        if (handler is not null)
        {
            return handler;
        }

        using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
        var programId = extensionKey?.GetValue(null) as string;
        return string.IsNullOrWhiteSpace(programId)
            ? null
            : ReadHandler($@"{programId}\shellex\{PreviewHandlerShellExtension}");
    }

    private static Guid? ReadHandler(string keyPath)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(keyPath);
        return Guid.TryParse(key?.GetValue(null) as string, out var value) ? value : null;
    }

    private static void ReleaseComObject(ref object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }

        value = null;
    }

    private static void TryReleaseComObject(ref object? value)
    {
        try
        {
            ReleaseComObject(ref value);
        }
        catch (Exception)
        {
            value = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        [PreserveSig] int SetWindow(IntPtr parentWindow, ref PreviewRect rectangle);
        [PreserveSig] int SetRect(ref PreviewRect rectangle);
        [PreserveSig] int DoPreview();
        [PreserveSig] int Unload();
        [PreserveSig] int SetFocus();
        [PreserveSig] int QueryFocus(out IntPtr windowHandle);
        [PreserveSig] int TranslateAccelerator(ref NativeMessage message);
    }

    [ComImport]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, int mode);
    }

    [ComImport]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        [PreserveSig] int Initialize(IStream stream, int mode);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public System.Drawing.Point Point;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentWindow,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out PreviewRect rectangle);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(
        string fileName,
        int mode,
        uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create,
        IStream? template,
        out IStream stream);
}
