using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace ListaryOpen.Infrastructure.Hooks;

public interface IHookIpcClient
{
    Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken);

    Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken);
}

public interface IHookActiveDialogQueryClient
{
    Task<HookActiveDialogResult> GetActiveDialogResultAsync(CancellationToken cancellationToken);
}

public interface IHookHealthProbeClient
{
    Task<HookJumpResult> ProbeHealthAsync(CancellationToken cancellationToken);
}

public interface IHookShutdownClient
{
    Task<HookJumpResult> ShutdownAsync(CancellationToken cancellationToken);
}

public sealed class HookIpcClient : IHookIpcClient, IHookActiveDialogQueryClient, IHookHealthProbeClient, IHookShutdownClient, IDisposable
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DialogJumpTimeout = TimeSpan.FromSeconds(2);

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private readonly int _clientProcessId;
    private readonly string _secret;

    public HookIpcClient(string pipeName, TimeSpan connectTimeout, int? clientProcessId = null, string? secret = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("Pipe name cannot be empty.", nameof(pipeName));
        }

        if (connectTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), "Connect timeout must be nonnegative.");
        }

        _pipeName = pipeName;
        _connectTimeout = connectTimeout;
        _clientProcessId = clientProcessId ?? Environment.ProcessId;
        _secret = secret ?? string.Empty;
    }

    public async Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken)
    {
        var result = await GetActiveDialogResultAsync(cancellationToken).ConfigureAwait(false);
        return result.Dialog;
    }

    public async Task<HookActiveDialogResult> GetActiveDialogResultAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var exchange = await SendRequestAsync(
                HookIpcEnvelope.Command(new HookActiveDialogQuery()),
                cancellationToken)
            .ConfigureAwait(false);
        if (exchange.Failure is not null)
        {
            return HookActiveDialogResult.FromJumpResult(exchange.Failure);
        }

        var replyEnvelope = exchange.Envelope;
        if (replyEnvelope is null)
        {
            return new HookActiveDialogResult(
                HookJumpStatus.Failed,
                "Hook host returned no active dialog response.",
                null);
        }

        if (string.Equals(replyEnvelope.MessageType, "ActiveDialog", StringComparison.Ordinal)
            && replyEnvelope.Payload is HookActiveDialogEvent activeDialog)
        {
            var dialog = CreateDialogContext(activeDialog);
            return dialog is null
                ? new HookActiveDialogResult(
                    HookJumpStatus.Failed,
                    "Hook host returned an invalid active dialog payload.",
                    null)
                : HookActiveDialogResult.Active(dialog);
        }

        if (string.Equals(replyEnvelope.MessageType, "CommandReply", StringComparison.Ordinal)
            && replyEnvelope.Payload is HookCommandReply reply)
        {
            if (!Enum.TryParse<HookJumpStatus>(reply.Status, ignoreCase: true, out var status))
            {
                return new HookActiveDialogResult(
                    HookJumpStatus.Failed,
                    $"Hook host returned unknown status '{reply.Status}'.",
                    null);
            }

            if (status == HookJumpStatus.NoActiveDialog)
            {
                return HookActiveDialogResult.NoActiveDialog(reply.Message);
            }

            return new HookActiveDialogResult(
                status == HookJumpStatus.Success ? HookJumpStatus.Failed : status,
                reply.Message,
                null);
        }

        return new HookActiveDialogResult(
            HookJumpStatus.Failed,
            $"Hook host returned unexpected message type '{replyEnvelope.MessageType}'.",
            null);
    }

    public Task<HookJumpResult> ProbeHealthAsync(CancellationToken cancellationToken) =>
        SendCommandAsync(HookIpcEnvelope.Command(new HookHealthProbe()), cancellationToken);

    public Task<HookJumpResult> ShutdownAsync(CancellationToken cancellationToken) =>
        SendCommandAsync(HookIpcEnvelope.Command(new HookShutdownCommand()), cancellationToken);

    public async Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var envelope = HookIpcEnvelope.Command(new HookJumpCommand(dialogId, folderPath, DialogJumpTimeout));
        var result = await SendCommandAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (result.Status != HookJumpStatus.UnsupportedDialog ||
            !TryNavigateStandardFileDialog(dialogId, folderPath))
        {
            return result;
        }

        return new HookJumpResult(
            HookJumpStatus.Success,
            "Standard file-dialog address bar accepted the folder jump.");
    }

    private static bool TryNavigateStandardFileDialog(string dialogId, string folderPath)
    {
        if (!TryParseDialogWindow(dialogId, out var dialogWindow) || !IsWindow(dialogWindow))
        {
            return false;
        }

        try
        {
            // Asking UI Automation for the address toolbar initializes the lazy
            // breadcrumb provider used by modern Windows file dialogs.
            var root = AutomationElement.FromHandle(dialogWindow);
            var addressToolbar = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "1001"));
            if (addressToolbar is not null)
            {
                ActivateAddressToolbar(new IntPtr(addressToolbar.Current.NativeWindowHandle));
                addressToolbar.SetFocus();
            }
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            // The Win32 edit may already exist even when its UIA provider is unavailable.
        }

        var deadline = Environment.TickCount64 + 500;
        var addressEdit = FindAddressEdit(dialogWindow);
        while (addressEdit == IntPtr.Zero && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(20);
            addressEdit = FindAddressEdit(dialogWindow);
        }
        if (addressEdit == IntPtr.Zero)
        {
            return false;
        }

        var widePath = new StringBuilder(folderPath);
        if (SendMessage(addressEdit, WmSetText, IntPtr.Zero, widePath) == IntPtr.Zero)
        {
            return false;
        }

        _ = SendMessage(addressEdit, WmKeyDown, new IntPtr(VkReturn), IntPtr.Zero);
        _ = SendMessage(addressEdit, WmKeyUp, new IntPtr(VkReturn), IntPtr.Zero);
        WaitForAddressNavigation(dialogWindow, folderPath);
        RestoreDialogInputFocus(dialogWindow);
        return true;
    }

    private static void WaitForAddressNavigation(IntPtr dialogWindow, string folderPath)
    {
        var expectedLeaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath));
        var deadline = Environment.TickCount64 + 1_500;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                var toolbar = AutomationElement.FromHandle(dialogWindow).FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "1001"));
                if (toolbar is not null &&
                    toolbar.Current.Name.Contains(expectedLeaf, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (Exception exception) when (
                exception is ElementNotAvailableException or InvalidOperationException or COMException)
            {
            }
            Thread.Sleep(25);
        }
    }

    private static void RestoreDialogInputFocus(IntPtr dialogWindow)
    {
        var deadline = Environment.TickCount64 + 750;
        while (Environment.TickCount64 < deadline)
        {
            var input = FindDialogInput(dialogWindow);
            if (input != IntPtr.Zero)
            {
                try
                {
                    AutomationElement.FromHandle(input).SetFocus();
                    return;
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or InvalidOperationException or COMException)
                {
                }
            }
            Thread.Sleep(20);
        }
    }

    private static IntPtr FindDialogInput(IntPtr dialogWindow)
    {
        var fileNameEdit = IntPtr.Zero;
        var folderNameEdit = IntPtr.Zero;
        _ = EnumChildWindows(
            dialogWindow,
            (window, _) =>
            {
                var controlId = GetDlgCtrlID(window);
                if (controlId == FileNameEditControlId)
                {
                    fileNameEdit = window;
                }
                else if (controlId == FolderNameEditControlId)
                {
                    folderNameEdit = window;
                }
                return fileNameEdit == IntPtr.Zero;
            },
            IntPtr.Zero);
        return fileNameEdit != IntPtr.Zero ? fileNameEdit : folderNameEdit;
    }

    private static void ActivateAddressToolbar(IntPtr toolbar)
    {
        if (toolbar == IntPtr.Zero || !GetClientRect(toolbar, out var bounds))
        {
            return;
        }

        var x = Math.Max(1, bounds.Right - 8);
        var y = Math.Max(1, bounds.Height / 2);
        var point = new IntPtr((y << 16) | (x & 0xffff));
        _ = SendMessage(toolbar, WmLeftButtonDown, new IntPtr(1), point);
        _ = SendMessage(toolbar, WmLeftButtonUp, IntPtr.Zero, point);
        _ = SendMessage(toolbar, WmLeftButtonDoubleClick, new IntPtr(1), point);
        _ = SendMessage(toolbar, WmLeftButtonUp, IntPtr.Zero, point);
    }

    private static bool TryParseDialogWindow(string dialogId, out IntPtr window)
    {
        window = IntPtr.Zero;
        var parts = dialogId.Split(':');
        return parts.Length == 3 &&
            ulong.TryParse(parts[2], out var value) &&
            value != 0 &&
            (window = new IntPtr(unchecked((long)value))) != IntPtr.Zero;
    }

    private static IntPtr FindAddressEdit(IntPtr dialogWindow)
    {
        var found = IntPtr.Zero;
        _ = EnumChildWindows(
            dialogWindow,
            (window, _) =>
            {
                if (GetDlgCtrlID(window) != AddressBarEditControlId)
                {
                    return true;
                }

                var className = new StringBuilder(32);
                _ = GetClassName(window, className, className.Capacity);
                if (!string.Equals(className.ToString(), "Edit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                found = window;
                return false;
            },
            IntPtr.Zero);
        return found;
    }

    private const int AddressBarEditControlId = 41477;
    private const int FileNameEditControlId = 1148;
    private const int FolderNameEditControlId = 1152;
    private const uint WmSetText = 0x000C;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const int VkReturn = 0x0D;

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
        public int Height => Bottom - Top;
    }

    private async Task<HookJumpResult> SendCommandAsync(HookIpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var exchange = await SendRequestAsync(envelope, cancellationToken).ConfigureAwait(false);
        if (exchange.Failure is not null)
        {
            return exchange.Failure;
        }

        var replyEnvelope = exchange.Envelope;
        if (replyEnvelope is null
            || replyEnvelope.Payload is not HookCommandReply reply
            || !string.Equals(replyEnvelope.MessageType, "CommandReply", StringComparison.Ordinal))
        {
            return new HookJumpResult(
                HookJumpStatus.Failed,
                $"Hook host returned unexpected message type '{replyEnvelope?.MessageType ?? "<none>"}'.");
        }

        return Enum.TryParse<HookJumpStatus>(reply.Status, ignoreCase: true, out var status)
            ? new HookJumpResult(status, reply.Message)
            : new HookJumpResult(HookJumpStatus.Failed, $"Hook host returned unknown status '{reply.Status}'.");
    }

    private async Task<HookIpcExchange> SendRequestAsync(HookIpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var connectionEstablished = false;

        using var timeoutCancellation = new CancellationTokenSource();
        timeoutCancellation.CancelAfter(_connectTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);

        try
        {
            var request = HookIpcSerializer.Serialize(envelope with
            {
                ClientProcessId = _clientProcessId,
                Secret = _secret
            });

            cancellationToken.ThrowIfCancellationRequested();

            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(linkedCancellation.Token).ConfigureAwait(false);
            connectionEstablished = true;

            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            var requestLine = PipeEncoding.GetBytes(request + "\n");
            await pipe.WriteAsync(requestLine.AsMemory(0, requestLine.Length), linkedCancellation.Token).ConfigureAwait(false);
            await pipe.FlushAsync(linkedCancellation.Token).ConfigureAwait(false);

            var response = await reader.ReadLineAsync(linkedCancellation.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response))
            {
                return HookIpcExchange.FromFailure(
                    new HookJumpResult(HookJumpStatus.Failed, "Hook host returned an empty response."));
            }

            var replyEnvelope = HookIpcSerializer.Deserialize(response);
            return HookIpcExchange.FromEnvelope(replyEnvelope);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !connectionEstablished)
        {
            return HookIpcExchange.FromFailure(
                new HookJumpResult(HookJumpStatus.HostUnavailable, "Hook host pipe was unavailable before the connect timeout."));
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return HookIpcExchange.FromFailure(
                new HookJumpResult(HookJumpStatus.Timeout, "Hook host did not respond before timeout."));
        }
        catch (IOException exception)
        {
            return HookIpcExchange.FromFailure(new HookJumpResult(HookJumpStatus.HostUnavailable, exception.Message));
        }
        catch (TimeoutException exception)
        {
            return HookIpcExchange.FromFailure(new HookJumpResult(HookJumpStatus.HostUnavailable, exception.Message));
        }
        catch (JsonException exception)
        {
            return HookIpcExchange.FromFailure(
                new HookJumpResult(HookJumpStatus.Failed, $"Hook host returned invalid JSON: {exception.Message}"));
        }
        catch (InvalidOperationException exception)
        {
            return HookIpcExchange.FromFailure(
                new HookJumpResult(HookJumpStatus.Failed, $"Hook host returned an invalid response: {exception.Message}"));
        }
    }

    private static HookDialogContext? CreateDialogContext(HookActiveDialogEvent activeDialog)
    {
        if (!TryParseArchitecture(activeDialog.Architecture, out var architecture))
        {
            return null;
        }

        try
        {
            return new HookDialogContext(
                activeDialog.DialogId,
                new IntPtr(activeDialog.WindowHandle),
                activeDialog.ProcessId,
                activeDialog.ThreadId,
                architecture,
                activeDialog.ProcessName,
                activeDialog.ClassName,
                activeDialog.Title,
                DateTimeOffset.UtcNow,
                activeDialog.FirefoxFileDialogUtility,
                activeDialog.PreloadConfirmedBeforeDialog);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool TryParseArchitecture(string value, out HookArchitecture architecture)
    {
        if (string.Equals(value, "x64", StringComparison.OrdinalIgnoreCase))
        {
            architecture = HookArchitecture.X64;
            return true;
        }

        if (string.Equals(value, "x86", StringComparison.OrdinalIgnoreCase))
        {
            architecture = HookArchitecture.X86;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out architecture);
    }

    public void Dispose()
    {
    }

    private sealed record HookIpcExchange(HookIpcEnvelope? Envelope, HookJumpResult? Failure)
    {
        public static HookIpcExchange FromEnvelope(HookIpcEnvelope envelope) => new(envelope, null);

        public static HookIpcExchange FromFailure(HookJumpResult result) => new(null, result);
    }
}
