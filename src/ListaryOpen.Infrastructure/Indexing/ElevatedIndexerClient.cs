using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.AppData;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Indexing;

public interface IElevatedIndexerClient
{
    bool IsAvailable { get; }

    IAsyncEnumerable<FileRecord> ScanNtfsAsync(IndexRoot root, CancellationToken cancellationToken);

    Task<UsnJournalState?> QueryJournalStateAsync(IndexRoot root, CancellationToken cancellationToken);

    IAsyncEnumerable<UsnJournalChange> ReadJournalChangesAsync(
        IndexRoot root,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        CancellationToken cancellationToken);
}

internal interface IElevatedIndexerProcess : IDisposable
{
    int ExitCode { get; }

    bool HasExited { get; }

    bool Start();

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

    public Task<UsnJournalState?> QueryJournalStateAsync(IndexRoot root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<UsnJournalState?>(null);
    }

    public IAsyncEnumerable<UsnJournalChange> ReadJournalChangesAsync(
        IndexRoot root,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Elevated indexer helper is not available.");
    }
}

public sealed class ElevatedIndexerClient : IElevatedIndexerClient
{
    private const string HelperExecutableName = "ListaryOpen.Indexer.Elevated.exe";
    private const string HelperAssemblyName = "ListaryOpen.Indexer.Elevated.dll";
    private const string HelperDepsName = "ListaryOpen.Indexer.Elevated.deps.json";
    private const string HelperRuntimeConfigName = "ListaryOpen.Indexer.Elevated.runtimeconfig.json";

    private readonly Func<string?> _resolveHelperPath;
    private readonly Func<HelperFileCommand, string, string, ulong, long, long, string, string, IElevatedIndexerProcess> _createFileProcess;
    private readonly Func<HelperFileCommand, string, string, ulong, long, long, string, string, IElevatedIndexerProcess> _createElevatedProcess;
    private readonly Func<bool> _isProcessElevated;
    private readonly Func<bool>? _isUacElevationEnabledOverride;
    private readonly string _indexerTempDirectory;
    private bool _uacElevationEnabled;

    public ElevatedIndexerClient()
        : this(
            ResolveDefaultHelperPath,
            CreateFileProcess,
            CreateElevatedProcess,
            IsCurrentProcessElevated,
            null,
            AppDataPaths.CreateDefault().IndexerTempDirectory)
    {
    }

    public ElevatedIndexerClient(string helperPath)
        : this(
            () => helperPath,
            CreateFileProcess,
            CreateElevatedProcess,
            IsCurrentProcessElevated,
            null,
            AppDataPaths.CreateDefault().IndexerTempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    internal ElevatedIndexerClient(string helperPath, Func<bool> isProcessElevated)
        : this(
            () => helperPath,
            CreateFileProcess,
            CreateElevatedProcess,
            isProcessElevated,
            null,
            AppDataPaths.CreateDefault().IndexerTempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    internal ElevatedIndexerClient(
        string helperPath,
        Func<string, string, string, string, IElevatedIndexerProcess> createFileProcess)
        : this(
            () => helperPath,
            WrapFileProcess(createFileProcess),
            CreateElevatedProcess,
            () => true,
            null,
            AppDataPaths.CreateDefault().IndexerTempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    internal ElevatedIndexerClient(
        string helperPath,
        Func<string, string, string, string, IElevatedIndexerProcess> createFileProcess,
        Func<bool> isProcessElevated)
        : this(
            () => helperPath,
            WrapFileProcess(createFileProcess),
            CreateElevatedProcess,
            isProcessElevated,
            null,
            AppDataPaths.CreateDefault().IndexerTempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    internal ElevatedIndexerClient(
        string helperPath,
        Func<string, string, string, string, IElevatedIndexerProcess> createFileProcess,
        Func<bool> isProcessElevated,
        Func<bool> isUacElevationEnabled,
        Func<string, string, string, string, IElevatedIndexerProcess> createElevatedProcess,
        string? indexerTempDirectory = null)
        : this(
            () => helperPath,
            WrapFileProcess(createFileProcess),
            WrapFileProcess(createElevatedProcess),
            isProcessElevated,
            isUacElevationEnabled,
            indexerTempDirectory ?? AppDataPaths.CreateDefault().IndexerTempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    private ElevatedIndexerClient(
        Func<string?> resolveHelperPath,
        Func<HelperFileCommand, string, string, ulong, long, long, string, string, IElevatedIndexerProcess> createFileProcess,
        Func<HelperFileCommand, string, string, ulong, long, long, string, string, IElevatedIndexerProcess> createElevatedProcess,
        Func<bool> isProcessElevated,
        Func<bool>? isUacElevationEnabled,
        string indexerTempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexerTempDirectory);

        _resolveHelperPath = resolveHelperPath ?? throw new ArgumentNullException(nameof(resolveHelperPath));
        _createFileProcess = createFileProcess ?? throw new ArgumentNullException(nameof(createFileProcess));
        _createElevatedProcess = createElevatedProcess ?? throw new ArgumentNullException(nameof(createElevatedProcess));
        _isProcessElevated = isProcessElevated ?? throw new ArgumentNullException(nameof(isProcessElevated));
        _isUacElevationEnabledOverride = isUacElevationEnabled;
        _indexerTempDirectory = Path.GetFullPath(indexerTempDirectory);
    }

    public bool UacElevationEnabled => IsUacElevationEnabled();

    public bool IsAvailable
    {
        get
        {
            return TryResolveAvailableHelperPath(out _, out _);
        }
    }

    public void EnableUacElevation()
    {
        Volatile.Write(ref _uacElevationEnabled, true);
    }

    public async IAsyncEnumerable<FileRecord> ScanNtfsAsync(
        IndexRoot root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveAvailableHelperPath(out var helperPath, out var launchMode))
        {
            Trace.TraceWarning("Elevated indexer helper is not available at '{0}'.", helperPath ?? "<unresolved>");
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        await foreach (var record in RunHelperToFileAsync(helperPath!, root.Path, launchMode, cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    public async Task<UsnJournalState?> QueryJournalStateAsync(
        IndexRoot root,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveAvailableHelperPath(out var helperPath, out var launchMode))
        {
            Trace.TraceWarning("Elevated indexer helper is not available at '{0}'.", helperPath ?? "<unresolved>");
            return null;
        }

        var outputPath = CreateTempIndexerFilePath(".jsonl");
        var errorPath = CreateTempIndexerFilePath(".err");
        try
        {
            await WriteHelperOutputFileAsync(
                    HelperFileCommand.JournalState,
                    helperPath!,
                    root.Path,
                    expectedUsnJournalId: 0,
                    startUsn: 0,
                    endUsn: 0,
                    outputPath,
                    errorPath,
                    launchMode,
                    cancellationToken)
                .ConfigureAwait(false);

            return await ParseJournalStateFileIfExistsAsync(outputPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(outputPath);
            TryDeleteFile(errorPath);
        }
    }

    public async IAsyncEnumerable<UsnJournalChange> ReadJournalChangesAsync(
        IndexRoot root,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryResolveAvailableHelperPath(out var helperPath, out var launchMode))
        {
            Trace.TraceWarning("Elevated indexer helper is not available at '{0}'.", helperPath ?? "<unresolved>");
            throw new InvalidOperationException("Elevated indexer helper is not available.");
        }

        var outputPath = CreateTempIndexerFilePath(".jsonl");
        var errorPath = CreateTempIndexerFilePath(".err");
        try
        {
            await WriteHelperOutputFileAsync(
                    HelperFileCommand.JournalChanges,
                    helperPath!,
                    root.Path,
                    expectedUsnJournalId,
                    startUsn,
                    endUsn,
                    outputPath,
                    errorPath,
                    launchMode,
                    cancellationToken)
                .ConfigureAwait(false);

            await foreach (var change in ParseJournalChangesFileIfExistsAsync(outputPath, cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            TryDeleteFile(outputPath);
            TryDeleteFile(errorPath);
        }
    }

    private async IAsyncEnumerable<FileRecord> RunHelperToFileAsync(
        string helperPath,
        string rootPath,
        HelperLaunchMode launchMode,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var outputPath = CreateTempIndexerFilePath(".jsonl");
        var errorPath = CreateTempIndexerFilePath(".err");

        try
        {
            await WriteHelperOutputFileAsync(
                    HelperFileCommand.Scan,
                    helperPath,
                    rootPath,
                    expectedUsnJournalId: 0,
                    startUsn: 0,
                    endUsn: 0,
                    outputPath,
                    errorPath,
                    launchMode,
                    cancellationToken).ConfigureAwait(false);

            await foreach (var record in ParseOutputFileIfExistsAsync(outputPath, cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }
        finally
        {
            TryDeleteFile(outputPath);
            TryDeleteFile(errorPath);
        }
    }

    private async Task WriteHelperOutputFileAsync(
        HelperFileCommand command,
        string helperPath,
        string rootPath,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        string outputPath,
        string errorPath,
        HelperLaunchMode launchMode,
        CancellationToken cancellationToken)
    {
        using var process = launchMode == HelperLaunchMode.UacFile
            ? _createElevatedProcess(command, helperPath, rootPath, expectedUsnJournalId, startUsn, endUsn, outputPath, errorPath)
            : _createFileProcess(command, helperPath, rootPath, expectedUsnJournalId, startUsn, endUsn, outputPath, errorPath);

        try
        {
            if (!process.Start())
            {
                if (launchMode == HelperLaunchMode.UacFile)
                {
                    Trace.TraceInformation("Elevated indexer helper launch was canceled or declined.");
                    throw new ElevatedIndexerLaunchCanceledException("Elevated indexer helper launch was canceled or declined.");
                }

                var message = $"Failed to start elevated indexer helper '{helperPath}'.";
                Trace.TraceError(message);
                throw new ElevatedIndexerException(message);
            }
        }
        catch (Exception exception) when (launchMode == HelperLaunchMode.UacFile && IsUacCanceled(exception))
        {
            Trace.TraceInformation("Elevated indexer helper launch was canceled by the user.");
            throw new ElevatedIndexerLaunchCanceledException("Elevated indexer helper launch was canceled by the user.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                         and not ElevatedIndexerException
                                         and not ElevatedIndexerLaunchCanceledException)
        {
            var message = $"Failed to start elevated indexer helper '{helperPath}': {exception.Message}";
            Trace.TraceError("Failed to start elevated indexer helper '{0}': {1}", helperPath, exception);
            throw new ElevatedIndexerException(message, exception);
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var diagnostic = TrimDiagnostic(await ReadFileIfExistsAsync(errorPath, cancellationToken).ConfigureAwait(false));
                var message = $"Elevated indexer helper '{helperPath}' exited with code {process.ExitCode}. stderr: {diagnostic}";
                Trace.TraceError(message);
                throw new ElevatedIndexerException(message);
            }

            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupCanceledProcessAsync(process, stdoutTask: null, stderrTask: null).ConfigureAwait(false);
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

    private static async IAsyncEnumerable<FileRecord> ParseOutputFileIfExistsAsync(
        string path,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return ParseRecord(line, lineNumber);
        }
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

    private static async Task<UsnJournalState?> ParseJournalStateFileIfExistsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<UsnJournalStateDto>(line);
            if (dto?.UsnJournalId is null || dto.LowestValidUsn is null || dto.NextUsn is null)
            {
                throw new InvalidDataException("Journal state is missing one or more required fields.");
            }

            return new UsnJournalState(
                dto.UsnJournalId.GetValueOrDefault(),
                dto.LowestValidUsn.GetValueOrDefault(),
                dto.NextUsn.GetValueOrDefault());
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException("Invalid elevated indexer journal state output.", exception);
        }
    }

    private static async IAsyncEnumerable<UsnJournalChange> ParseJournalChangesFileIfExistsAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return ParseJournalChange(line, lineNumber);
        }
    }

    private static UsnJournalChange ParseJournalChange(string line, int lineNumber)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<UsnJournalChangeDto>(line);
            if (dto?.Kind is null)
            {
                throw new InvalidDataException("Journal change is missing kind.");
            }

            return dto.Kind switch
            {
                "upsert" => UsnJournalChange.Upsert(FileRecord.Create(
                    dto.FullPath ?? throw new InvalidDataException("Upsert change is missing full path."),
                    dto.IsDirectory ?? throw new InvalidDataException("Upsert change is missing directory flag."),
                    dto.SizeBytes ?? throw new InvalidDataException("Upsert change is missing size."),
                    dto.LastWriteTime ?? throw new InvalidDataException("Upsert change is missing last write time."))),
                "delete" => UsnJournalChange.Delete(
                    dto.FullPath ?? throw new InvalidDataException("Delete change is missing full path.")),
                "fileRename" => UsnJournalChange.FileRename(
                    dto.OldFullPath ?? throw new InvalidDataException("File rename change is missing old full path."),
                    FileRecord.Create(
                        dto.FullPath ?? throw new InvalidDataException("File rename change is missing full path."),
                        dto.IsDirectory ?? throw new InvalidDataException("File rename change is missing directory flag."),
                        dto.SizeBytes ?? throw new InvalidDataException("File rename change is missing size."),
                        dto.LastWriteTime ?? throw new InvalidDataException("File rename change is missing last write time."))),
                "directoryRenameOrMove" => UsnJournalChange.DirectoryRenameOrMove(),
                _ => throw new InvalidDataException($"Unsupported journal change kind: {dto.Kind}.")
            };
        }
        catch (Exception exception) when (exception is JsonException
                                          or InvalidDataException
                                          or ArgumentException
                                          or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException(
                $"Invalid elevated indexer journal change output on line {lineNumber}.",
                exception);
        }
    }

    private static Func<HelperFileCommand, string, string, ulong, long, long, string, string, IElevatedIndexerProcess> WrapFileProcess(
        Func<string, string, string, string, IElevatedIndexerProcess> createProcess)
    {
        return (_, helperPath, rootPath, _, _, _, outputPath, errorPath) =>
            createProcess(helperPath, rootPath, outputPath, errorPath);
    }

    private static IElevatedIndexerProcess CreateFileProcess(
        HelperFileCommand command,
        string helperPath,
        string rootPath,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        string outputPath,
        string errorPath)
    {
        var process = new Process
        {
            StartInfo = command switch
            {
                HelperFileCommand.Scan => ElevatedIndexerProcessStartInfoFactory.CreateRedirectedFile(
                    helperPath,
                    rootPath,
                    outputPath,
                    errorPath),
                HelperFileCommand.JournalState => ElevatedIndexerProcessStartInfoFactory.CreateRedirectedJournalStateFile(
                    helperPath,
                    rootPath,
                    outputPath,
                    errorPath),
                HelperFileCommand.JournalChanges => ElevatedIndexerProcessStartInfoFactory.CreateRedirectedJournalChangesFile(
                    helperPath,
                    rootPath,
                    expectedUsnJournalId,
                    startUsn,
                    endUsn,
                    outputPath,
                    errorPath),
                _ => throw new NotSupportedException($"Unsupported helper command: {command}.")
            }
        };

        return new ElevatedIndexerProcess(process);
    }

    private static IElevatedIndexerProcess CreateElevatedProcess(
        HelperFileCommand command,
        string helperPath,
        string rootPath,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        string outputPath,
        string errorPath)
    {
        var process = new Process
        {
            StartInfo = command switch
            {
                HelperFileCommand.Scan => ElevatedIndexerProcessStartInfoFactory.CreateUacFile(
                    helperPath,
                    rootPath,
                    outputPath,
                    errorPath),
                HelperFileCommand.JournalState => ElevatedIndexerProcessStartInfoFactory.CreateUacJournalStateFile(
                    helperPath,
                    rootPath,
                    outputPath,
                    errorPath),
                HelperFileCommand.JournalChanges => ElevatedIndexerProcessStartInfoFactory.CreateUacJournalChangesFile(
                    helperPath,
                    rootPath,
                    expectedUsnJournalId,
                    startUsn,
                    endUsn,
                    outputPath,
                    errorPath),
                _ => throw new NotSupportedException($"Unsupported helper command: {command}.")
            }
        };

        return new ElevatedIndexerProcess(process);
    }

    private bool TryResolveAvailableHelperPath(out string? helperPath, out HelperLaunchMode launchMode)
    {
        helperPath = _resolveHelperPath();
        launchMode = HelperLaunchMode.Unavailable;
        if (string.IsNullOrWhiteSpace(helperPath) || !IsUsableHelperBundle(helperPath))
        {
            return false;
        }

        if (IsProcessElevated())
        {
            launchMode = HelperLaunchMode.Redirected;
            return true;
        }

        if (IsUacElevationEnabled())
        {
            launchMode = HelperLaunchMode.UacFile;
            return true;
        }

        return false;
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

    private bool IsUacElevationEnabled()
    {
        return _isUacElevationEnabledOverride?.Invoke() ?? Volatile.Read(ref _uacElevationEnabled);
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
        return ResolveDefaultHelperPath(AppContext.BaseDirectory);
    }

    internal static string ResolveDefaultHelperPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var besideApp = Path.Combine(baseDirectory, HelperExecutableName);
        if (IsUsableHelperBundle(besideApp))
        {
            return besideApp;
        }

        var buildOutput = ResolveRepositoryBuildOutput(baseDirectory);
        return buildOutput is not null && IsUsableHelperBundle(buildOutput) ? buildOutput : besideApp;
    }

    private static bool IsUsableHelperBundle(string helperPath)
    {
        if (!File.Exists(helperPath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(helperPath);
        return !string.IsNullOrWhiteSpace(directory) &&
            File.Exists(Path.Combine(directory, HelperAssemblyName)) &&
            File.Exists(Path.Combine(directory, HelperDepsName)) &&
            File.Exists(Path.Combine(directory, HelperRuntimeConfigName));
    }

    private static string? ResolveRepositoryBuildOutput(string baseDirectory)
    {
        if (!TryInferBuildOutputLayout(
                baseDirectory,
                out var configuration,
                out var targetFramework,
                out var runtimeIdentifier))
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
                targetFramework);
            if (!string.IsNullOrWhiteSpace(runtimeIdentifier))
            {
                candidate = Path.Combine(candidate, runtimeIdentifier);
            }

            candidate = Path.Combine(candidate, HelperExecutableName);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool TryInferBuildOutputLayout(
        string baseDirectory,
        out string configuration,
        out string targetFramework,
        out string? runtimeIdentifier)
    {
        var outputDirectory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
        targetFramework = outputDirectory.Name;
        runtimeIdentifier = null;
        var configurationDirectory = outputDirectory.Parent;

        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            runtimeIdentifier = targetFramework;
            outputDirectory = configurationDirectory ?? new DirectoryInfo(baseDirectory);
            targetFramework = outputDirectory.Name;
            configurationDirectory = outputDirectory.Parent;
        }

        configuration = configurationDirectory?.Name ?? string.Empty;
        return !string.IsNullOrWhiteSpace(configuration) &&
            !string.IsNullOrWhiteSpace(targetFramework);
    }

    private static string TrimDiagnostic(string value)
    {
        const int maxLength = 1024;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private string CreateTempIndexerFilePath(string extension)
    {
        Directory.CreateDirectory(_indexerTempDirectory);
        return Path.Combine(_indexerTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + extension);
    }

    private static async Task<string> ReadFileIfExistsAsync(string path, CancellationToken cancellationToken)
    {
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : string.Empty;
    }

    private static bool IsUacCanceled(Exception exception)
    {
        const int errorCancelled = 1223;
        return exception is Win32Exception { NativeErrorCode: errorCancelled };
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private enum HelperLaunchMode
    {
        Unavailable,
        Redirected,
        UacFile
    }

    private enum HelperFileCommand
    {
        Scan,
        JournalState,
        JournalChanges
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

    private sealed class UsnJournalStateDto
    {
        [JsonPropertyName("usnJournalId")]
        public ulong? UsnJournalId { get; init; }

        [JsonPropertyName("lowestValidUsn")]
        public long? LowestValidUsn { get; init; }

        [JsonPropertyName("nextUsn")]
        public long? NextUsn { get; init; }
    }

    private sealed class UsnJournalChangeDto
    {
        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("fullPath")]
        public string? FullPath { get; init; }

        [JsonPropertyName("oldFullPath")]
        public string? OldFullPath { get; init; }

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

internal sealed class ElevatedIndexerLaunchCanceledException : InvalidOperationException
{
    public ElevatedIndexerLaunchCanceledException(string message)
        : base(message)
    {
    }

    public ElevatedIndexerLaunchCanceledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
