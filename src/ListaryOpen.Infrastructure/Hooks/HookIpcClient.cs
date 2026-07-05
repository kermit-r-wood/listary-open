using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ListaryOpen.Infrastructure.Hooks;

public interface IHookIpcClient
{
    Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken);

    Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken);
}

public interface IHookHealthProbeClient
{
    Task<HookJumpResult> ProbeHealthAsync(CancellationToken cancellationToken);
}

public sealed class HookIpcClient : IHookIpcClient, IHookHealthProbeClient, IDisposable
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public HookIpcClient(string pipeName, TimeSpan connectTimeout)
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
    }

    public async Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var exchange = await SendRequestAsync(
                HookIpcEnvelope.Command(new HookActiveDialogQuery()),
                cancellationToken)
            .ConfigureAwait(false);
        if (exchange.Envelope is null)
        {
            return null;
        }

        if (string.Equals(exchange.Envelope.MessageType, "ActiveDialog", StringComparison.Ordinal)
            && exchange.Envelope.Payload is HookActiveDialogEvent activeDialog)
        {
            return CreateDialogContext(activeDialog);
        }

        if (string.Equals(exchange.Envelope.MessageType, "CommandReply", StringComparison.Ordinal)
            && exchange.Envelope.Payload is HookCommandReply reply
            && string.Equals(reply.Status, HookJumpStatus.NoActiveDialog.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return null;
    }

    public Task<HookJumpResult> ProbeHealthAsync(CancellationToken cancellationToken) =>
        SendCommandAsync(HookIpcEnvelope.Command(new HookHealthProbe()), cancellationToken);

    public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var envelope = HookIpcEnvelope.Command(new HookJumpCommand(dialogId, folderPath, TimeSpan.FromMilliseconds(750)));

        return SendCommandAsync(envelope, cancellationToken);
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
            var request = HookIpcSerializer.Serialize(envelope);

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
                DateTimeOffset.UtcNow);
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
