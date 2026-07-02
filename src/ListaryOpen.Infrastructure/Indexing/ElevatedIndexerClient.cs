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

    public ElevatedIndexerClient()
        : this(ResolveDefaultHelperPath)
    {
    }

    public ElevatedIndexerClient(string helperPath)
        : this(() => helperPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
    }

    private ElevatedIndexerClient(Func<string?> resolveHelperPath)
    {
        _resolveHelperPath = resolveHelperPath ?? throw new ArgumentNullException(nameof(resolveHelperPath));
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

    private static async Task RunHelperAsync(string helperPath, string rootPath, CancellationToken cancellationToken)
    {
        using var process = new Process
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

        try
        {
            if (!process.Start())
            {
                Trace.TraceError("Failed to start elevated indexer helper '{0}'.", helperPath);
                return;
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("Failed to start elevated indexer helper '{0}': {1}", helperPath, exception);
            return;
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

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
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Elevated indexer helper '{0}' failed while running: {1}", helperPath, exception);
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Cancellation is already being reported to the caller.
        }
    }
}
