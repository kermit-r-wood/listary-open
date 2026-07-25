using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Indexer.Elevated;
using ListaryOpen.Infrastructure.Indexing;
using ListaryOpen.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.IndexingBenchmark;

internal static class BenchmarkScenarios
{
    private const string UpsertSql = """
        insert into files (
            full_path,
            path_key,
            name,
            parent_path,
            search_text,
            is_directory,
            size_bytes,
            last_write_time,
            index_generation
        )
        values (
            $full_path,
            $path_key,
            $name,
            $parent_path,
            $search_text,
            $is_directory,
            $size_bytes,
            $last_write_time,
            $index_generation
        )
        on conflict(path_key) do update set
            full_path = excluded.full_path,
            name = excluded.name,
            parent_path = excluded.parent_path,
            search_text = excluded.search_text,
            is_directory = excluded.is_directory,
            size_bytes = excluded.size_bytes,
            last_write_time = excluded.last_write_time,
            index_generation = excluded.index_generation;
        """;

    public static async Task<string> CreateHelperBundleAsync(
        string scratchDirectory,
        CancellationToken cancellationToken)
    {
        var bundleDirectory = Path.Combine(scratchDirectory, "helper-bundle");
        Directory.CreateDirectory(bundleDirectory);
        var helperPath = Path.Combine(bundleDirectory, "ListaryOpen.Indexer.Elevated.exe");
        var files = new[]
        {
            helperPath,
            Path.Combine(bundleDirectory, "ListaryOpen.Indexer.Elevated.dll"),
            Path.Combine(bundleDirectory, "ListaryOpen.Indexer.Elevated.deps.json"),
            Path.Combine(bundleDirectory, "ListaryOpen.Indexer.Elevated.runtimeconfig.json")
        };
        foreach (var file in files)
        {
            await File.WriteAllBytesAsync(file, Array.Empty<byte>(), cancellationToken).ConfigureAwait(false);
        }

        return helperPath;
    }

    public static Task<BenchmarkRun> MeasureEnumerationAsync(
        IReadOnlyList<FileRecord> records,
        bool baseline,
        int repeat,
        CancellationToken cancellationToken)
    {
        var variant = baseline ? "baseline-task-yield" : "current-inline";
        return IndexingBenchmarkRunner.MeasureAsync(
            "async-enumeration",
            variant,
            repeat,
            records.Count,
            async stopwatch =>
            {
                var source = baseline
                    ? EnumerateWithPerRecordYieldAsync(records, cancellationToken)
                    : EnumerateInlineAsync(records, cancellationToken);
                return await ConsumeAsync(source, records, stopwatch, cancellationToken).ConfigureAwait(false);
            });
    }

    public static async Task<BenchmarkRun> MeasureWriterAsync(
        IReadOnlyList<FileRecord> records,
        bool baseline,
        int repeat,
        string scratchDirectory,
        CancellationToken cancellationToken)
    {
        var variant = baseline ? "baseline-default-buffer" : "current-64k-flush256";
        var outputPath = baseline
            ? Path.Combine(scratchDirectory, $"writer-old-{Guid.NewGuid():N}.jsonl")
            : CreateTrustedOutputPath();

        try
        {
            var expectedChecksum = ExpectedChecksum(records.Count);
            var run = await IndexingBenchmarkRunner.MeasureAsync(
                "record-writer",
                variant,
                repeat,
                records.Count,
                async _ =>
                {
                    if (baseline)
                    {
                        await WriteOldFileAsync(
                            EnumerateInlineAsync(records, cancellationToken),
                            outputPath,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ElevatedIndexerRecordWriter.WriteFileAsync(
                            EnumerateInlineAsync(records, cancellationToken),
                            outputPath,
                            cancellationToken).ConfigureAwait(false);
                    }

                    return new BenchmarkOutcome(records.Count, expectedChecksum);
                }).ConfigureAwait(false);

            var verification = await ReadCompletedFileAsync(outputPath, stopwatch: null, cancellationToken)
                .ConfigureAwait(false);
            VerifyOutcome("record-writer", variant, verification, records);
            return run;
        }
        finally
        {
            TryDeleteFile(outputPath);
        }
    }

    /// <summary>
    /// Profiles JSONL serialize+parse vs binary frame encode+decode for the same records.
    /// Used to justify the scan-path binary IPC switch.
    /// </summary>
    public static Task<BenchmarkRun> MeasureTransportCodecAsync(
        IReadOnlyList<FileRecord> records,
        bool baseline,
        int repeat,
        CancellationToken cancellationToken)
    {
        var variant = baseline ? "jsonl-serialize-parse" : "binary-encode-decode";
        return IndexingBenchmarkRunner.MeasureAsync(
            "transport-codec",
            variant,
            repeat,
            records.Count,
            async stopwatch =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                long checksum = 0;
                double? firstMs = null;

                if (baseline)
                {
                    await using var stream = new MemoryStream();
                    await using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
                    {
                        foreach (var record in records)
                        {
                            var line = JsonSerializer.Serialize(
                                new
                                {
                                    record.FullPath,
                                    record.IsDirectory,
                                    record.SizeBytes,
                                    record.LastWriteTime,
                                    fileReferenceNumber = record.FileReferenceNumber.ToString(CultureInfo.InvariantCulture)
                                },
                                new JsonSerializerOptions(JsonSerializerDefaults.Web));
                            await writer.WriteLineAsync(line).ConfigureAwait(false);
                        }

                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    stream.Position = 0;
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var index = 0;
                    while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        var dto = JsonSerializer.Deserialize<JsonElement>(line);
                        var path = dto.GetProperty("fullPath").GetString() ?? string.Empty;
                        checksum += path.Length + index;
                        if (firstMs is null)
                        {
                            firstMs = stopwatch.Elapsed.TotalMilliseconds;
                        }

                        index++;
                    }

                    return new BenchmarkOutcome(index, checksum, firstMs);
                }

                await using (var stream = new MemoryStream())
                {
                    await ElevatedIndexerBinaryWriter
                        .WriteRecordsAsync(EnumerateInlineAsync(records, cancellationToken), stream, cancellationToken)
                        .ConfigureAwait(false);
                    var bytes = stream.ToArray();
                    var decoded = new List<FileRecord>(records.Count);
                    if (!ElevatedIndexerBinaryCodec.TryConsumeFrames(bytes, decoded, out var consumed, out var corrupt)
                        || corrupt
                        || consumed != bytes.Length)
                    {
                        throw new InvalidDataException("Binary transport round-trip failed.");
                    }

                    for (var index = 0; index < decoded.Count; index++)
                    {
                        checksum += decoded[index].FullPath.Length + index;
                        if (firstMs is null)
                        {
                            firstMs = stopwatch.Elapsed.TotalMilliseconds;
                        }
                    }

                    return new BenchmarkOutcome(decoded.Count, checksum, firstMs);
                }
            });
    }

    public static Task<BenchmarkRun> MeasureHelperPipelineAsync(
        IReadOnlyList<FileRecord> records,
        bool baseline,
        int repeat,
        string helperPath,
        string scratchDirectory,
        int producerDelayMilliseconds,
        CancellationToken cancellationToken)
    {
        var variant = baseline ? "baseline-wait-then-read" : "current-tail-while-run";
        return IndexingBenchmarkRunner.MeasureAsync(
            "helper-pipeline",
            variant,
            repeat,
            records.Count,
            async stopwatch =>
            {
                BenchmarkOutcome outcome;
                if (baseline)
                {
                    var outputPath = Path.Combine(scratchDirectory, $"pipeline-old-{Guid.NewGuid():N}.jsonl");
                    try
                    {
                        await WriteOldFileAsync(
                            EnumeratePacedAsync(records, producerDelayMilliseconds, cancellationToken),
                            outputPath,
                            cancellationToken).ConfigureAwait(false);
                        outcome = await ReadCompletedFileAsync(outputPath, stopwatch, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        TryDeleteFile(outputPath);
                    }
                }
                else
                {
                    outcome = await RunCurrentHelperPipelineAsync(
                        records,
                        helperPath,
                        producerDelayMilliseconds,
                        stopwatch,
                        cancellationToken).ConfigureAwait(false);
                }

                VerifyOutcome("helper-pipeline", variant, outcome, records);
                return outcome;
            });
    }

    public static async Task<BenchmarkRun> MeasureSqliteUpsertAsync(
        IReadOnlyList<FileRecord> records,
        bool baseline,
        int repeat,
        string scratchDirectory,
        CancellationToken cancellationToken)
    {
        var variant = baseline ? "baseline-command-per-row" : "current-prepared-reuse";
        var databasePath = Path.Combine(
            scratchDirectory,
            $"sqlite-{(baseline ? "old" : "current")}-{Guid.NewGuid():N}.db");
        await PrepareDatabaseAsync(databasePath, cancellationToken).ConfigureAwait(false);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureWriterAsync(connection, cancellationToken).ConfigureAwait(false);

            var run = await IndexingBenchmarkRunner.MeasureAsync(
                "sqlite-upsert",
                variant,
                repeat,
                records.Count,
                async _ =>
                {
                    if (baseline)
                    {
                        await UpsertWithCommandPerRecordAsync(connection, records, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await UpsertWithPreparedCommandAsync(connection, records, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return new BenchmarkOutcome(records.Count, ExpectedChecksum(records.Count));
                }).ConfigureAwait(false);

            await VerifyDatabaseAsync(connection, records, cancellationToken).ConfigureAwait(false);
            return run;
        }
        finally
        {
            TryDeleteFile(databasePath);
            TryDeleteFile(databasePath + "-shm");
            TryDeleteFile(databasePath + "-wal");
        }
    }

    public static Task<BenchmarkRun> MeasureProgressDispatchAsync(
        int logicalBatches,
        bool baseline,
        int repeat)
    {
        var variant = baseline ? "ablation-unthrottled" : "current-200ms-throttle";
        return IndexingBenchmarkRunner.MeasureAsync(
            "progress-dispatch",
            variant,
            repeat,
            logicalBatches,
            _ =>
            {
                var sink = new ProgressSink();
                if (baseline)
                {
                    for (var batch = 1; batch <= logicalBatches; batch++)
                    {
                        sink.Publish(new IndexingStatus(
                            IndexingRunState.Indexing,
                            "Writing 500 indexed items...",
                            (batch - 1) * 500));
                        sink.Publish(new IndexingStatus(
                            IndexingRunState.Indexing,
                            "Scanning with BenchmarkProvider...",
                            batch * 500));
                    }
                }
                else
                {
                    // A deterministic virtual clock advances 1 ms per batch. Production uses the
                    // same 200 ms deadline against Stopwatch timestamps.
                    var nextProgressReportAt = 0;
                    var lastReportedCount = -1;
                    for (var batch = 1; batch <= logicalBatches; batch++)
                    {
                        var logicalMilliseconds = batch - 1;
                        if (logicalMilliseconds < nextProgressReportAt)
                        {
                            continue;
                        }

                        sink.Publish(new IndexingStatus(
                            IndexingRunState.Indexing,
                            "Writing 500 indexed items...",
                            (batch - 1) * 500));
                        lastReportedCount = batch * 500;
                        sink.Publish(new IndexingStatus(
                            IndexingRunState.Indexing,
                            "Scanning with BenchmarkProvider...",
                            lastReportedCount));
                        nextProgressReportAt = logicalMilliseconds + 200;
                    }

                    var finalCount = logicalBatches * 500;
                    if (lastReportedCount != finalCount)
                    {
                        sink.Publish(new IndexingStatus(
                            IndexingRunState.Indexing,
                            "Scanning with BenchmarkProvider...",
                            finalCount));
                    }
                }

                if (sink.LastIndexedCount != logicalBatches * 500)
                {
                    throw new InvalidDataException("Progress ablation did not preserve the final indexed count.");
                }

                return Task.FromResult(new BenchmarkOutcome(
                    logicalBatches,
                    sink.PublishedCount));
            });
    }

    private static async Task<BenchmarkOutcome> RunCurrentHelperPipelineAsync(
        IReadOnlyList<FileRecord> records,
        string helperPath,
        int producerDelayMilliseconds,
        System.Diagnostics.Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var trustedTempDirectory = ElevatedIndexerOutputPathValidator.GetTrustedTempDirectory();
        Directory.CreateDirectory(trustedTempDirectory);
        IElevatedIndexerProcess CreateProcess(string _, string __, string outputPath, string errorPath)
            => new CurrentWriterProcess(
                records,
                producerDelayMilliseconds,
                outputPath,
                errorPath);

        var client = new ElevatedIndexerClient(
            helperPath,
            CreateProcess,
            isProcessElevated: () => true,
            isUacElevationEnabled: () => false,
            createElevatedProcess: CreateProcess,
            indexerTempDirectory: trustedTempDirectory);

        return await ConsumeAsync(
                client.ScanNtfsAsync(new IndexRoot(@"C:\benchmark"), cancellationToken),
                records,
                stopwatch,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<BenchmarkOutcome> ConsumeAsync(
        IAsyncEnumerable<FileRecord> source,
        IReadOnlyList<FileRecord> expected,
        System.Diagnostics.Stopwatch? stopwatch,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var checksum = 0L;
        double? firstRecordMilliseconds = null;
        string? firstPath = null;
        string? lastPath = null;

        await foreach (var record in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            firstRecordMilliseconds ??= stopwatch?.Elapsed.TotalMilliseconds;
            firstPath ??= record.FullPath;
            lastPath = record.FullPath;
            count++;
            checksum = checked(checksum + record.SizeBytes);
        }

        var outcome = new BenchmarkOutcome(
            count,
            checksum,
            firstRecordMilliseconds,
            firstPath,
            lastPath);
        VerifyOutcome("consumer", "source", outcome, expected);
        return outcome;
    }

    private static async Task<BenchmarkOutcome> ReadCompletedFileAsync(
        string outputPath,
        System.Diagnostics.Stopwatch? stopwatch,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            outputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream);
        var count = 0;
        var checksum = 0L;
        double? firstRecordMilliseconds = null;
        string? firstPath = null;
        string? lastPath = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var dto = JsonSerializer.Deserialize<WriterRecordDto>(line)
                ?? throw new InvalidDataException("Writer produced an empty JSON record.");
            var fullPath = dto.FullPath
                ?? throw new InvalidDataException("Writer record is missing fullPath.");
            var record = FileRecord.Create(
                fullPath,
                dto.IsDirectory
                    ?? throw new InvalidDataException("Writer record is missing isDirectory."),
                dto.SizeBytes
                    ?? throw new InvalidDataException("Writer record is missing sizeBytes."),
                dto.LastWriteTime
                    ?? throw new InvalidDataException("Writer record is missing lastWriteTime."));
            firstRecordMilliseconds ??= stopwatch?.Elapsed.TotalMilliseconds;
            firstPath ??= record.FullPath;
            lastPath = record.FullPath;
            count++;
            checksum = checked(checksum + record.SizeBytes);
        }

        return new BenchmarkOutcome(
            count,
            checksum,
            firstRecordMilliseconds,
            firstPath,
            lastPath);
    }

    private static async Task WriteOldFileAsync(
        IAsyncEnumerable<FileRecord> records,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        await using var writer = new StreamWriter(stream);
        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var line = JsonSerializer.Serialize(
                new
                {
                    record.FullPath,
                    record.IsDirectory,
                    record.SizeBytes,
                    record.LastWriteTime
                },
                global::JsonOptions.Default);
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<FileRecord> EnumerateWithPerRecordYieldAsync(
        IReadOnlyList<FileRecord> records,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<FileRecord> EnumerateInlineAsync(
        IReadOnlyList<FileRecord> records,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    private static async IAsyncEnumerable<FileRecord> EnumeratePacedAsync(
        IReadOnlyList<FileRecord> records,
        int producerDelayMilliseconds,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return records[index];
            if ((index + 1) % ElevatedIndexerRecordWriter.StreamingFlushRecordCount == 0
                && index + 1 < records.Count
                && producerDelayMilliseconds > 0)
            {
                await Task.Delay(producerDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task PrepareDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var index = await SqliteSearchIndex.OpenAsync(databasePath, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ConfigureWriterAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            pragma journal_mode = wal;
            pragma synchronous = normal;
            pragma busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertWithCommandPerRecordAsync(
        SqliteConnection connection,
        IReadOnlyList<FileRecord> records,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = UpsertSql;
            command.Parameters.AddWithValue("$full_path", record.FullPath);
            command.Parameters.AddWithValue("$path_key", record.PathKey);
            command.Parameters.AddWithValue("$name", record.Name);
            command.Parameters.AddWithValue("$parent_path", record.ParentPath);
            command.Parameters.AddWithValue("$search_text", PinyinMatcher.CreateSearchAliases(record.FullPath));
            command.Parameters.AddWithValue("$is_directory", record.IsDirectory ? 1 : 0);
            command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
            command.Parameters.AddWithValue("$last_write_time", FormatDateTime(record.LastWriteTime));
            command.Parameters.AddWithValue("$index_generation", 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertWithPreparedCommandAsync(
        SqliteConnection connection,
        IReadOnlyList<FileRecord> records,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = UpsertSql;
        command.Parameters.Add("$full_path", SqliteType.Text);
        command.Parameters.Add("$path_key", SqliteType.Text);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$parent_path", SqliteType.Text);
        command.Parameters.Add("$search_text", SqliteType.Text);
        command.Parameters.Add("$is_directory", SqliteType.Integer);
        command.Parameters.Add("$size_bytes", SqliteType.Integer);
        command.Parameters.Add("$last_write_time", SqliteType.Text);
        command.Parameters.Add("$index_generation", SqliteType.Integer);
        command.Prepare();

        foreach (var record in records)
        {
            command.Parameters[0].Value = record.FullPath;
            command.Parameters[1].Value = record.PathKey;
            command.Parameters[2].Value = record.Name;
            command.Parameters[3].Value = record.ParentPath;
            command.Parameters[4].Value = PinyinMatcher.CreateSearchAliases(record.FullPath);
            command.Parameters[5].Value = record.IsDirectory ? 1 : 0;
            command.Parameters[6].Value = record.SizeBytes;
            command.Parameters[7].Value = FormatDateTime(record.LastWriteTime);
            command.Parameters[8].Value = 0;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyDatabaseAsync(
        SqliteConnection connection,
        IReadOnlyList<FileRecord> records,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "select count(*), coalesce(sum(size_bytes), 0), min(size_bytes), max(size_bytes) from files;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQLite verification returned no aggregate row.");
        }

        var count = reader.GetInt32(0);
        var checksum = reader.GetInt64(1);
        var minimum = reader.GetInt64(2);
        var maximum = reader.GetInt64(3);
        if (count != records.Count
            || checksum != ExpectedChecksum(records.Count)
            || minimum != records[0].SizeBytes
            || maximum != records[^1].SizeBytes)
        {
            throw new InvalidDataException(
                $"SQLite semantic guard failed: count={count}, checksum={checksum}, range={minimum}..{maximum}.");
        }
    }

    private static string CreateTrustedOutputPath()
    {
        var trustedTempDirectory = ElevatedIndexerOutputPathValidator.GetTrustedTempDirectory();
        Directory.CreateDirectory(trustedTempDirectory);
        return Path.Combine(
            trustedTempDirectory,
            $"listary-open-indexer-benchmark-{Guid.NewGuid():N}.jsonl");
    }

    private static string FormatDateTime(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static long ExpectedChecksum(int recordCount)
        => checked((long)recordCount * (recordCount + 1L) / 2L);

    private static void VerifyOutcome(
        string scenario,
        string variant,
        BenchmarkOutcome outcome,
        IReadOnlyList<FileRecord> records)
    {
        if (outcome.ProcessedOperations != records.Count
            || outcome.SemanticChecksum != ExpectedChecksum(records.Count))
        {
            throw new InvalidDataException(
                $"{scenario}/{variant} semantic guard failed: count={outcome.ProcessedOperations}, checksum={outcome.SemanticChecksum}.");
        }

        if (outcome.FirstPath is not null
            && !string.Equals(outcome.FirstPath, records[0].FullPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{scenario}/{variant} changed the first record.");
        }

        if (outcome.LastPath is not null
            && !string.Equals(outcome.LastPath, records[^1].FullPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{scenario}/{variant} changed the last record.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class CurrentWriterProcess : IElevatedIndexerProcess
    {
        private readonly IReadOnlyList<FileRecord> _records;
        private readonly int _producerDelayMilliseconds;
        private readonly string _outputPath;
        private readonly string _errorPath;
        private readonly CancellationTokenSource _cancellation = new();
        private Task _runTask = Task.CompletedTask;
        private int _hasExited;

        public CurrentWriterProcess(
            IReadOnlyList<FileRecord> records,
            int producerDelayMilliseconds,
            string outputPath,
            string errorPath)
        {
            _records = records;
            _producerDelayMilliseconds = producerDelayMilliseconds;
            _outputPath = outputPath;
            _errorPath = errorPath;
        }

        public int ExitCode => 0;

        public bool HasExited => Volatile.Read(ref _hasExited) != 0;

        public bool Start()
        {
            _runTask = RunAsync();
            return true;
        }

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _runTask.WaitAsync(cancellationToken);

        public void Kill()
        {
            _cancellation.Cancel();
        }

        public void Dispose()
        {
            _cancellation.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                await ElevatedIndexerRecordWriter.WriteFileAsync(
                    EnumeratePacedAsync(_records, _producerDelayMilliseconds, _cancellation.Token),
                    _outputPath,
                    _cancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(_errorPath, exception.ToString(), CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            finally
            {
                Volatile.Write(ref _hasExited, 1);
            }
        }
    }

    private sealed class ProgressSink
    {
        public int PublishedCount { get; private set; }

        public int LastIndexedCount { get; private set; }

        public void Publish(IndexingStatus status)
        {
            PublishedCount++;
            LastIndexedCount = status.IndexedCount;
        }
    }

    private sealed class WriterRecordDto
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
}
