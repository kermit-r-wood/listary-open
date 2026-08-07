using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ListaryOpen.Infrastructure.Indexing;

/// <summary>
/// Holds one elevated indexer helper process for the app session so journal,
/// scan, and catch-up commands share a single UAC consent.
/// </summary>
internal sealed class ElevatedIndexerUacSession : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);
    // The app has already canceled the active indexing run. Give an idle worker
    // one short chance to consume "exit", then terminate a scan promptly so the
    // elevated helper cannot outlive a fast parent shutdown.
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly string _helperPath;
    private readonly Func<string, string, ProcessStartInfo> _createWorkerStartInfo;
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    public ElevatedIndexerUacSession(
        string helperPath,
        Func<string, string, ProcessStartInfo>? createWorkerStartInfo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        _helperPath = helperPath;
        _createWorkerStartInfo = createWorkerStartInfo
            ?? ElevatedIndexerProcessStartInfoFactory.CreateUacWorker;
    }

    public async Task RunCommandAsync(
        string command,
        string rootPath,
        string outputPath,
        string errorPath,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

            var payload = command switch
            {
                "scan-to-file" => JsonSerializer.Serialize(
                    new
                    {
                        cmd = "scan-to-file",
                        root = rootPath,
                        output = outputPath,
                        error = errorPath
                    },
                    JsonOptions),
                "journal-state-to-file" => JsonSerializer.Serialize(
                    new
                    {
                        cmd = "journal-state-to-file",
                        root = rootPath,
                        output = outputPath,
                        error = errorPath
                    },
                    JsonOptions),
                "read-journal-to-file" => JsonSerializer.Serialize(
                    new
                    {
                        cmd = "read-journal-to-file",
                        root = rootPath,
                        output = outputPath,
                        error = errorPath,
                        expectedUsnJournalId = expectedUsnJournalId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        startUsn = startUsn.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        endUsn = endUsn.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    },
                    JsonOptions),
                _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported worker command.")
            };

            StreamWriter writer;
            StreamReader reader;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                writer = _writer ?? throw new InvalidOperationException("Elevated indexer worker is not connected.");
                reader = _reader ?? throw new InvalidOperationException("Elevated indexer worker is not connected.");
            }

            await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            var responseLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                throw new ElevatedIndexerException("Elevated indexer worker closed the pipe unexpectedly.");
            }

            WorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<WorkerResponse>(responseLine, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new ElevatedIndexerException(
                    "Elevated indexer worker returned an invalid response.",
                    exception);
            }

            if (response is null)
            {
                throw new ElevatedIndexerException("Elevated indexer worker returned an empty response.");
            }

            if (response.ExitCode == 0)
            {
                return;
            }

            var diagnostic = string.IsNullOrWhiteSpace(response.Error)
                ? await ReadFileIfExistsAsync(errorPath, cancellationToken).ConfigureAwait(false)
                : response.Error;
            throw new ElevatedIndexerException(
                $"Elevated indexer helper '{_helperPath}' exited with code {response.ExitCode}. stderr: {TrimDiagnostic(diagnostic)}");
        }
        finally
        {
            _commandGate.Release();
        }
    }

    /// <summary>
    /// Test hook: force-kill the sticky worker so the next command exercises recovery.
    /// </summary>
    internal void KillWorkerForTests()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DiscardCurrentWorkerUnlocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            if (_writer is not null)
            {
                try
                {
                    var exitPayload = JsonSerializer.Serialize(new { cmd = "exit" }, JsonOptions);
                    _writer.WriteLine(exitPayload);
                    _writer.Flush();
                }
                catch
                {
                }
            }
        }
        finally
        {
            TryDispose(ref _writer);
            TryDispose(ref _reader);
            TryDispose(ref _pipe);
            TryStopProcess();
            _commandGate.Dispose();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_writer is not null && _process is { HasExited: false })
            {
                return;
            }

            // Drop a dead worker (or a half-open pipe) so the next start owns a
            // clean session. Recovery may elevate again; normal multi-command use
            // never hits this path while the worker is healthy.
            DiscardCurrentWorkerUnlocked();
        }

        await StartCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        var pipeName = "listary-open-indexer-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        Process? process = null;
        try
        {
            var startInfo = _createWorkerStartInfo(_helperPath, pipeName);
            process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    throw new ElevatedIndexerLaunchCanceledException(
                        "Elevated indexer helper launch was canceled or declined.");
                }
            }
            catch (Exception exception) when (IsUacCanceled(exception))
            {
                process.Dispose();
                throw new ElevatedIndexerLaunchCanceledException(
                    "Elevated indexer helper launch was canceled by the user.",
                    exception);
            }
            catch (Exception exception) when (exception is not ElevatedIndexerLaunchCanceledException
                                             and not OperationCanceledException)
            {
                process.Dispose();
                throw new ElevatedIndexerException(
                    $"Failed to start elevated indexer helper '{_helperPath}': {exception.Message}",
                    exception);
            }

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                process.Dispose();
                pipe.Dispose();
                throw new ElevatedIndexerException(
                    "Timed out waiting for the elevated indexer worker to connect.");
            }

            var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true
            };

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Another command may have recovered while we were connecting; keep
                // the healthy session already published and drop this spare worker.
                if (_writer is not null && _process is { HasExited: false })
                {
                    TryDispose(ref writer);
                    TryDispose(ref reader);
                    TryDispose(ref pipe);
                    TryKill(process);
                    process.Dispose();
                    process = null;
                    pipe = null;
                    return;
                }

                DiscardCurrentWorkerUnlocked();
                _pipe = pipe;
                _process = process;
                _reader = reader;
                _writer = writer;
                process = null;
                pipe = null;
            }
        }
        finally
        {
            pipe?.Dispose();
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }
        }
    }

    private void DiscardCurrentWorkerUnlocked()
    {
        TryDispose(ref _writer);
        TryDispose(ref _reader);
        TryDispose(ref _pipe);

        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void TryStopProcess()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                if (!process.WaitForExit(ShutdownTimeout))
                {
                    TryKill(process);
                    process.WaitForExit(ShutdownTimeout);
                }
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static bool IsUacCanceled(Exception exception)
    {
        const int errorCancelled = 1223;
        return exception is System.ComponentModel.Win32Exception { NativeErrorCode: errorCancelled };
    }

    private static async Task<string> ReadFileIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    private static string TrimDiagnostic(string value)
    {
        const int maxLength = 1024;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static void TryDispose<T>(ref T? value) where T : class, IDisposable
    {
        try
        {
            value?.Dispose();
        }
        catch
        {
        }
        finally
        {
            value = null;
        }
    }

    private sealed record WorkerResponse(
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("error")] string? Error);
}
