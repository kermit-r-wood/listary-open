using System.Runtime.InteropServices;
using System.Text.Json;

namespace ListaryOpen.IndexingBenchmark;

internal sealed record BenchmarkRun(
    string Scenario,
    string Variant,
    int Repeat,
    int Operations,
    double ElapsedMilliseconds,
    double OperationsPerSecond,
    long AllocatedBytes,
    double AllocatedBytesPerOperation,
    double? FirstRecordMilliseconds,
    long SemanticChecksum);

internal sealed record BenchmarkSummary(
    string Scenario,
    string Variant,
    int Operations,
    double MedianElapsedMilliseconds,
    double MedianOperationsPerSecond,
    long MedianAllocatedBytes,
    double MedianAllocatedBytesPerOperation,
    double? MedianFirstRecordMilliseconds,
    long SemanticChecksum);

internal sealed record BenchmarkEnvironment(
    string Processor,
    int LogicalProcessorCount,
    string OperatingSystem,
    string Framework,
    string Architecture,
    bool ServerGarbageCollector);

internal sealed record BenchmarkReport(
    DateTimeOffset RecordedAt,
    BenchmarkEnvironment Environment,
    BenchmarkOptions Configuration,
    IReadOnlyList<BenchmarkRun> Runs,
    IReadOnlyList<BenchmarkSummary> Summaries);

internal static class BenchmarkReportWriter
{
    public static BenchmarkReport Create(BenchmarkOptions options, IReadOnlyList<BenchmarkRun> runs)
    {
        var summaries = runs
            .GroupBy(run => (run.Scenario, run.Variant))
            .Select(group =>
            {
                var orderedElapsed = group.OrderBy(run => run.ElapsedMilliseconds).ToArray();
                var orderedThroughput = group.OrderBy(run => run.OperationsPerSecond).ToArray();
                var orderedAllocations = group.OrderBy(run => run.AllocatedBytes).ToArray();
                var orderedBytesPerOperation = group.OrderBy(run => run.AllocatedBytesPerOperation).ToArray();
                var firstRecordValues = group
                    .Where(run => run.FirstRecordMilliseconds.HasValue)
                    .Select(run => run.FirstRecordMilliseconds!.Value)
                    .OrderBy(value => value)
                    .ToArray();
                var representative = orderedElapsed[orderedElapsed.Length / 2];

                return new BenchmarkSummary(
                    group.Key.Scenario,
                    group.Key.Variant,
                    representative.Operations,
                    Median(orderedElapsed.Select(run => run.ElapsedMilliseconds).ToArray()),
                    Median(orderedThroughput.Select(run => run.OperationsPerSecond).ToArray()),
                    (long)Median(orderedAllocations.Select(run => (double)run.AllocatedBytes).ToArray()),
                    Median(orderedBytesPerOperation.Select(run => run.AllocatedBytesPerOperation).ToArray()),
                    firstRecordValues.Length == 0 ? null : Median(firstRecordValues),
                    representative.SemanticChecksum);
            })
            .OrderBy(summary => summary.Scenario, StringComparer.Ordinal)
            .ThenBy(summary => summary.Variant, StringComparer.Ordinal)
            .ToArray();

        var environment = new BenchmarkEnvironment(
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            Environment.ProcessorCount,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            System.Runtime.GCSettings.IsServerGC);

        return new BenchmarkReport(DateTimeOffset.Now, environment, options, runs, summaries);
    }

    public static void WriteConsole(BenchmarkReport report)
    {
        Console.WriteLine($"Indexing pipeline A/B benchmark - {report.RecordedAt:O}");
        Console.WriteLine($"{report.Environment.Processor}; {report.Environment.Framework}; {report.Environment.OperatingSystem}");
        Console.WriteLine();
        Console.WriteLine("scenario             variant                 ops    median ms        ops/s      alloc MiB       B/op   first ms");
        Console.WriteLine(new string('-', 116));
        foreach (var summary in report.Summaries)
        {
            var firstRecord = summary.MedianFirstRecordMilliseconds?.ToString("N2") ?? "-";
            Console.WriteLine(
                $"{summary.Scenario,-20} {summary.Variant,-20} {summary.Operations,8:N0} " +
                $"{summary.MedianElapsedMilliseconds,12:N2} {summary.MedianOperationsPerSecond,12:N0} " +
                $"{summary.MedianAllocatedBytes / 1024d / 1024d,14:N2} {summary.MedianAllocatedBytesPerOperation,10:N1} " +
                $"{firstRecord,10}");
        }

        Console.WriteLine();
        Console.WriteLine("All variants passed count/checksum semantic guards.");
    }

    public static async Task WriteJsonAsync(
        BenchmarkReport report,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(
            stream,
            report,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
        Console.WriteLine($"Raw JSON: {fullPath}");
    }

    private static double Median(double[] sortedValues)
    {
        if (sortedValues.Length == 0)
        {
            throw new InvalidOperationException("Cannot calculate the median of an empty sequence.");
        }

        var middle = sortedValues.Length / 2;
        return sortedValues.Length % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2d
            : sortedValues[middle];
    }
}
