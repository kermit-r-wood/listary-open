using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Search.NameTable;

namespace ListaryOpen.SearchBenchmark;

/// <summary>
/// Read-only migration benchmark for a legacy LOSN v1 JSON payload.  The legacy
/// watermark is intentionally ignored: a v1 file cannot prove multi-volume
/// coverage.  Records are streamed in bounded batches into the compact engine.
/// </summary>
internal static class NameTableLegacySnapshotBenchmarkRunner
{
    private const uint LegacyMagic = 0x4E534F4C;
    private const int LegacyVersion = 1;
    private const int LegacyHeaderSize = 44;
    private const int BatchSize = 2_000;
    private static readonly byte[] RecordsPrefix = Encoding.UTF8.GetBytes("\"Records\":[");

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseOptions(args, out var options))
        {
            PrintUsage();
            return 2;
        }

        await using var index = new NameTableSearchIndex();
        var empty = StabilizeAndCaptureMemory();
        var stopwatch = Stopwatch.StartNew();
        var import = await ReadRecordsIntoAsync(
                options.SnapshotPath,
                index,
                options.MaxRecords,
                cancellationToken)
            .ConfigureAwait(false);
        var optimizeStopwatch = Stopwatch.StartNew();
        index.Engine.Optimize();
        optimizeStopwatch.Stop();
        stopwatch.Stop();

        var loaded = StabilizeAndCaptureMemory();
        var stats = index.Engine.GetMemoryStats();
        long? v2SnapshotBytes = null;
        if (!string.IsNullOrWhiteSpace(options.V2SnapshotPath))
        {
            var v2Path = Path.GetFullPath(options.V2SnapshotPath);
            var directory = Path.GetDirectoryName(v2Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await new LosnSnapshotStore()
                .SaveAsync(v2Path, index.Engine, cancellationToken)
                .ConfigureAwait(false);
            v2SnapshotBytes = new FileInfo(v2Path).Length;
        }

        var everything = FindEverythingIndexProcess();
        var privateDelta = Math.Max(0, loaded.PrivateBytes - empty.PrivateBytes);
        var report = new LegacySnapshotBenchmarkReport
        {
            SourceSnapshot = Path.GetFullPath(options.SnapshotPath),
            Records = import.RecordCount,
            BuildSeconds = stopwatch.Elapsed.TotalSeconds,
            ImportSeconds = import.TotalSeconds,
            UpsertSeconds = import.UpsertSeconds,
            OptimizeSeconds = optimizeStopwatch.Elapsed.TotalSeconds,
            RecordsPerSecond = import.RecordCount / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001),
            Empty = empty,
            Loaded = loaded,
            PrivateBytesDelta = privateDelta,
            PrivateBytesDeltaPerRecord = import.RecordCount == 0 ? 0 : (double)privateDelta / import.RecordCount,
            NameTableMemory = stats,
            V2SnapshotPath = string.IsNullOrWhiteSpace(options.V2SnapshotPath)
                ? null
                : Path.GetFullPath(options.V2SnapshotPath),
            V2SnapshotBytes = v2SnapshotBytes,
            Everything = everything,
            CompactToEverythingPrivateRatio = everything is null || everything.PrivateBytes == 0
                ? null
                : (double)stats.EstimatedRetainedBytes / everything.PrivateBytes,
            ProcessDeltaToEverythingPrivateRatio = everything is null || everything.PrivateBytes == 0
                ? null
                : (double)privateDelta / everything.PrivateBytes,
            Note = "Legacy records were imported read-only; the v1 single-volume watermark was not trusted or persisted."
        };

        WriteConsole(report);
        if (!string.IsNullOrWhiteSpace(options.JsonPath))
        {
            var jsonPath = Path.GetFullPath(options.JsonPath);
            var directory = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                    jsonPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    cancellationToken)
                .ConfigureAwait(false);
            Console.WriteLine($"JSON: {jsonPath}");
        }

        return 0;
    }

    private static async Task<ImportResult> ReadRecordsIntoAsync(
        string snapshotPath,
        NameTableSearchIndex index,
        int? maxRecords,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            snapshotPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        var header = new byte[LegacyHeaderSize];
        file.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (magic != LegacyMagic || version != LegacyVersion || payloadLength < 0
            || file.Length != LegacyHeaderSize + (long)payloadLength)
        {
            throw new InvalidDataException("Legacy LOSN v1 header or payload length is invalid.");
        }

        var expectedHash = header[12..LegacyHeaderSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var readBuffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var objectBuffer = new ArrayBufferWriter<byte>(1024);
            var batch = new List<FileRecord>(BatchSize);
            var remaining = payloadLength;
            var prefixIndex = 0;
            var recordsStarted = false;
            var recordsComplete = false;
            var objectStarted = false;
            var inString = false;
            var escaped = false;
            var objectDepth = 0;
            var recordCount = 0;
            var upsertSeconds = 0d;
            var importStopwatch = Stopwatch.StartNew();

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = Math.Min(readBuffer.Length, remaining);
                var read = file.Read(readBuffer, 0, requested);
                if (read == 0)
                {
                    throw new EndOfStreamException("Legacy LOSN payload ended early.");
                }

                remaining -= read;
                hash.AppendData(readBuffer, 0, read);
                if (recordsComplete)
                {
                    continue;
                }

                for (var indexInBuffer = 0; indexInBuffer < read; indexInBuffer++)
                {
                    var value = readBuffer[indexInBuffer];
                    if (!recordsStarted)
                    {
                        if (value == RecordsPrefix[prefixIndex])
                        {
                            prefixIndex++;
                            if (prefixIndex == RecordsPrefix.Length)
                            {
                                recordsStarted = true;
                            }
                        }
                        else
                        {
                            prefixIndex = value == RecordsPrefix[0] ? 1 : 0;
                        }

                        continue;
                    }

                    if (!objectStarted)
                    {
                        if (value == (byte)']')
                        {
                            recordsComplete = true;
                            break;
                        }

                        if (value != (byte)'{')
                        {
                            continue;
                        }

                        objectBuffer.Clear();
                        AppendByte(objectBuffer, value);
                        objectStarted = true;
                        objectDepth = 1;
                        inString = false;
                        escaped = false;
                        continue;
                    }

                    AppendByte(objectBuffer, value);
                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (value == (byte)'\\')
                        {
                            escaped = true;
                        }
                        else if (value == (byte)'"')
                        {
                            inString = false;
                        }

                        continue;
                    }

                    if (value == (byte)'"')
                    {
                        inString = true;
                    }
                    else if (value == (byte)'{')
                    {
                        objectDepth++;
                    }
                    else if (value == (byte)'}')
                    {
                        objectDepth--;
                        if (objectDepth == 0)
                        {
                            var legacy = JsonSerializer.Deserialize<LegacyFileRecord>(objectBuffer.WrittenSpan)
                                ?? throw new InvalidDataException("Legacy LOSN contains a null record.");
                            batch.Add(ToFileRecord(legacy));
                            recordCount++;
                            objectStarted = false;
                            if (batch.Count == BatchSize)
                            {
                                var upsertStarted = Stopwatch.GetTimestamp();
                                await index.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);
                                upsertSeconds += Stopwatch.GetElapsedTime(upsertStarted).TotalSeconds;
                                batch.Clear();
                                if (recordCount % 100_000 == 0)
                                {
                                    var process = Process.GetCurrentProcess();
                                    process.Refresh();
                                    Console.WriteLine(
                                        $"import_progress records={recordCount:N0} " +
                                        $"elapsed={importStopwatch.Elapsed.TotalSeconds:F1}s " +
                                        $"upsert={upsertSeconds:F1}s " +
                                        $"private_mib={process.PrivateMemorySize64 / 1024d / 1024d:F1}");
                                }
                            }

                            if (maxRecords is not null && recordCount >= maxRecords.Value)
                            {
                                recordsComplete = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (!recordsStarted || !recordsComplete || objectStarted)
            {
                throw new InvalidDataException("Legacy LOSN Records array is incomplete.");
            }

            if (batch.Count > 0)
            {
                var upsertStarted = Stopwatch.GetTimestamp();
                await index.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);
                upsertSeconds += Stopwatch.GetElapsedTime(upsertStarted).TotalSeconds;
            }

            var actualHash = hash.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                throw new InvalidDataException("Legacy LOSN payload hash does not match its header.");
            }

            importStopwatch.Stop();
            return new ImportResult(recordCount, importStopwatch.Elapsed.TotalSeconds, upsertSeconds);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static FileRecord ToFileRecord(LegacyFileRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.FullPath) || record.SizeBytes < 0)
        {
            throw new InvalidDataException("Legacy LOSN contains an invalid file record.");
        }

        return FileRecord.CreateFromNormalizedPath(
            record.FullPath,
            record.IsDirectory,
            record.SizeBytes,
            DateTimeOffset.FromUnixTimeMilliseconds(record.LastWriteUnixMs),
            record.FileReferenceNumber);
    }

    private static void AppendByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
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

    private static ExternalProcessSnapshot? FindEverythingIndexProcess()
    {
        return Process.GetProcessesByName("everything")
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
    }

    private static bool TryParseOptions(string[] args, out Options options)
    {
        string? snapshotPath = null;
        string? jsonPath = null;
        string? v2SnapshotPath = null;
        int? maxRecords = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (index + 1 >= args.Length)
            {
                options = default!;
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
                case "--write-v2":
                    v2SnapshotPath = value;
                    break;
                case "--max-records" when int.TryParse(value, out var parsedMaxRecords) && parsedMaxRecords > 0:
                    maxRecords = parsedMaxRecords;
                    break;
                default:
                    options = default!;
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
        {
            options = default!;
            return false;
        }

        options = new Options(snapshotPath, jsonPath, v2SnapshotPath, maxRecords);
        return true;
    }

    private static void WriteConsole(LegacySnapshotBenchmarkReport report)
    {
        Console.WriteLine("=== NameTable real-corpus memory benchmark ===");
        Console.WriteLine(
            $"records={report.Records:N0} build={report.BuildSeconds:F2}s " +
            $"import={report.ImportSeconds:F2}s upsert={report.UpsertSeconds:F2}s " +
            $"optimize={report.OptimizeSeconds:F2}s records_per_second={report.RecordsPerSecond:N0}");
        Console.WriteLine(
            $"compact={FormatBytes(report.NameTableMemory.EstimatedRetainedBytes)} " +
            $"compact_bytes_per_record={report.NameTableMemory.BytesPerLiveRecord:F2} " +
            $"unique_names={report.NameTableMemory.UniqueNames:N0}");
        Console.WriteLine(
            $"process_private_delta={FormatBytes(report.PrivateBytesDelta)} " +
            $"process_private_delta_per_record={report.PrivateBytesDeltaPerRecord:F2} " +
            $"working_set={FormatBytes(report.Loaded.WorkingSetBytes)}");
        if (report.Everything is not null)
        {
            Console.WriteLine(
                $"everything_pid={report.Everything.ProcessId} " +
                $"everything_private={FormatBytes(report.Everything.PrivateBytes)} " +
                $"compact/everything={report.CompactToEverythingPrivateRatio:F3} " +
                $"process_delta/everything={report.ProcessDeltaToEverythingPrivateRatio:F3}");
        }

        if (report.V2SnapshotBytes is not null)
        {
            Console.WriteLine(
                $"losn_v2={FormatBytes(report.V2SnapshotBytes.Value)} path={report.V2SnapshotPath}");
        }
    }

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

    private static void PrintUsage() => Console.Error.WriteLine(
        "Usage: nametable-v1-memory --snapshot <index.losn.tmp> [--json report.json] [--write-v2 snapshot.losn] [--max-records N]");

    private sealed record Options(
        string SnapshotPath,
        string? JsonPath,
        string? V2SnapshotPath,
        int? MaxRecords);

    private readonly record struct ImportResult(int RecordCount, double TotalSeconds, double UpsertSeconds);

    private sealed class LegacyFileRecord
    {
        public string FullPath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long SizeBytes { get; set; }
        public long LastWriteUnixMs { get; set; }
        public ulong FileReferenceNumber { get; set; }
    }

    private sealed class LegacySnapshotBenchmarkReport
    {
        public string SourceSnapshot { get; set; } = "";
        public int Records { get; set; }
        public double BuildSeconds { get; set; }
        public double ImportSeconds { get; set; }
        public double UpsertSeconds { get; set; }
        public double OptimizeSeconds { get; set; }
        public double RecordsPerSecond { get; set; }
        public ProcessMemorySnapshot Empty { get; set; } = new();
        public ProcessMemorySnapshot Loaded { get; set; } = new();
        public long PrivateBytesDelta { get; set; }
        public double PrivateBytesDeltaPerRecord { get; set; }
        public NameTableMemoryStats NameTableMemory { get; set; }
        public string? V2SnapshotPath { get; set; }
        public long? V2SnapshotBytes { get; set; }
        public ExternalProcessSnapshot? Everything { get; set; }
        public double? CompactToEverythingPrivateRatio { get; set; }
        public double? ProcessDeltaToEverythingPrivateRatio { get; set; }
        public string Note { get; set; } = "";
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
