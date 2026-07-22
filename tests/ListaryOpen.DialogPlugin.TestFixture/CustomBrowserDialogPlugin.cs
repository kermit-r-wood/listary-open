using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ListaryOpen.Infrastructure.Dialog;

namespace ListaryOpen.DialogPlugin.TestFixture;

public sealed class CustomBrowserDialogPlugin : IDialogJumpPlugin
{
    public const string PluginId = "listaryopen.fixture.custom-browser";
    public const string HostProcessName = "ListaryOpen.TestHost";
    public const string HostWindowClass = "ListaryOpenPluginFixtureBrowser";
    public const string HostWindowTitle = "ListaryOpen Plugin Fixture Browser";
    private const uint WmCopyData = 0x004A;
    private const uint SendMessageTimeoutAbortIfHung = 0x0002;
    private const uint ProtocolMarker = 0x4C4F5046;

    public string Id => PluginId;

    public string Name => "ListaryOpen structured custom-browser fixture";

    public DialogPluginTarget? TryCaptureActiveTarget()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !IsWindow(window) ||
            !string.Equals(ReadClassName(window), HostWindowClass, StringComparison.Ordinal) ||
            !string.Equals(ReadWindowTitle(window), HostWindowTitle, StringComparison.Ordinal))
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (!string.Equals(process.ProcessName, HostProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new DialogPluginTarget(
                Id,
                new DialogWindowSnapshot(
                    window,
                    processId,
                    process.ProcessName,
                    HostWindowClass,
                    HostWindowTitle,
                    DateTimeOffset.UtcNow));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public Task<DialogJumpResult> JumpToFolderAsync(
        DialogPluginTarget target,
        string folderPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(target.PluginId, Id, StringComparison.Ordinal) ||
            target.Window.WindowHandle == IntPtr.Zero ||
            !IsWindow(target.Window.WindowHandle))
        {
            return Task.FromResult(new DialogJumpResult(DialogJumpStatus.TargetGone, "Fixture target is unavailable."));
        }

        if (!Directory.Exists(folderPath))
        {
            return Task.FromResult(new DialogJumpResult(DialogJumpStatus.TargetGone, "Fixture folder no longer exists."));
        }

        var payload = JsonSerializer.Serialize(new SetFolderCommand(
            Version: 1,
            Command: "SetFolder",
            FolderPath: Path.GetFullPath(folderPath)));
        var payloadPointer = Marshal.StringToHGlobalUni(payload);
        var copyData = new CopyDataStruct
        {
            Data = new UIntPtr(ProtocolMarker),
            ByteCount = checked((payload.Length + 1) * sizeof(char)),
            DataPointer = payloadPointer
        };
        var structurePointer = Marshal.AllocHGlobal(Marshal.SizeOf<CopyDataStruct>());
        try
        {
            Marshal.StructureToPtr(copyData, structurePointer, fDeleteOld: false);
            var sendResult = SendMessageTimeout(
                target.Window.WindowHandle,
                WmCopyData,
                IntPtr.Zero,
                structurePointer,
                SendMessageTimeoutAbortIfHung,
                1_000,
                out var commandResult);
            return Task.FromResult(
                sendResult != IntPtr.Zero && commandResult == new UIntPtr(1)
                    ? new DialogJumpResult(DialogJumpStatus.Success, "Fixture host confirmed structured direct navigation.")
                    : new DialogJumpResult(DialogJumpStatus.TargetGone, "Fixture host rejected or did not acknowledge direct navigation."));
        }
        finally
        {
            Marshal.FreeHGlobal(structurePointer);
            Marshal.FreeHGlobal(payloadPointer);
        }
    }

    private static string ReadClassName(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string ReadWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        var copied = GetWindowText(window, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    private sealed record SetFolderCommand(int Version, string Command, string FolderPath);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyDataStruct
    {
        public UIntPtr Data;
        public int ByteCount;
        public IntPtr DataPointer;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, char[] className, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, char[] text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out UIntPtr result);
}
