using System.Globalization;
using ListaryOpen.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.IndexingBenchmark;

/// <summary>
/// Runs reconciliation A/B against a disposable production-scale database copy.
/// The supplied database is intentionally mutated; callers must pass a copy.
/// </summary>
internal static class ExistingDatabaseProbe
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length is < 1 or > 4)
        {
            Console.Error.WriteLine(
                "Usage: --probe-reconciliation DB_PATH [RECORDS=10000] [REPEATS=3] [JSON_PATH]");
            return 2;
        }

        var databasePath = Path.GetFullPath(args[0]);
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Probe database does not exist.", databasePath);
        }

        var recordCount = args.Length >= 2 ? ParsePositiveInt(args[1], "RECORDS") : 10_000;
        var repeats = args.Length >= 3 ? ParsePositiveInt(args[2], "REPEATS") : 3;
        var jsonPath = args.Length >= 4 ? args[3] : null;
        var records = await ReadRecordsAsync(databasePath, recordCount, cancellationToken)
            .ConfigureAwait(false);
        var runs = new List<BenchmarkRun>();

        Console.Error.WriteLine("Warmup 1/1...");
        await RunRoundAsync(databasePath, records, repeat: -1, capture: false, runs, cancellationToken)
            .ConfigureAwait(false);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            Console.Error.WriteLine($"Measured round {repeat + 1}/{repeats}...");
            await RunRoundAsync(databasePath, records, repeat, capture: true, runs, cancellationToken)
                .ConfigureAwait(false);
        }

        var options = new BenchmarkOptions(
            records.Length,
            records.Length,
            records.Length,
            1,
            repeats,
            1,
            0,
            jsonPath);
        var report = BenchmarkReportWriter.Create(options, runs);
        BenchmarkReportWriter.WriteConsole(report);
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            await BenchmarkReportWriter.WriteJsonAsync(report, jsonPath, cancellationToken)
                .ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task RunRoundAsync(
        string databasePath,
        IReadOnlyList<FileRecord> records,
        int repeat,
        bool capture,
        ICollection<BenchmarkRun> runs,
        CancellationToken cancellationToken)
    {
        var baselineFirst = repeat < 0 || repeat % 2 == 0;
        foreach (var baseline in baselineFirst ? new[] { true, false } : new[] { false, true })
        {
            var run = await BenchmarkScenarios.MeasureExistingReconciliationAsync(
                    databasePath,
                    records,
                    baseline,
                    repeat,
                    cancellationToken)
                .ConfigureAwait(false);
            if (capture)
            {
                runs.Add(run);
            }
        }
    }

    private static async Task<FileRecord[]> ReadRecordsAsync(
        string databasePath,
        int recordCount,
        CancellationToken cancellationToken)
    {
        var records = new List<FileRecord>(recordCount);
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            select full_path, is_directory, size_bytes, last_write_time, file_reference
            from files indexed by ix_files_file_reference
            where file_reference > 0
              and path_key >= 'C:\'
              and path_key < 'C:\' || char(65535)
            order by file_reference
            limit $limit;
            """;
        command.Parameters.AddWithValue("$limit", recordCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(FileRecord.CreateFromNormalizedPath(
                reader.GetString(0),
                reader.GetInt64(1) != 0,
                reader.GetInt64(2),
                DateTimeOffset.Parse(
                    reader.GetString(3),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                unchecked((ulong)reader.GetInt64(4))));
        }

        if (records.Count != recordCount)
        {
            throw new InvalidDataException(
                $"Probe requested {recordCount:N0} records but loaded {records.Count:N0}.");
        }

        return records.ToArray();
    }

    private static int ParsePositiveInt(string value, string name)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new ArgumentException($"{name} must be a positive integer.");
}
