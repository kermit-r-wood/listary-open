using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly Func<bool> _isProcessElevated;

    public ElevatedIndexerClient()
        : this(ResolveDefaultHelperPath, CreateProcess, IsCurrentProcessElevated)
    {
    }

    public ElevatedIndexerClient(string helperPath)
        : this(() => helperPath, CreateProcess, IsCurrentProcessElevated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    internal ElevatedIndexerClient(string helperPath, Func<bool> isProcessElevated)
        : this(() => helperPath, CreateProcess, isProcessElevated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    internal ElevatedIndexerClient(string helperPath, Func<string, string, IElevatedIndexerProcess> createProcess)
        : this(() => helperPath, createProcess, () => true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    internal ElevatedIndexerClient(
        string helperPath,
        Func<string, string, IElevatedIndexerProcess> createProcess,
        Func<bool> isProcessElevated)
        : this(() => helperPath, createProcess, isProcessElevated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    private ElevatedIndexerClient(
        Func<string?> resolveHelperPath,
        Func<string, string, IElevatedIndexerProcess> createProcess,
        Func<bool> isProcessElevated)
    {
        _resolveHelperPath = resolveHelperPath ?? throw new ArgumentNullException(nameof(resolveHelperPath));
        _createProcess = createProcess ?? throw new ArgumentNullException(nameof(createProcess));
        _isProcessElevated = isProcessElevated ?? throw new ArgumentNullException(nameof(isProcessElevated));
    }

    public bool IsAvailable
    {
        get
        {
            var helperPath = _resolveHelperPath();
            return !string.IsNullOrWhiteSpace(helperPath) &&
                File.Exists(helperPath) &&
                IsProcessElevated();
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

        var records = await RunHelperAsync(helperPath!, root.Path, cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    private async Task<IReadOnlyList<FileRecord>> RunHelperAsync(
        string helperPath,
        string rootPath,
        CancellationToken cancellationToken)
    {
        using var process = _createProcess(helperPath, rootPath);

        try
        {
            if (!process.Start())
            {
                var message = $"Failed to start elevated indexer helper '{helperPath}'.";
                Trace.TraceError(message);
                throw new ElevatedIndexerException(message);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ElevatedIndexerException)
        {
            var message = $"Failed to start elevated indexer helper '{helperPath}': {exception.Message}";
            Trace.TraceError("Failed to start elevated indexer helper '{0}': {1}", helperPath, exception);
            throw new ElevatedIndexerException(message, exception);
        }

        Task<string>? stdoutTask = null;
        Task<string>? stderrTask = null;

        try
        {
            stdoutTask = process.ReadStandardOutputToEndAsync(cancellationToken);
            stderrTask = process.ReadStandardErrorToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var diagnostic = TrimDiagnostic(stderr);
                var message = $"Elevated indexer helper '{helperPath}' exited with code {process.ExitCode}. stderr: {diagnostic}";
                Trace.TraceError(message);
                throw new ElevatedIndexerException(message);
            }

            return ParseOutput(stdout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupCanceledProcessAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (ElevatedIndexerException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Elevated indexer helper '{helperPath}' failed while running: {exception.Message}";
            Trace.TraceError("Elevated indexer helper '{0}' failed while running: {1}", helperPath, exception);
            throw new ElevatedIndexerException(message, exception);
        }
    }

    private static IReadOnlyList<FileRecord> ParseOutput(string stdout)
    {
        var records = new List<FileRecord>();
        using var reader = new StringReader(stdout);
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            records.Add(ParseRecord(line, lineNumber));
        }

        return records;
    }

    private static FileRecord ParseRecord(string line, int lineNumber)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ElevatedIndexerRecordDto>(line);
            if (dto is null)
            {
                throw new InvalidDataException("Record was empty.");
            }

            ValidateRequiredFields(dto);
            return FileRecord.Create(
                dto.FullPath!,
                dto.IsDirectory.GetValueOrDefault(),
                dto.SizeBytes.GetValueOrDefault(),
                dto.LastWriteTime.GetValueOrDefault());
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidDataException
                                          or ArgumentException
                                          or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(
                $"Invalid elevated indexer output on line {lineNumber}.",
                exception);
        }
    }

    private static void ValidateRequiredFields(ElevatedIndexerRecordDto dto)
    {
        if (dto.FullPath is null
            || dto.IsDirectory is null
            || dto.SizeBytes is null
            || dto.LastWriteTime is null)
        {
            throw new InvalidDataException("Record is missing one or more required fields.");
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

    private bool IsProcessElevated()
    {
        try
        {
            return _isProcessElevated();
        }
        catch (Exception exception) when (exception is SystemException or SecurityException)
        {
            Trace.TraceWarning("Unable to determine elevated indexer privilege state: {0}", exception);
            return false;
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
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

    private sealed class ElevatedIndexerRecordDto
    {
        [JsonPropertyName("fullPath")]
        public string? FullPath { get; init; }

        [JsonPropertyName("isDirectory")]
        public bool? IsDirectory { get; init; }

        [JsonPropertyName("sizeBytes")]
        public long? SizeBytes { get; init; }

        [JsonPropertyName("lastWriteTime")]
        public DateTimeOffset? LastWriteTime { get; init; }
    }

    private sealed class ElevatedIndexerException : InvalidOperationException
    {
        public ElevatedIndexerException(string message)
            : base(message)
        {
        }

        public ElevatedIndexerException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
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
