using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using ListaryOpen.Infrastructure.Search.NameTable;

namespace ListaryOpen.SearchBenchmark;

/// <summary>
/// Measures a cold-process LOSN v2 bind/load independently from full-build
/// allocation history. This is the normal steady-state startup path.
/// </summary>
internal static class NameTableSnapshotLoadBenchmarkRunner
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseOptions(args, out var snapshotPath, out var jsonPath))
        {
            Console.Error.WriteLine(
                "Usage: nametable-v2-load --snapshot <index.losn> [--json report.json]");
            return 2;
        }

        var empty = StabilizeAndCaptureMemory();
        var engine = new NameTableEngine();
        var stopwatch = Stopwatch.StartNew();
        await new LosnSnapshotStore()
            .LoadIntoAsync(snapshotPath, engine, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();
        var loaded = StabilizeAndCaptureMemory();
        var stats = engine.GetMemoryStats();
        var everything = FindEverythingIndexProcess();
        var privateDelta = Math.Max(0, loaded.PrivateBytes - empty.PrivateBytes);
        var report = new SnapshotLoadReport
        {
            SnapshotPath = Path.GetFullPath(snapshotPath),
            SnapshotBytes = new FileInfo(snapshotPath).Length,
            LoadSeconds = stopwatch.Elapsed.TotalSeconds,
            Empty = empty,
            Loaded = loaded,
            PrivateBytesDelta = privateDelta,
            PrivateBytesDeltaPerRecord = stats.LiveRecords == 0
                ? 0
                : (double)privateDelta / stats.LiveRecords,
            NameTableMemory = stats,
            Everything = everything,
            CompactToEverythingPrivateRatio = everything is null || everything.PrivateBytes == 0
                ? null
                : (double)stats.EstimatedRetainedBytes / everything.PrivateBytes,
            ProcessDeltaToEverythingPrivateRatio = everything is null || everything.PrivateBytes == 0
                ? null
                : (double)privateDelta / everything.PrivateBytes
        };

        Console.WriteLine("=== NameTable LOSN v2 cold-load benchmark ===");
        Console.WriteLine(
            $"records={stats.LiveRecords:N0} load={report.LoadSeconds:F2}s " +
            $"snapshot={FormatBytes(report.SnapshotBytes)}");
        Console.WriteLine(
            $"compact={FormatBytes(stats.EstimatedRetainedBytes)} " +
            $"compact_bytes_per_record={stats.BytesPerLiveRecord:F2}");
        Console.WriteLine(
            $"process_private_delta={FormatBytes(privateDelta)} " +
            $"process_private_delta_per_record={report.PrivateBytesDeltaPerRecord:F2} " +
            $"working_set={FormatBytes(loaded.WorkingSetBytes)}");
        if (everything is not null)
        {
            Console.WriteLine(
                $"everything_pid={everything.ProcessId} " +
                $"everything_private={FormatBytes(everything.PrivateBytes)} " +
                $"compact/everything={report.CompactToEverythingPrivateRatio:F3} " +
                $"process_delta/everything={report.ProcessDeltaToEverythingPrivateRatio:F3}");
        }

        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            var fullJsonPath = Path.GetFullPath(jsonPath);
            var directory = Path.GetDirectoryName(fullJsonPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                    fullJsonPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"JSON: {fullJsonPath}");
        }

        return 0;
    }

    private static bool TryParseOptions(string[] args, out string snapshotPath, out string? jsonPath)
    {
        snapshotPath = "";
        jsonPath = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (index + 1 >= args.Length)
            {
                return false;
            }

            var option = args[index];
            var value = args[++index];
            switch (option)
            {
                case "--snapshot":
                    snapshotPath = value;
                    break;
                case "--json":
                    jsonPath = value;
                    break;
                default:
                    return false;
            }
        }

        return !string.IsNullOrWhiteSpace(snapshotPath) && File.Exists(snapshotPath);
    }

    private static ProcessMemorySnapshot StabilizeAndCaptureMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        return new ProcessMemorySnapshot
        {
            PrivateBytes = process.PrivateMemorySize64,
            WorkingSetBytes = process.WorkingSet64,
            ManagedHeapBytes = gc.HeapSizeBytes,
            ManagedCommittedBytes = gc.TotalCommittedBytes,
            ManagedFragmentedBytes = gc.FragmentedBytes
        };
    }

    private static ExternalProcessSnapshot? FindEverythingIndexProcess() =>
        Process.GetProcessesByName("everything")
            .Select(process =>
            {
                try
                {
                    process.Refresh();
                    return new ExternalProcessSnapshot
                    {
                        ProcessId = process.Id,
                        PrivateBytes = process.PrivateMemorySize64,
                        WorkingSetBytes = process.WorkingSet64
                    };
                }
                finally
                {
                    process.Dispose();
                }
            })
            .OrderByDescending(process => process.PrivateBytes)
            .FirstOrDefault();

    private static string FormatBytes(long value)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB"];
        var scaled = (double)value;
        var suffix = 0;
        while (scaled >= 1024 && suffix < suffixes.Length - 1)
        {
            scaled /= 1024;
            suffix++;
        }

        return $"{scaled:F2}{suffixes[suffix]}";
    }

    private sealed class SnapshotLoadReport
    {
        public string SnapshotPath { get; set; } = "";
        public long SnapshotBytes { get; set; }
        public double LoadSeconds { get; set; }
        public ProcessMemorySnapshot Empty { get; set; } = new();
        public ProcessMemorySnapshot Loaded { get; set; } = new();
        public long PrivateBytesDelta { get; set; }
        public double PrivateBytesDeltaPerRecord { get; set; }
        public NameTableMemoryStats NameTableMemory { get; set; }
        public ExternalProcessSnapshot? Everything { get; set; }
        public double? CompactToEverythingPrivateRatio { get; set; }
        public double? ProcessDeltaToEverythingPrivateRatio { get; set; }
    }

    private sealed class ProcessMemorySnapshot
    {
        public long PrivateBytes { get; set; }
        public long WorkingSetBytes { get; set; }
        public long ManagedHeapBytes { get; set; }
        public long ManagedCommittedBytes { get; set; }
        public long ManagedFragmentedBytes { get; set; }
    }

    private sealed class ExternalProcessSnapshot
    {
        public int ProcessId { get; set; }
        public long PrivateBytes { get; set; }
        public long WorkingSetBytes { get; set; }
    }
}
