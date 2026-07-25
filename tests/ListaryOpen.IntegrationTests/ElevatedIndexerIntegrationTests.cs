using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.IntegrationTests;

/// <summary>
/// Real elevated-helper checks that intentionally avoid Consent UI automation:
/// the test process must already be running as administrator so the helper
/// launches redirected (no runas) while still opening NTFS volumes.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ElevatedIndexerIntegrationCollection
{
    public const string Name = "Elevated indexer integration";
}

[Collection(ElevatedIndexerIntegrationCollection.Name)]
public sealed class ElevatedIndexerIntegrationTests
{
    private const int HighIntegrityRid = 0x3000;

    [Fact]
    [Trait("Category", "ElevatedIndexerIntegration")]
    public async Task ElevatedHelperQueriesJournalStateForSystemVolume()
    {
        RequireElevatedRunner();
        var helperPath = RequirePackagedHelperPath();
        using var client = new ElevatedIndexerClient(helperPath, isProcessElevated: () => true);

        Assert.True(client.IsAvailable, "Elevated helper bundle must be available beside the package.");

        var state = await client.QueryJournalStateAsync(new IndexRoot(@"C:\"), CancellationToken.None);

        Assert.NotNull(state);
        Assert.True(state!.UsnJournalId > 0, "USN journal id should be non-zero on a real NTFS system volume.");
        Assert.True(state.NextUsn >= state.LowestValidUsn);
    }

    [Fact]
    [Trait("Category", "ElevatedIndexerIntegration")]
    public async Task ElevatedHelperScansWithoutAbortingOnProtectedSystemFiles()
    {
        RequireElevatedRunner();
        var helperPath = RequirePackagedHelperPath();
        using var client = new ElevatedIndexerClient(helperPath, isProcessElevated: () => true);

        // Take only a bounded sample so the suite stays practical on large volumes.
        const int sampleSize = 64;
        var records = new List<FileRecord>(sampleSize);
        await foreach (var record in client.ScanNtfsAsync(new IndexRoot(@"C:\"), CancellationToken.None))
        {
            records.Add(record);
            if (records.Count >= sampleSize)
            {
                break;
            }
        }

        Assert.True(
            records.Count >= sampleSize,
            "Elevated NTFS scan should stream real records without aborting on protected system files " +
            "(for example WMI ETW realtime logs).");
        Assert.All(records, record => Assert.False(string.IsNullOrWhiteSpace(record.FullPath)));
    }

    [Fact]
    [Trait("Category", "ElevatedIndexerIntegration")]
    public async Task ElevatedClientRunsJournalAndScanInRedirectedModeWithoutUacWorker()
    {
        RequireElevatedRunner();
        var helperPath = RequirePackagedHelperPath();

        // Parent elevated => Redirected one-shot helpers (no runas sticky worker needed).
        using var client = new ElevatedIndexerClient(helperPath, isProcessElevated: () => true);
        Assert.True(client.IsAvailable);
        Assert.False(client.UacElevationEnabled);

        var state = await client.QueryJournalStateAsync(new IndexRoot(@"C:\"), CancellationToken.None);
        Assert.NotNull(state);

        FileRecord? first = null;
        await foreach (var record in client.ScanNtfsAsync(new IndexRoot(@"C:\"), CancellationToken.None))
        {
            first = record;
            break;
        }

        Assert.NotNull(first);
        Assert.False(string.IsNullOrWhiteSpace(first!.FullPath));
    }

    [Fact]
    [Trait("Category", "ElevatedIndexerIntegration")]
    public async Task ElevatedHelperProcessWritesJournalStateFileWithExitCodeZero()
    {
        RequireElevatedRunner();
        var helperPath = RequirePackagedHelperPath();
        var helperDirectory = Path.GetDirectoryName(helperPath)!;
        var tmp = Path.Combine(helperDirectory, "data", "tmp");
        Directory.CreateDirectory(tmp);

        var statePath = Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var errorPath = Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".err");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = helperDirectory
            };
            startInfo.ArgumentList.Add("journal-state-to-file");
            startInfo.ArgumentList.Add(@"C:\");
            startInfo.ArgumentList.Add(statePath);
            startInfo.ArgumentList.Add(errorPath);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process!.WaitForExitAsync(timeout.Token);

            var error = File.Exists(errorPath) ? await File.ReadAllTextAsync(errorPath) : string.Empty;
            Assert.True(
                process.ExitCode == 0,
                $"Expected exit 0 from elevated journal helper, got {process.ExitCode}. stderr: {error}");
            Assert.True(File.Exists(statePath), "Journal state output file was not created.");

            await using var stream = File.OpenRead(statePath);
            using var document = await JsonDocument.ParseAsync(stream);
            Assert.True(document.RootElement.TryGetProperty("usnJournalId", out _)
                || document.RootElement.TryGetProperty("UsnJournalId", out _)
                || document.RootElement.EnumerateObject().Any());
        }
        finally
        {
            TryDelete(statePath);
            TryDelete(errorPath);
        }
    }

    private static void RequireElevatedRunner()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        Assert.True(
            principal.IsInRole(WindowsBuiltInRole.Administrator),
            "ElevatedIndexerIntegration requires an already-elevated test process. " +
            "Start PowerShell/Visual Studio as administrator and run " +
            "`dotnet test --filter Category=ElevatedIndexerIntegration`. " +
            "Tests intentionally fail instead of skipping so CI cannot silently drop this coverage.");
    }

    private static string RequirePackagedHelperPath()
    {
        var packageDirectory = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
        Assert.False(
            string.IsNullOrWhiteSpace(packageDirectory),
            "LISTARYOPEN_PACKAGE_DIR must identify the published package under test.");

        var helperPath = Path.Combine(packageDirectory, "ListaryOpen.Indexer.Elevated.exe");
        Assert.True(
            File.Exists(helperPath),
            $"Packaged elevated helper is missing: {helperPath}");
        Assert.True(
            File.Exists(Path.Combine(packageDirectory, "ListaryOpen.Indexer.Elevated.dll")),
            "Packaged elevated helper bundle is incomplete (dll missing).");
        Assert.True(
            File.Exists(Path.Combine(packageDirectory, "ListaryOpen.Indexer.Elevated.deps.json")),
            "Packaged elevated helper bundle is incomplete (deps missing).");
        Assert.True(
            File.Exists(Path.Combine(packageDirectory, "ListaryOpen.Indexer.Elevated.runtimeconfig.json")),
            "Packaged elevated helper bundle is incomplete (runtimeconfig missing).");
        return helperPath;
    }

    private static void TryDelete(string path)
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
}
