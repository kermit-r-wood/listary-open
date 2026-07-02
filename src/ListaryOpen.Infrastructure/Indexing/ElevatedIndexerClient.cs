using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public interface IElevatedIndexerClient
{
    bool IsAvailable { get; }

    IAsyncEnumerable<FileRecord> ScanNtfsAsync(IndexRoot root, CancellationToken cancellationToken);
}

internal interface IElevatedIndexerProcess : IDisposable
{
    int ExitCode { get; }

    bool HasExited { get; }

    bool Start();

    Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken);

    Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken);

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill();
}

public sealed class DisabledElevatedIndexerClient : IElevatedIndexerClient
{
    public bool IsAvailable => false;

    public async IAsyncEnumerable<FileRecord> ScanNtfsAsync(
        IndexRoot root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}

public sealed class ElevatedIndexerClient : IElevatedIndexerClient
{
    private const string HelperExecutableName = "ListaryOpen.Indexer.Elevated.exe";

    private readonly Func<string?> _resolveHelperPath;
    private readonly Func<string, string, IElevatedIndexerProcess> _createProcess;

    public ElevatedIndexerClient()
        : this(ResolveDefaultHelperPath, CreateProcess)
    {
    }

    public ElevatedIndexerClient(string helperPath)
        : this(() => helperPath, CreateProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    internal ElevatedIndexerClient(string helperPath, Func<string, string, IElevatedIndexerProcess> createProcess)
        : this(() => helperPath, createProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    private ElevatedIndexerClient(
        Func<string?> resolveHelperPath,
        Func<string, string, IElevatedIndexerProcess> createProcess)
    {
        _resolveHelperPath = resolveHelperPath ?? throw new ArgumentNullException(nameof(resolveHelperPath));
        _createProcess = createProcess ?? throw new ArgumentNullException(nameof(createProcess));
    }

    public bool IsAvailable
    {
        get
        {
            var helperPath = _resolveHelperPath();
            return !string.IsNullOrWhiteSpace(helperPath) && File.Exists(helperPath);
        }
    }

    public async IAsyncEnumerable<FileRecord> ScanNtfsAsync(
        IndexRoot root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();

        var helperPath = _resolveHelperPath();
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath))
        {
            Trace.TraceWarning("Elevated indexer helper is not available at '{0}'.", helperPath ?? "<unresolved>");
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        await RunHelperAsync(helperPath!, root.Path, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private async Task RunHelperAsync(string helperPath, string rootPath, CancellationToken cancellationToken)
    {
        using var process = _createProcess(helperPath, rootPath);

        try
        {
            if (!process.Start())
            {
                Trace.TraceError("Failed to start elevated indexer helper '{0}'.", helperPath);
                return;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceError("Failed to start elevated indexer helper '{0}': {1}", helperPath, exception);
            return;
        }

        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            stdoutTask = process.ReadStandardOutputToEndAsync(cancellationToken);
            stderrTask = process.ReadStandardErrorToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                Trace.TraceError(
                    "Elevated indexer helper '{0}' exited with code {1}. stderr: {2}",
                    helperPath,
                    process.ExitCode,
                    TrimDiagnostic(stderr));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupCanceledProcessAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Elevated indexer helper '{0}' failed while running: {1}", helperPath, exception);
        }
    }

    private static IElevatedIndexerProcess CreateProcess(string helperPath, string rootPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("scan");
        process.StartInfo.ArgumentList.Add(rootPath);

        return new ElevatedIndexerProcess(process);
    }

    private static async Task CleanupCanceledProcessAsync(
        IElevatedIndexerProcess process,
        Task<string>? stdoutTask,
        Task<string>? stderrTask)
    {
        TryKill(process);
        await TryWaitForExitAsync(process).ConfigureAwait(false);
        await ObserveCanceledReadAsync(stdoutTask).ConfigureAwait(false);
        await ObserveCanceledReadAsync(stderrTask).ConfigureAwait(false);
    }

    private static async Task TryWaitForExitAsync(IElevatedIndexerProcess process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task ObserveCanceledReadAsync(Task<string>? readTask)
    {
        if (readTask is null)
        {
            return;
        }

        try
        {
            await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception)
        {
            // The caller is already observing cancellation; this await exists to observe pipe-read task faults.
        }
    }

    private static string ResolveDefaultHelperPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var besideApp = Path.Combine(baseDirectory, HelperExecutableName);
        if (File.Exists(besideApp))
        {
            return besideApp;
        }

        var buildOutput = ResolveRepositoryBuildOutput(baseDirectory);
        return buildOutput is not null && File.Exists(buildOutput) ? buildOutput : besideApp;
    }

    private static string? ResolveRepositoryBuildOutput(string baseDirectory)
    {
        var trimmedBaseDirectory = Path.TrimEndingDirectorySeparator(baseDirectory);
        var targetFramework = new DirectoryInfo(trimmedBaseDirectory).Name;
        var configuration = Directory.GetParent(trimmedBaseDirectory)?.Name;
        if (string.IsNullOrWhiteSpace(targetFramework) || string.IsNullOrWhiteSpace(configuration))
        {
            return null;
        }

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "ListaryOpen.Indexer.Elevated",
                "bin",
                configuration,
                targetFramework,
                HelperExecutableName);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string TrimDiagnostic(string value)
    {
        const int maxLength = 1024;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static void TryKill(IElevatedIndexerProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Cancellation is already being reported to the caller.
        }
        catch (Win32Exception)
        {
            // Cancellation is already being reported to the caller.
        }
        catch (NotSupportedException)
        {
            // Cancellation is already being reported to the caller.
        }
    }

    private sealed class ElevatedIndexerProcess : IElevatedIndexerProcess
    {
        private readonly Process _process;

        public ElevatedIndexerProcess(Process process)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
        }

        public int ExitCode => _process.ExitCode;

        public bool HasExited => _process.HasExited;

        public bool Start() => _process.Start();

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => _process.StandardOutput.ReadToEndAsync(cancellationToken);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => _process.StandardError.ReadToEndAsync(cancellationToken);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _process.WaitForExitAsync(cancellationToken);

        public void Kill()
        {
            _process.Kill(entireProcessTree: true);
        }

        public void Dispose()
        {
            _process.Dispose();
        }
    }
}
