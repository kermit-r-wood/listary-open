using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Dialog;

public sealed class BlenderFileBrowserAdapter : ICustomDialogAdapter
{
    private const string ProcessName = "blender";
    private const string WindowClassName = "GHOST_WindowClass";
    private const string WindowTitle = "Blender File View";
    private static readonly TimeSpan ForegroundDelay = TimeSpan.FromMilliseconds(150);

    private readonly Func<DialogWindowSnapshot?> _activeWindowProvider;
    private readonly Func<IntPtr, bool> _setForegroundWindow;
    private readonly Func<IReadOnlyList<NativeMethods.Input>, bool> _sendInputs;
    private readonly Action<TimeSpan> _wait;

    public BlenderFileBrowserAdapter()
        : this(
            GetActiveWindowSnapshot,
            NativeMethods.SetForegroundWindow,
            TrySendKeyboardInputs,
            delay => Thread.Sleep(delay))
    {
    }

    internal BlenderFileBrowserAdapter(
        Func<DialogWindowSnapshot?> activeWindowProvider,
        Func<IntPtr, bool> setForegroundWindow,
        Func<IReadOnlyList<NativeMethods.Input>, bool> sendInputs,
        Action<TimeSpan> wait)
    {
        _activeWindowProvider = activeWindowProvider ?? throw new ArgumentNullException(nameof(activeWindowProvider));
        _setForegroundWindow = setForegroundWindow ?? throw new ArgumentNullException(nameof(setForegroundWindow));
        _sendInputs = sendInputs ?? throw new ArgumentNullException(nameof(sendInputs));
        _wait = wait ?? throw new ArgumentNullException(nameof(wait));
    }

    public string Name => "Blender file browser";

    public bool CanHandleActiveWindow()
    {
        return IsBlenderFileBrowser(_activeWindowProvider());
    }

    public Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        cancellationToken.ThrowIfCancellationRequested();

        var window = _activeWindowProvider();
        if (!IsBlenderFileBrowser(window) || !Directory.Exists(folderPath))
        {
            return Task.FromResult(false);
        }

        if (!_setForegroundWindow(window.WindowHandle))
        {
            return Task.FromResult(false);
        }

        _wait(ForegroundDelay);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_sendInputs(BuildNavigateToFolderInputs(folderPath)));
    }

    internal static IReadOnlyList<NativeMethods.Input> BuildNavigateToFolderInputs(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var inputs = new List<NativeMethods.Input>();
        AddVirtualKey(inputs, NativeMethods.VkControl, keyUp: false);
        AddVirtualKey(inputs, NativeMethods.VkL, keyUp: false);
        AddVirtualKey(inputs, NativeMethods.VkL, keyUp: true);
        AddVirtualKey(inputs, NativeMethods.VkControl, keyUp: true);

        foreach (var character in folderPath)
        {
            AddUnicodeKey(inputs, character, keyUp: false);
            AddUnicodeKey(inputs, character, keyUp: true);
        }

        AddVirtualKey(inputs, NativeMethods.VkReturn, keyUp: false);
        AddVirtualKey(inputs, NativeMethods.VkReturn, keyUp: true);
        return inputs;
    }

    private static bool IsBlenderFileBrowser([NotNullWhen(true)] DialogWindowSnapshot? window)
    {
        return window is not null &&
            ProcessNamesEqual(window.ProcessName, ProcessName) &&
            string.Equals(window.ClassName, WindowClassName, StringComparison.Ordinal) &&
            string.Equals(window.Title, WindowTitle, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProcessNamesEqual(string value, string expected)
    {
        var processName = Path.GetFileNameWithoutExtension(value.Trim());
        return string.Equals(processName, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static DialogWindowSnapshot? GetActiveWindowSnapshot()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var processName = GetProcessName(handle);
        return string.IsNullOrWhiteSpace(processName)
            ? null
            : new DialogWindowSnapshot(
                handle,
                processName,
                GetClassName(handle),
                GetWindowText(handle));
    }

    private static string? GetProcessName(IntPtr handle)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or Win32Exception
                                          or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetClassName(IntPtr handle)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(handle, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static string GetWindowText(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowText(handle, buffer, buffer.Length);
        return copied <= 0 ? string.Empty : new string(buffer, 0, copied);
    }

    private static void AddVirtualKey(List<NativeMethods.Input> inputs, ushort virtualKey, bool keyUp)
    {
        inputs.Add(new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? NativeMethods.KeyEventFKeyUp : 0
                }
            }
        });
    }

    private static void AddUnicodeKey(List<NativeMethods.Input> inputs, char character, bool keyUp)
    {
        inputs.Add(new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    ScanCode = character,
                    Flags = NativeMethods.KeyEventFUnicode | (keyUp ? NativeMethods.KeyEventFKeyUp : 0)
                }
            }
        });
    }

    private static bool TrySendKeyboardInputs(IReadOnlyList<NativeMethods.Input> inputs)
    {
        if (inputs.Count == 0)
        {
            return true;
        }

        try
        {
            var inputArray = inputs.ToArray();
            var sent = NativeMethods.SendInput(
                (uint)inputArray.Length,
                inputArray,
                Marshal.SizeOf<NativeMethods.Input>());
            return sent == inputArray.Length;
        }
        catch (Exception exception) when (exception is Win32Exception or ExternalException)
        {
            Trace.TraceError(exception.ToString());
            return false;
        }
    }
}
