using System.Diagnostics;
using System.Globalization;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;
using ListaryOpen.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.SearchBenchmark;

internal static class AblationBenchmarkRunner
{
    private const int CandidateMultiplier = 20;
    private const int MinimumCandidateLimit = 200;
    private const int MaximumCandidateLimit = 5_000;
    private const int FallbackCandidateLimit = 200;

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseOptions(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: ab <index.db> --root <path> --query <plain-query> " +
                "[--iterations N] [--sample N] [--upserts N]");
            return 2;
        }

        var query = new SearchQuery(
            options.Query,
            SearchMode.FilesAndFolders,
            preferredRoot: options.PreferredRoot);
        ValidatePlainQuery(query);

        // The A/B suite needs the current schema/indexes. Use a database copy because this can migrate it.
        await using (var index = await SqliteSearchIndex.OpenAsync(options.DatabasePath, cancellationToken))
        {
        }
        await EnsurePreferredRootIndexAsync(options.DatabasePath, cancellationToken);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        Console.WriteLine(
            $"dataset={Path.GetFullPath(options.DatabasePath)}\troot={query.PreferredRoot}\t" +
            $"query={query.NormalizedText}\titerations={options.Iterations}\tsample={options.SampleSize}");

        await BenchmarkPreferredRootIndexAsync(connection, query, options.Iterations, cancellationToken);
        await BenchmarkCandidatePassElisionAsync(connection, query, options.Iterations, cancellationToken);
        await BenchmarkRankerAsync(connection, query, options.SampleSize, options.Iterations, cancellationToken);
        await BenchmarkPinyinAsync(connection, query.NormalizedText, options.SampleSize, options.Iterations, cancellationToken);
        await MeasureSearchTextStorageAsync(connection, options.SampleSize, cancellationToken);
        await BenchmarkPreparedUpsertAsync(options.UpsertCount, Math.Min(options.Iterations, 3), cancellationToken);

        Console.WriteLine("semantic_checks=passed");
        return 0;
    }

    private static async Task BenchmarkPreferredRootIndexAsync(
        SqliteConnection connection,
        SearchQuery query,
        int iterations,
        CancellationToken cancellationToken)
    {
        var baseline = await ReadPreferredRootAsync(connection, query, indexed: false, cancellationToken);
        var optimized = await ReadPreferredRootAsync(connection, query, indexed: true, cancellationToken);
        AssertSameRecords("preferred-root indexed/not-indexed", baseline, optimized);

        var baselineTimes = new List<double>();
        var optimizedTimes = new List<double>();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if ((iteration & 1) == 0)
            {
                baselineTimes.Add((await MeasureAsync(
                    () => ReadPreferredRootAsync(connection, query, indexed: false, cancellationToken))).Milliseconds);
                optimizedTimes.Add((await MeasureAsync(
                    () => ReadPreferredRootAsync(connection, query, indexed: true, cancellationToken))).Milliseconds);
            }
            else
            {
                optimizedTimes.Add((await MeasureAsync(
                    () => ReadPreferredRootAsync(connection, query, indexed: true, cancellationToken))).Milliseconds);
                baselineTimes.Add((await MeasureAsync(
                    () => ReadPreferredRootAsync(connection, query, indexed: false, cancellationToken))).Milliseconds);
            }
        }

        PrintComparison(
            "preferred_root_parent_index",
            baselineTimes,
            optimizedTimes,
            $"rows={optimized.Count}; equivalent SQL, baseline forced NOT INDEXED");
    }

    private static async Task BenchmarkCandidatePassElisionAsync(
        SqliteConnection connection,
        SearchQuery query,
        int iterations,
        CancellationToken cancellationToken)
    {
        var baseline = await RunCandidatePipelineAsync(connection, query, optimized: false, cancellationToken);
        var optimized = await RunCandidatePipelineAsync(connection, query, optimized: true, cancellationToken);
        AssertSameResults("candidate pass elision", baseline.Results, optimized.Results);

        if (!optimized.SkippedAnyPass)
        {
            Console.WriteLine(
                $"candidate_pass_elision\tnot_applicable\tcandidates={optimized.CandidateCount}\t" +
                "optimized pipeline needed every pass for this query");
            return;
        }

        var baselineTimes = new List<double>();
        var optimizedTimes = new List<double>();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if ((iteration & 1) == 0)
            {
                baselineTimes.Add((await MeasureAsync(
                    () => RunCandidatePipelineAsync(connection, query, optimized: false, cancellationToken))).Milliseconds);
                optimizedTimes.Add((await MeasureAsync(
                    () => RunCandidatePipelineAsync(connection, query, optimized: true, cancellationToken))).Milliseconds);
            }
            else
            {
                optimizedTimes.Add((await MeasureAsync(
                    () => RunCandidatePipelineAsync(connection, query, optimized: true, cancellationToken))).Milliseconds);
                baselineTimes.Add((await MeasureAsync(
                    () => RunCandidatePipelineAsync(connection, query, optimized: false, cancellationToken))).Milliseconds);
            }
        }

        PrintComparison(
            "candidate_pass_elision",
            baselineTimes,
            optimizedTimes,
            $"results={optimized.Results.Count}; optimized_candidates={optimized.CandidateCount}; " +
            $"baseline_candidates={baseline.CandidateCount}");
    }

    private static async Task BenchmarkRankerAsync(
        SqliteConnection connection,
        SearchQuery query,
        int sampleSize,
        int iterations,
        CancellationToken cancellationToken)
    {
        var records = await ReadSampleRecordsAsync(connection, sampleSize, cancellationToken);
        var usage = records
            .Where((_, index) => index % 7 == 0)
            .Select((record, index) => new UsageRecord(
                record.FullPath,
                index % 31,
                DateTimeOffset.UtcNow.AddDays(-(index % 10) - 2)))
            .ToArray();
        var pinned = records
            .Where(record => record.IsDirectory)
            .Where((_, index) => index % 19 == 0)
            .Select(record => record.FullPath)
            .ToArray();

        var baseline = BaselineResultRanker.Rank(query, records, usage, pinned);
        var optimized = ResultRanker.Rank(query, records, usage, pinned);
        AssertSameResults("ResultRanker", baseline, optimized);

        var baselineTimes = new List<double>();
        var optimizedTimes = new List<double>();
        var baselineAllocations = new List<long>();
        var optimizedAllocations = new List<long>();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if ((iteration & 1) == 0)
            {
                AddSyncMeasurement(
                    () => BaselineResultRanker.Rank(query, records, usage, pinned),
                    baselineTimes,
                    baselineAllocations);
                AddSyncMeasurement(
                    () => ResultRanker.Rank(query, records, usage, pinned),
                    optimizedTimes,
                    optimizedAllocations);
            }
            else
            {
                AddSyncMeasurement(
                    () => ResultRanker.Rank(query, records, usage, pinned),
                    optimizedTimes,
                    optimizedAllocations);
                AddSyncMeasurement(
                    () => BaselineResultRanker.Rank(query, records, usage, pinned),
                    baselineTimes,
                    baselineAllocations);
            }
        }

        PrintComparison(
            "result_ranker_single_pass",
            baselineTimes,
            optimizedTimes,
            $"records={records.Count}; baseline_alloc={Median(baselineAllocations):N0}B; " +
            $"optimized_alloc={Median(optimizedAllocations):N0}B");
    }

    private static async Task BenchmarkPinyinAsync(
        SqliteConnection connection,
        string query,
        int sampleSize,
        int iterations,
        CancellationToken cancellationToken)
    {
        var names = (await ReadSampleRecordsAsync(connection, sampleSize, cancellationToken))
            .Select(record => record.Name)
            .ToArray();
        foreach (var name in names)
        {
            var baselineText = BaselinePinyinMatcher.CreateSearchText(name);
            var optimizedText = PinyinMatcher.CreateSearchText(name);
            if (!string.Equals(baselineText, optimizedText, StringComparison.Ordinal) ||
                BaselinePinyinMatcher.Score(query, name) != PinyinMatcher.Score(query, name))
            {
                throw new InvalidOperationException($"Pinyin semantic mismatch for '{name}'.");
            }
        }

        var baselineTimes = new List<double>();
        var optimizedTimes = new List<double>();
        var baselineAllocations = new List<long>();
        var optimizedAllocations = new List<long>();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            if ((iteration & 1) == 0)
            {
                AddSyncMeasurement(
                    () => RunBaselinePinyinBatch(names, query),
                    baselineTimes,
                    baselineAllocations);
                AddSyncMeasurement(
                    () => RunOptimizedPinyinBatch(names, query),
                    optimizedTimes,
                    optimizedAllocations);
            }
            else
            {
                AddSyncMeasurement(
                    () => RunOptimizedPinyinBatch(names, query),
                    optimizedTimes,
                    optimizedAllocations);
                AddSyncMeasurement(
                    () => RunBaselinePinyinBatch(names, query),
                    baselineTimes,
                    baselineAllocations);
            }
        }

        PrintComparison(
            "pinyin_single_pass",
            baselineTimes,
            optimizedTimes,
            $"names={names.Length}; baseline_alloc={Median(baselineAllocations):N0}B; " +
            $"optimized_alloc={Median(optimizedAllocations):N0}B");
    }

    private static async Task MeasureSearchTextStorageAsync(
        SqliteConnection connection,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        var records = await ReadSampleRecordsAsync(connection, sampleSize, cancellationToken);
        long baselineCharacters = 0;
        long optimizedCharacters = 0;
        foreach (var record in records)
        {
            baselineCharacters += BaselinePinyinMatcher.CreateSearchText(record.FullPath).Length;
            baselineCharacters++;
            baselineCharacters += BaselinePinyinMatcher.CreateSearchText(record.Name).Length;
            optimizedCharacters += PinyinMatcher.CreateSearchAliases(record.FullPath).Length;
        }

        var reduction = baselineCharacters == 0
            ? 0
            : 100 * (1 - optimizedCharacters / (double)baselineCharacters);
        Console.WriteLine(
            $"search_text_alias_storage\tbaseline={baselineCharacters:N0}chars\t" +
            $"optimized={optimizedCharacters:N0}chars\treduction={reduction:F2}%\trows={records.Count}");
    }

    private static async Task BenchmarkPreparedUpsertAsync(
        int recordCount,
        int iterations,
        CancellationToken cancellationToken)
    {
        var records = Enumerable.Range(0, recordCount)
            .Select(index => FileRecord.Create(
                $"C:\\Benchmark\\Folder{index % 97:D2}\\Record-{index:D6}-{(index % 10 == 0 ? "合同" : "ascii")}.txt",
                false,
                index,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(index)))
            .ToArray();
        var baselineTimes = new List<double>();
        var optimizedTimes = new List<double>();

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var baselinePath = CreateTemporaryDatabasePath("baseline");
            var optimizedPath = CreateTemporaryDatabasePath("optimized");
            try
            {
                await CreateEmptyIndexAsync(baselinePath, cancellationToken);
                await EnsurePreferredRootIndexAsync(baselinePath, cancellationToken);
                await CreateEmptyIndexAsync(optimizedPath, cancellationToken);
                await EnsurePreferredRootIndexAsync(optimizedPath, cancellationToken);
                await using var baselineConnection = await OpenWriterConnectionAsync(baselinePath, cancellationToken);
                await using var optimizedIndex = await SqliteSearchIndex.OpenAsync(optimizedPath, cancellationToken);

                if ((iteration & 1) == 0)
                {
                    baselineTimes.Add((await MeasureAsync(
                        () => BaselineUpsertManyAsync(baselineConnection, records, cancellationToken))).Milliseconds);
                    optimizedTimes.Add((await MeasureAsync(async () =>
                    {
                        await optimizedIndex.UpsertManyAsync(records, cancellationToken);
                        return true;
                    })).Milliseconds);
                }
                else
                {
                    optimizedTimes.Add((await MeasureAsync(async () =>
                    {
                        await optimizedIndex.UpsertManyAsync(records, cancellationToken);
                        return true;
                    })).Milliseconds);
                    baselineTimes.Add((await MeasureAsync(
                        () => BaselineUpsertManyAsync(baselineConnection, records, cancellationToken))).Milliseconds);
                }

                var baselineSnapshot = await ReadDatabaseSnapshotAsync(baselineConnection, cancellationToken);
                var optimizedSnapshot = await ReadDatabaseSnapshotAsync(optimizedPath, cancellationToken);
                if (baselineSnapshot != optimizedSnapshot)
                {
                    throw new InvalidOperationException(
                        $"Prepared upsert semantic mismatch: baseline={baselineSnapshot}, optimized={optimizedSnapshot}.");
                }
            }
            finally
            {
                DeleteSqliteFiles(baselinePath);
                DeleteSqliteFiles(optimizedPath);
            }
        }

        PrintComparison(
            "prepared_upsert_batch",
            baselineTimes,
            optimizedTimes,
            $"records={recordCount}; baseline creates a command per row");
    }

    private static async Task<CandidatePipelineResult> RunCandidatePipelineAsync(
        SqliteConnection connection,
        SearchQuery query,
        bool optimized,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
        AddRecords(records, await ReadPreferredRootAsync(connection, query, indexed: true, cancellationToken));
        AddRecords(records, await ReadExactAsync(connection, query, cancellationToken));

        var skippedAnyPass = false;
        var expensiveFuzzy = query.NormalizedText.Length >= 3;
        if (optimized)
        {
            if (expensiveFuzzy && records.Count < query.Limit)
            {
                AddRecords(records, await ReadFtsAsync(connection, query, nameOnly: true, cancellationToken));
                if (records.Count < query.Limit)
                {
                    AddRecords(records, await ReadFtsAsync(connection, query, nameOnly: false, cancellationToken));
                }
                else
                {
                    skippedAnyPass = true;
                }
            }
            else if (expensiveFuzzy)
            {
                skippedAnyPass = true;
            }

            if (records.Count < query.Limit)
            {
                AddRecords(records, await ReadFallbackAsync(connection, cancellationToken));
            }
            else
            {
                skippedAnyPass = true;
            }
        }
        else
        {
            var hasExactName = records.Values.Any(record =>
                string.Equals(record.Name, query.NormalizedText, StringComparison.OrdinalIgnoreCase));
            if (expensiveFuzzy && !hasExactName)
            {
                AddRecords(records, await ReadFtsAsync(connection, query, nameOnly: true, cancellationToken));
                AddRecords(records, await ReadFtsAsync(connection, query, nameOnly: false, cancellationToken));
            }

            AddRecords(records, await ReadFallbackAsync(connection, cancellationToken));
        }

        var usage = await ReadUsageAsync(connection, records.Keys, cancellationToken);
        var results = ResultRanker.Rank(
            query,
            records.Values,
            usage,
            Array.Empty<string>());
        return new CandidatePipelineResult(results, records.Count, skippedAnyPass);
    }

    private static async Task<IReadOnlyList<FileRecord>> ReadPreferredRootAsync(
        SqliteConnection connection,
        SearchQuery query,
        bool indexed,
        CancellationToken cancellationToken)
    {
        var normalized = query.NormalizedText.Trim().ToLowerInvariant();
        var tableHint = indexed
            ? "indexed by ix_files_parent_path_nocase_name"
            : "not indexed";
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            select full_path, is_directory, size_bytes, last_write_time
            from files {tableHint}
            where parent_path = $root collate nocase
              and (
                lower(full_path) like $contains escape '\'
                or search_text like $contains escape '\'
                or lower(full_path) like $ordered escape '\'
                or search_text like $ordered escape '\'
              )
            order by length(name), name collate nocase, name, full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$root", query.PreferredRoot!);
        command.Parameters.AddWithValue("$contains", $"%{EscapeLike(normalized)}%");
        command.Parameters.AddWithValue("$ordered", CreateOrderedLikePattern(normalized));
        command.Parameters.AddWithValue("$limit", CreateCandidateLimit(query));
        return await ReadRecordsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<FileRecord>> ReadExactAsync(
        SqliteConnection connection,
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        var normalized = query.NormalizedText.Trim().ToLowerInvariant();
        using var command = connection.CreateCommand();
        command.CommandText = """
            select full_path, is_directory, size_bytes, last_write_time
            from files
            where name >= $query collate nocase
              and name < $query_upper_bound collate nocase
            order by
                case
                    when name = $query then 0
                    when name >= $query collate nocase
                     and name < $query_upper_bound collate nocase then 1
                    else 2
                end,
                length(name), name, length(full_path), full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$query", normalized);
        command.Parameters.AddWithValue("$query_upper_bound", normalized + '\uFFFF');
        command.Parameters.AddWithValue("$limit", CreateCandidateLimit(query));
        return await ReadRecordsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<FileRecord>> ReadFtsAsync(
        SqliteConnection connection,
        SearchQuery query,
        bool nameOnly,
        CancellationToken cancellationToken)
    {
        var terms = query.Parsed.Phrases
            .Concat(query.Parsed.Terms)
            .Select(term => term.Trim().ToLowerInvariant())
            .Where(term => term.Length >= 3)
            .Select(term => $"\"{term.Replace("\"", "\"\"")}\"")
            .ToArray();
        if (terms.Length == 0)
        {
            return Array.Empty<FileRecord>();
        }

        var termsQuery = string.Join(" AND ", terms);
        var match = nameOnly
            ? $"name : ({termsQuery})"
            : $"{{ parent_path search_text }} : ({termsQuery})";
        using var command = connection.CreateCommand();
        command.CommandText = """
            select files.full_path, files.is_directory, files.size_bytes, files.last_write_time
            from files_fts_v1
            inner join files on files.rowid = files_fts_v1.rowid
            where files_fts_v1 match $match
            order by
                length(files.name), files.name collate nocase, files.name,
                length(files.full_path), files.full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", CreateCandidateLimit(query));
        return await ReadRecordsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<FileRecord>> ReadFallbackAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            select full_path, is_directory, size_bytes, last_write_time
            from files
            order by name, full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$limit", FallbackCandidateLimit);
        return await ReadRecordsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<FileRecord>> ReadSampleRecordsAsync(
        SqliteConnection connection,
        int sampleSize,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            select full_path, is_directory, size_bytes, last_write_time
            from files
            order by rowid
            limit $limit;
            """;
        command.Parameters.AddWithValue("$limit", sampleSize);
        return await ReadRecordsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<UsageRecord>> ReadUsageAsync(
        SqliteConnection connection,
        IEnumerable<string> pathKeys,
        CancellationToken cancellationToken)
    {
        var keys = pathKeys.Distinct(StringComparer.Ordinal).ToArray();
        var usage = new List<UsageRecord>();
        for (var offset = 0; offset < keys.Length; offset += 500)
        {
            var count = Math.Min(500, keys.Length - offset);
            using var command = connection.CreateCommand();
            var parameters = new string[count];
            for (var index = 0; index < count; index++)
            {
                var name = $"$path_{index}";
                parameters[index] = name;
                command.Parameters.AddWithValue(name, keys[offset + index]);
            }

            command.CommandText = $"""
                select full_path, open_count, last_used_at
                from usage
                where path_key in ({string.Join(", ", parameters)});
                """;
            var reader = await command.ExecuteReaderAsync(cancellationToken);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    usage.Add(new UsageRecord(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        DateTimeOffset.Parse(
                            reader.GetString(2),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind)));
                }
            }
        }

        return usage;
    }

    private static async Task<IReadOnlyList<FileRecord>> ReadRecordsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var records = new List<FileRecord>();
        var reader = await command.ExecuteReaderAsync(cancellationToken);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                records.Add(FileRecord.Create(
                    reader.GetString(0),
                    reader.GetInt64(1) != 0,
                    reader.GetInt64(2),
                    DateTimeOffset.Parse(
                        reader.GetString(3),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)));
            }
        }

        return records;
    }

    private static async Task CreateEmptyIndexAsync(string path, CancellationToken cancellationToken)
    {
        await using var index = await SqliteSearchIndex.OpenAsync(path, cancellationToken);
    }

    private static async Task EnsurePreferredRootIndexAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenWriterConnectionAsync(path, cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            create index if not exists ix_files_parent_path_nocase_name
            on files(parent_path collate nocase, name collate nocase);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SqliteConnection> OpenWriterConnectionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<bool> BaselineUpsertManyAsync(
        SqliteConnection connection,
        IReadOnlyList<FileRecord> records,
        CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var record in records)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertSql;
            command.Parameters.AddWithValue("$full_path", record.FullPath);
            command.Parameters.AddWithValue("$path_key", record.PathKey);
            command.Parameters.AddWithValue("$name", record.Name);
            command.Parameters.AddWithValue("$parent_path", record.ParentPath);
            command.Parameters.AddWithValue("$search_text", PinyinMatcher.CreateSearchAliases(record.FullPath));
            command.Parameters.AddWithValue("$is_directory", record.IsDirectory ? 1 : 0);
            command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
            command.Parameters.AddWithValue(
                "$last_write_time",
                record.LastWriteTime.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$index_generation", 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<DatabaseSnapshot> ReadDatabaseSnapshotAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenWriterConnectionAsync(path, cancellationToken);
        return await ReadDatabaseSnapshotAsync(connection, cancellationToken);
    }

    private static async Task<DatabaseSnapshot> ReadDatabaseSnapshotAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            select count(*), coalesce(sum(size_bytes), 0), coalesce(sum(length(search_text)), 0)
            from files;
            """;
        var reader = await command.ExecuteReaderAsync(cancellationToken);
        await using (reader.ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken);
            return new DatabaseSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }
    }

    private static long RunBaselinePinyinBatch(IReadOnlyList<string> names, string query)
    {
        long checksum = 0;
        foreach (var name in names)
        {
            checksum += BaselinePinyinMatcher.CreateSearchText(name).Length;
            checksum += (long)BaselinePinyinMatcher.Score(query, name);
        }

        return checksum;
    }

    private static long RunOptimizedPinyinBatch(IReadOnlyList<string> names, string query)
    {
        long checksum = 0;
        foreach (var name in names)
        {
            checksum += PinyinMatcher.CreateSearchText(name).Length;
            checksum += (long)PinyinMatcher.Score(query, name);
        }

        return checksum;
    }

    private static async Task<TimedResult<T>> MeasureAsync<T>(Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await action();
        stopwatch.Stop();
        return new TimedResult<T>(result, stopwatch.Elapsed.TotalMilliseconds);
    }

    private static void AddSyncMeasurement<T>(
        Func<T> action,
        ICollection<double> timings,
        ICollection<long> allocations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        _ = action();
        stopwatch.Stop();
        allocations.Add(GC.GetAllocatedBytesForCurrentThread() - before);
        timings.Add(stopwatch.Elapsed.TotalMilliseconds);
    }

    private static void PrintComparison(
        string name,
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> optimized,
        string details)
    {
        var baselineMedian = Median(baseline);
        var optimizedMedian = Median(optimized);
        var speedup = optimizedMedian == 0 ? double.PositiveInfinity : baselineMedian / optimizedMedian;
        Console.WriteLine(
            $"{name}\tbaseline_median={baselineMedian:F3}ms\toptimized_median={optimizedMedian:F3}ms\t" +
            $"speedup={speedup:F2}x\tbaseline_p95={Percentile(baseline, 0.95):F3}ms\t" +
            $"optimized_p95={Percentile(optimized, 0.95):F3}ms\t{details}");
    }

    private static double Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

    private static long Median(IReadOnlyList<long> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered[ordered.Length / 2];
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static void AssertSameRecords(
        string operation,
        IReadOnlyList<FileRecord> baseline,
        IReadOnlyList<FileRecord> optimized)
    {
        var baselinePaths = baseline.Select(record => record.FullPath);
        var optimizedPaths = optimized.Select(record => record.FullPath);
        if (!baselinePaths.SequenceEqual(optimizedPaths, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"{operation} changed the ordered record sequence.");
        }
    }

    private static void AssertSameResults(
        string operation,
        IReadOnlyList<SearchResult> baseline,
        IReadOnlyList<SearchResult> optimized)
    {
        var equal = baseline.Count == optimized.Count;
        for (var index = 0; equal && index < baseline.Count; index++)
        {
            equal = string.Equals(
                    baseline[index].Record.FullPath,
                    optimized[index].Record.FullPath,
                    StringComparison.Ordinal) &&
                baseline[index].Score == optimized[index].Score &&
                string.Equals(baseline[index].MatchReason, optimized[index].MatchReason, StringComparison.Ordinal);
        }

        if (!equal)
        {
            throw new InvalidOperationException($"{operation} changed ranked results.");
        }
    }

    private static void AddRecords(
        IDictionary<string, FileRecord> destination,
        IEnumerable<FileRecord> records)
    {
        foreach (var record in records)
        {
            destination.TryAdd(record.PathKey, record);
        }
    }

    private static int CreateCandidateLimit(SearchQuery query)
        => Math.Clamp(query.Limit * CandidateMultiplier, MinimumCandidateLimit, MaximumCandidateLimit);

    private static string CreateOrderedLikePattern(string value)
        => "%" + string.Join('%', value.Select(EscapeLike)) + "%";

    private static string EscapeLike(char value)
        => value is '%' or '_' or '\\' ? "\\" + value : value.ToString();

    private static string EscapeLike(string value)
        => string.Concat(value.Select(EscapeLike));

    private static void ValidatePlainQuery(SearchQuery query)
    {
        if (query.PreferredRoot is null ||
            query.Parsed.Extensions.Count > 0 ||
            query.Parsed.ExcludedExtensions.Count > 0 ||
            query.Parsed.PathTerms.Count > 0 ||
            query.Parsed.Phrases.Count > 0 ||
            query.Parsed.ExcludedTerms.Count > 0 ||
            query.Parsed.FileOnly ||
            query.Parsed.ModeOverride is not null)
        {
            throw new ArgumentException("The A/B suite requires a preferred root and a plain text query.");
        }
    }

    private static bool TryParseOptions(string[] args, out BenchmarkOptions options)
    {
        options = default!;
        if (args.Length < 5)
        {
            return false;
        }

        var databasePath = args[0];
        string? root = null;
        string? query = null;
        var iterations = 7;
        var sample = 10_000;
        var upserts = 2_000;
        for (var index = 1; index < args.Length; index++)
        {
            if (index + 1 >= args.Length)
            {
                return false;
            }

            var value = args[++index];
            switch (args[index - 1])
            {
                case "--root":
                    root = value;
                    break;
                case "--query":
                    query = value;
                    break;
                case "--iterations" when int.TryParse(value, out var parsedIterations):
                    iterations = parsedIterations;
                    break;
                case "--sample" when int.TryParse(value, out var parsedSample):
                    sample = parsedSample;
                    break;
                case "--upserts" when int.TryParse(value, out var parsedUpserts):
                    upserts = parsedUpserts;
                    break;
                default:
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(databasePath) ||
            string.IsNullOrWhiteSpace(root) ||
            string.IsNullOrWhiteSpace(query) ||
            iterations < 3 || sample < 100 || upserts < 100)
        {
            return false;
        }

        options = new BenchmarkOptions(databasePath, root, query, iterations, sample, upserts);
        return true;
    }

    private static string CreateTemporaryDatabasePath(string suffix)
        => Path.Combine(Path.GetTempPath(), $"ListaryOpen-ab-{suffix}-{Guid.NewGuid():N}.db");

    private static void DeleteSqliteFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private const string UpsertSql = """
        insert into files (
            full_path, path_key, name, parent_path, search_text,
            is_directory, size_bytes, last_write_time, index_generation
        )
        values (
            $full_path, $path_key, $name, $parent_path, $search_text,
            $is_directory, $size_bytes, $last_write_time, $index_generation
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

    private sealed record BenchmarkOptions(
        string DatabasePath,
        string PreferredRoot,
        string Query,
        int Iterations,
        int SampleSize,
        int UpsertCount);

    private sealed record CandidatePipelineResult(
        IReadOnlyList<SearchResult> Results,
        int CandidateCount,
        bool SkippedAnyPass);

    private readonly record struct TimedResult<T>(T Result, double Milliseconds);
    private readonly record struct DatabaseSnapshot(long Count, long SizeBytes, long SearchTextCharacters);
}
