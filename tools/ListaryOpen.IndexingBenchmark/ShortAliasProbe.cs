using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.IndexingBenchmark;

/// <summary>
/// Compares the former full files-table scan with the sparse transliteration-index
/// scan used for one- and two-character pinyin aliases. The supplied database is
/// intentionally migrated and must be a disposable copy.
/// </summary>
internal static class ShortAliasProbe
{
    private const string PreviousSql = """
        select full_path
        from files not indexed
        where search_text like $alias_contains escape '\'
        order by length(name), name collate nocase, name, length(full_path), full_path
        limit $limit;
        """;

    private const string CurrentSql = """
        select full_path
        from files indexed by ix_files_search_text_nonempty
        where search_text <> ''
          and search_text like $alias_contains escape '\'
        order by length(name), name collate nocase, name, length(full_path), full_path
        limit $limit;
        """;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length is < 1 or > 4)
        {
            Console.Error.WriteLine(
                "Usage: --probe-short-alias DB_PATH [QUERY=ht] [REPEATS=5] [JSON_PATH]");
            return 2;
        }

        var databasePath = Path.GetFullPath(args[0]);
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Probe database does not exist.", databasePath);
        }

        var query = args.Length >= 2 ? args[1].Trim().ToLowerInvariant() : "ht";
        if (query.Length is < 1 or > 2)
        {
            throw new ArgumentException("QUERY must contain one or two characters.");
        }

        var repeats = args.Length >= 3 ? ParsePositiveInt(args[2], "REPEATS") : 5;
        var jsonPath = args.Length >= 4 ? args[3] : null;
        const int resultLimit = 200;

        await EnsureSparseIndexAsync(databasePath, cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False;Default Timeout=30");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var configure = connection.CreateCommand())
        {
            configure.CommandText = """
                pragma query_only = true;
                pragma busy_timeout = 30000;
                pragma cache_size = -16384;
                pragma mmap_size = 268435456;
                """;
            await configure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var expected = await ReadPathsAsync(
                connection,
                CurrentSql,
                query,
                resultLimit,
                cancellationToken)
            .ConfigureAwait(false);
        var previous = await ReadPathsAsync(
                connection,
                PreviousSql,
                query,
                resultLimit,
                cancellationToken)
            .ConfigureAwait(false);
        if (!previous.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Short-alias variants returned different ordered results.");
        }

        var runs = new List<BenchmarkRun>();
        Console.Error.WriteLine("Warmup 1/1...");
        await RunRoundAsync(
                connection,
                query,
                resultLimit,
                repeat: -1,
                capture: false,
                runs,
                cancellationToken)
            .ConfigureAwait(false);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            Console.Error.WriteLine($"Measured round {repeat + 1}/{repeats}...");
            await RunRoundAsync(
                    connection,
                    query,
                    resultLimit,
                    repeat,
                    capture: true,
                    runs,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var options = new BenchmarkOptions(1, 1, 1, 1, repeats, 1, 0, jsonPath);
        var report = BenchmarkReportWriter.Create(options, runs);
        BenchmarkReportWriter.WriteConsole(report);
        if (!string.IsNullOrWhiteSpace(jsonPath))
        {
            await BenchmarkReportWriter.WriteJsonAsync(report, jsonPath, cancellationToken)
                .ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task EnsureSparseIndexAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False;Default Timeout=30");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            create index if not exists ix_files_search_text_nonempty
                on files(search_text)
                where search_text <> '';
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunRoundAsync(
        SqliteConnection connection,
        string query,
        int resultLimit,
        int repeat,
        bool capture,
        ICollection<BenchmarkRun> runs,
        CancellationToken cancellationToken)
    {
        var previousFirst = repeat < 0 || repeat % 2 == 0;
        foreach (var previous in previousFirst ? new[] { true, false } : new[] { false, true })
        {
            var sql = previous ? PreviousSql : CurrentSql;
            var variant = previous ? "previous-full-table" : "current-sparse-index";
            var run = await IndexingBenchmarkRunner.MeasureAsync(
                    "short-alias-query",
                    variant,
                    repeat,
                    operations: 1,
                    async _ =>
                    {
                        var paths = await ReadPathsAsync(
                                connection,
                                sql,
                                query,
                                resultLimit,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return new BenchmarkOutcome(
                            ProcessedOperations: 1,
                            SemanticChecksum: CalculateChecksum(paths));
                    })
                .ConfigureAwait(false);
            if (capture)
            {
                runs.Add(run);
            }
        }
    }

    private static async Task<string[]> ReadPathsAsync(
        SqliteConnection connection,
        string sql,
        string query,
        int resultLimit,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$alias_contains", "%" + EscapeLike(query) + "%");
        command.Parameters.AddWithValue("$limit", resultLimit);

        var paths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            paths.Add(reader.GetString(0));
        }

        return paths.ToArray();
    }

    private static long CalculateChecksum(IEnumerable<string> paths)
    {
        const long offset = 1469598103934665603L;
        const long prime = 1099511628211L;
        var checksum = offset;
        foreach (var path in paths)
        {
            foreach (var ch in path)
            {
                checksum = unchecked((checksum ^ ch) * prime);
            }
        }

        return checksum;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static int ParsePositiveInt(string value, string name)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new ArgumentException($"{name} must be a positive integer.");
}
