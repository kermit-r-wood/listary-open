using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace ListaryOpen.App;

/// <summary>
/// An intentionally narrow control surface for black-box tests of the packaged app.
/// It can only report readiness/native-capture evidence, permit Windows-tagged
/// injected input, and request a normal application shutdown. It never invokes a
/// product feature directly.
/// </summary>
internal sealed class E2eControlServer : IDisposable
{
    private const int MaximumRequestLength = 256;
    private readonly E2eControlOptions _options;
    private readonly Func<bool, bool> _setInjectedInputPermission;
    private readonly Action _requestShutdown;
    private readonly Func<uint, E2eDialogPrecaptureProof?> _getDialogPrecaptureProof;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly object _gate = new();
    private NamedPipeServerStream? _listener;
    private Task? _runTask;
    private bool _disposed;

    public E2eControlServer(
        E2eControlOptions options,
        Func<bool, bool> setInjectedInputPermission,
        Action requestShutdown,
        Func<uint, E2eDialogPrecaptureProof?>? getDialogPrecaptureProof = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _setInjectedInputPermission = setInjectedInputPermission ?? throw new ArgumentNullException(nameof(setInjectedInputPermission));
        _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));
        _getDialogPrecaptureProof = getDialogPrecaptureProof ?? (_ => null);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runTask is not null)
            {
                return;
            }

            // Create the first listener synchronously so an explicitly requested
            // E2E launch fails immediately if its private endpoint is unavailable.
            _listener = CreateListener();
            _runTask = RunAsync(_listener, _cancellation.Token);
        }
    }

    public void Dispose()
    {
        NamedPipeServerStream? listener;
        Task? runTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cancellation.Cancel();
            listener = _listener;
            _listener = null;
            runTask = _runTask;
        }

        listener?.Dispose();
        if (runTask is null)
        {
            _cancellation.Dispose();
        }
        else
        {
            _ = DisposeCancellationWhenStoppedAsync(runTask);
        }
    }

    private async Task DisposeCancellationWhenStoppedAsync(Task runTask)
    {
        try
        {
            await runTask.ConfigureAwait(false);
        }
        catch
        {
            // Disposal must observe a background endpoint failure without
            // surfacing it on WPF's shutdown path.
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private async Task RunAsync(NamedPipeServerStream firstListener, CancellationToken cancellationToken)
    {
        var listener = firstListener;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await listener.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await ServeConnectionAsync(listener, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    // A client may disconnect at any byte boundary. The next
                    // connection still needs a fresh pipe instance.
                }
                finally
                {
                    listener.Dispose();
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                listener = CreateListener();
                lock (_gate)
                {
                    if (_disposed)
                    {
                        listener.Dispose();
                        break;
                    }

                    _listener = listener;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_listener, listener))
                {
                    _listener = null;
                }
            }
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 256,
            leaveOpen: true);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 256,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                string? request;
                try
                {
                    request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DecoderFallbackException)
                {
                    await writer.WriteLineAsync("InvalidRequest").ConfigureAwait(false);
                    return;
                }

                if (request is null)
                {
                    return;
                }

                if (request.Length > MaximumRequestLength)
                {
                    await writer.WriteLineAsync("InvalidRequest").ConfigureAwait(false);
                    return;
                }

                var response = HandleRequest(request, out var shutdownRequested);
                await writer.WriteLineAsync(response).ConfigureAwait(false);
                if (shutdownRequested)
                {
                    _requestShutdown();
                    return;
                }
            }
        }
        finally
        {
            // Input injection is a lease held by the authenticated controller,
            // not a sticky process-wide test mode. A crashed/disconnected test
            // runner therefore fails closed immediately.
            try
            {
                _setInjectedInputPermission(false);
            }
            catch
            {
                // Revocation runs during teardown and must not prevent pipe cleanup.
            }
        }
    }

    private string HandleRequest(string request, out bool shutdownRequested)
    {
        shutdownRequested = false;
        var separator = request.IndexOf(' ');
        if (separator <= 0 || separator == request.Length - 1)
        {
            return "InvalidRequest";
        }

        var nonce = request.AsSpan(0, separator);
        if (!E2eControlOptions.IsValidNonce(nonce) || !NonceEquals(nonce, _options.Nonce))
        {
            return "Unauthorized";
        }

        var command = request[(separator + 1)..];
        const string precaptureProofCommand = "DialogPrecaptureProof ";
        if (command.StartsWith(precaptureProofCommand, StringComparison.Ordinal) &&
            uint.TryParse(
                command.AsSpan(precaptureProofCommand.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var processId))
        {
            var proof = _getDialogPrecaptureProof(processId);
            return proof is null
                ? "Unavailable"
                : $"PrecaptureProof {proof.ProcessId} {proof.FirefoxFileDialogUtility} {proof.PreloadConfirmedBeforeDialog}";
        }
        switch (command)
        {
            case "Ready":
                return $"Ready {Environment.ProcessId}";
            case "AllowInjectedInput":
                return _setInjectedInputPermission(true) ? "Ok" : "Unavailable";
            case "Shutdown":
                shutdownRequested = true;
                return "Ok";
            default:
                return "UnsupportedCommand";
        }
    }

    private NamedPipeServerStream CreateListener() => new(
        _options.PipeName,
        PipeDirection.InOut,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        inBufferSize: 512,
        outBufferSize: 512);

    private static bool NonceEquals(ReadOnlySpan<char> candidate, string expected)
    {
        // Both values have already passed the strict 64-hex-character check.
        // Compare decoded bytes so the command authentication check is not an
        // early-exit string comparison and remains case insensitive.
        var candidateBytes = Convert.FromHexString(candidate.ToString());
        var expectedBytes = Convert.FromHexString(expected);
        return CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
    }
}

internal sealed record E2eDialogPrecaptureProof(
    uint ProcessId,
    bool FirefoxFileDialogUtility,
    bool PreloadConfirmedBeforeDialog);

internal sealed record E2eControlOptions(string Nonce)
{
    internal const string ArgumentPrefix = "--listary-e2e-control=";
    internal const int NonceByteLength = 32;
    internal const int NonceHexLength = NonceByteLength * 2;

    public string PipeName => $"ListaryOpen.E2E.{Nonce}";

    public static E2eControlOptions? TryParse(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count != 1)
        {
            return null;
        }

        var argument = arguments[0];
        if (!argument.StartsWith(ArgumentPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var nonce = argument.AsSpan(ArgumentPrefix.Length);
        return IsValidNonce(nonce)
            ? new E2eControlOptions(nonce.ToString().ToLowerInvariant())
            : null;
    }

    internal static bool IsValidNonce(ReadOnlySpan<char> nonce)
    {
        if (nonce.Length != NonceHexLength)
        {
            return false;
        }

        foreach (var character in nonce)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}
