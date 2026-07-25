using System.Diagnostics;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.IndexingBenchmark;

internal static class IndexingBenchmarkRunner
{
    public static async Task<BenchmarkReport> RunAsync(
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var maximumRecords = Math.Max(options.Records, Math.Max(options.PipelineRecords, options.SqliteRecords));
        var records = CreateRecords(maximumRecords);
        var scratchDirectory = Path.Combine(
            Path.GetTempPath(),
            "listary-open-indexing-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDirectory);

        try
        {
            var helperPath = await BenchmarkScenarios.CreateHelperBundleAsync(
                scratchDirectory,
                cancellationToken);
            var runs = new List<BenchmarkRun>();

            for (var warmup = 0; warmup < options.Warmups; warmup++)
            {
                Console.Error.WriteLine($"Warmup {warmup + 1}/{options.Warmups}...");
                await RunRoundAsync(
                    options,
                    records,
                    helperPath,
                    scratchDirectory,
                    repeat: -1,
                    capture: false,
                    runs,
                    cancellationToken);
            }

            for (var repeat = 0; repeat < options.Repeats; repeat++)
            {
                Console.Error.WriteLine($"Measured round {repeat + 1}/{options.Repeats}...");
                await RunRoundAsync(
                    options,
                    records,
                    helperPath,
                    scratchDirectory,
                    repeat,
                    capture: true,
                    runs,
                    cancellationToken);
            }

            return BenchmarkReportWriter.Create(options, runs);
        }
        finally
        {
            TryDeleteDirectory(scratchDirectory);
        }
    }

    internal static async Task<BenchmarkRun> MeasureAsync(
        string scenario,
        string variant,
        int repeat,
        int operations,
        Func<Stopwatch, Task<BenchmarkOutcome>> action)
    {
        ForceGarbageCollection();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        var outcome = await action(stopwatch).ConfigureAwait(false);
        stopwatch.Stop();
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        if (outcome.ProcessedOperations != operations)
        {
            throw new InvalidDataException(
                $"{scenario}/{variant} processed {outcome.ProcessedOperations:N0}; expected {operations:N0}.");
        }

        var elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        return new BenchmarkRun(
            scenario,
            variant,
            repeat,
            operations,
            elapsedMilliseconds,
            operations / Math.Max(stopwatch.Elapsed.TotalSeconds, double.Epsilon),
            allocatedBytes,
            allocatedBytes / (double)operations,
            outcome.FirstRecordMilliseconds,
            outcome.SemanticChecksum);
    }

    private static async Task RunRoundAsync(
        BenchmarkOptions options,
        IReadOnlyList<FileRecord> records,
        string helperPath,
        string scratchDirectory,
        int repeat,
        bool capture,
        List<BenchmarkRun> runs,
        CancellationToken cancellationToken)
    {
        var reverse = repeat >= 0 && repeat % 2 == 1;
        var variants = reverse ? new[] { false, true } : new[] { true, false };

        foreach (var baseline in variants)
        {
            AddIfCaptured(
                await BenchmarkScenarios.MeasureEnumerationAsync(
                    records.Take(options.Records).ToArray(),
                    baseline,
                    repeat,
                    cancellationToken),
                capture,
                runs);
        }

        foreach (var baseline in variants)
        {
            AddIfCaptured(
                await BenchmarkScenarios.MeasureWriterAsync(
                    records.Take(options.Records).ToArray(),
                    baseline,
                    repeat,
                    scratchDirectory,
                    cancellationToken),
                capture,
                runs);
        }

        foreach (var baseline in variants)
        {
            AddIfCaptured(
                await BenchmarkScenarios.MeasureTransportCodecAsync(
                    records.Take(options.Records).ToArray(),
                    baseline,
                    repeat,
                    cancellationToken),
                capture,
                runs);
        }

        foreach (var baseline in variants)
        {
            AddIfCaptured(
                await BenchmarkScenarios.MeasureHelperPipelineAsync(
                    records.Take(options.PipelineRecords).ToArray(),
                    baseline,
                    repeat,
                    helperPath,
                    scratchDirectory,
                    options.ProducerDelayMilliseconds,
                    cancellationToken),
                capture,
                runs);
        }

        foreach (var baseline in variants)
        {
            AddIfCaptured(
                await BenchmarkScenarios.MeasureSqliteUpsertAsync(
                    records.Take(options.SqliteRecords).ToArray(),
                    baseline,
                    repeat,
                    scratchDirectory,
                    cancellationToken),
                capture,
                runs);
        }

        foreach (var baseline in variants)
        {
            AddIfCaptured(
                await BenchmarkScenarios.MeasureProgressDispatchAsync(
                    options.ProgressBatches,
                    baseline,
                    repeat),
                capture,
                runs);
        }
    }

    private static FileRecord[] CreateRecords(int count)
    {
        var records = new FileRecord[count];
        var timestamp = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < records.Length; index++)
        {
            records[index] = FileRecord.Create(
                $@"C:\benchmark\folder-{index % 128:D3}\file-{index:D8}-项目.txt",
                isDirectory: false,
                sizeBytes: index + 1L,
                lastWriteTime: timestamp.AddSeconds(index));
        }

        return records;
    }

    private static void AddIfCaptured(BenchmarkRun run, bool capture, List<BenchmarkRun> runs)
    {
        if (capture)
        {
            runs.Add(run);
        }
    }

    private static void ForceGarbageCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
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

internal sealed record BenchmarkOutcome(
    int ProcessedOperations,
    long SemanticChecksum,
    double? FirstRecordMilliseconds = null,
    string? FirstPath = null,
    string? LastPath = null);
