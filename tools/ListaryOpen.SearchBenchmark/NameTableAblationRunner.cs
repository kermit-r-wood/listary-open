using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text.Json;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Search;
using ListaryOpen.Infrastructure.Search.NameTable;

namespace ListaryOpen.SearchBenchmark;

/// <summary>
/// Controlled NameTable/SQLite search ablation for design G11 / Appendix D §D.2.
/// Use a single-backend run for attributable memory numbers and a both-backend run
/// for semantic and latency comparisons on an identical deterministic corpus.
/// </summary>
internal static class NameTableAblationRunner
{
    private const int MinimumCorpusSize = 1_000;
    private const int G11MinimumCorpusSize = 1_000_000;
    private const int ProductionGateCorpusSize = 5_000_000;
    private const int RequiredMeasuredIterations = 20;
    private const int RequiredWarmupIterations = 5;
    private const int SeedBatchSize = 2_000;

    private static readonly DateTimeOffset CorpusTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly GoldenRecordDefinition[] GoldenRecords =
    [
        new(@"C:\Corpus", true, 1),
        new(@"C:\Corpus\Apps", true, 2),
        new(@"C:\Corpus\Docs", true, 3),
        new(@"C:\Corpus\Projects", true, 4),
        new(@"C:\Corpus\Projects\中大成赢", true, 5, CjkName: true),
        new(@"C:\Corpus\Other", true, 6),
        new(@"C:\Corpus\Preferred", true, 7),
        new(@"C:\Corpus\Hardlinks", true, 8),
        new(@"C:\Elsewhere", true, 9),
        new(@"D:\Corpus", true, 10),
        new(@"C:\Corpus\Apps\ListaryOpen.App.exe", false, 11),
        new(@"C:\Corpus\Apps\listary_open_hook_host.exe", false, 12),
        new(@"C:\Corpus\Docs\zz-needle-middle-target-suffix.log", false, 13),
        new(@"C:\Corpus\Docs\qx-alpha-unique.txt", false, 14),
        new(@"C:\Corpus\Docs\qx-beta-unique.txt", false, 15),
        new(@"C:\Corpus\Docs\qz-one-character.txt", false, 16),
        new(@"C:\Corpus\Docs\合同.docx", false, 17, CjkName: true),
        new(@"C:\Corpus\Docs\发票2026.xlsx", false, 18, CjkName: true),
        new(@"C:\Corpus\Docs\whitelist.txt", false, 19),
        new(@"C:\Corpus\Docs\height.md", false, 20),
        new(@"C:\Corpus\Projects\中大成赢\门房图纸.cad", false, 21, CjkName: true),
        new(@"C:\Corpus\Other\门房图纸.cad", false, 22, CjkName: true, RepeatedName: true),
        new(@"C:\Corpus\Docs\Invoice Final 2026.xlsx", false, 23),
        new(@"C:\Corpus\Docs\Invoice Final Archive 2026.xlsx", false, 24),
        new(@"C:\Corpus\Preferred\old-report.txt", false, 25),
        new(@"C:\Elsewhere\report", false, 26),
        new(@"C:\Corpus\Hardlinks\shared-alpha.bin", false, 777),
        new(@"C:\Corpus\Hardlinks\shared-beta.bin", false, 777, HardLinkAdditional: true),
        new(@"C:\Corpus\Docs\README.md", false, 29, RepeatedName: true),
        new(@"D:\Corpus\README.md", false, 30, RepeatedName: true)
    ];

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseOptions(args, out var options))
        {
            PrintUsage();
            return 2;
        }

        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "listary-open-g11-" + Guid.NewGuid().ToString("N") + ".db");
        SqliteSearchIndex? sqlite = null;
        NameTableSearchIndex? nameTable = null;

        try
        {
            Console.WriteLine(
                $"Building deterministic corpus: records={options.Records:N0} backend={options.BackendText}");

            if (options.IncludesSqlite)
            {
                sqlite = await SqliteSearchIndex.OpenAsync(dbPath, cancellationToken).ConfigureAwait(false);
                await sqlite.WaitForBackgroundMaintenanceAsync().ConfigureAwait(false);
            }

            if (options.IncludesNameTable)
            {
                nameTable = new NameTableSearchIndex();
            }

            var emptyMemory = StabilizeAndCaptureMemory();
            var allocatedBeforeBuild = GC.GetTotalAllocatedBytes(precise: false);
            var buildStopwatch = Stopwatch.StartNew();
            var corpus = await SeedBackendsAsync(
                    options.Records,
                    sqlite,
                    nameTable,
                    cancellationToken)
                .ConfigureAwait(false);
            nameTable?.Engine.Optimize();
            buildStopwatch.Stop();
            var buildAllocatedBytes = Math.Max(
                0,
                GC.GetTotalAllocatedBytes(precise: false) - allocatedBeforeBuild);
            var loadedMemory = StabilizeAndCaptureMemory();

            var queryReports = new List<QueryReport>();
            foreach (var golden in BuildGoldenQueries())
            {
                queryReports.Add(await MeasureQueryAsync(
                        golden,
                        sqlite,
                        nameTable,
                        options.WarmupIterations,
                        options.Iterations,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            var slices = queryReports
                .GroupBy(result => result.Slice, StringComparer.Ordinal)
                .Select(group => CreateSliceResult(group.Key, group))
                .ToList();
            slices.Add(CreateSliceResult("full_golden", queryReports));

            var finalMemory = StabilizeAndCaptureMemory();
            var memory = CreateMemoryReport(
                options.BackendText,
                options.Records,
                emptyMemory,
                loadedMemory,
                finalMemory,
                buildAllocatedBytes,
                buildStopwatch.Elapsed.TotalSeconds);

            var full = slices.Single(slice => slice.Slice == "full_golden");
            var substring = slices.Single(slice => slice.Slice == "substring");
            bool? semanticEquivalence = options.Backend == BackendMode.Both
                ? queryReports.All(result => result.SemanticOk == true)
                : null;
            bool? noP95Regression = options.Backend == BackendMode.Both
                ? slices
                    .Where(slice => slice.Slice != "full_golden")
                    .All(slice => slice.P95Speedup is not null && slice.P95Speedup.Value >= 1.0 / 1.1)
                : null;
            var corpusContractOk = corpus.NonZeroFrnEntries == options.Records
                && corpus.DirectoryEntries > 0
                && corpus.CjkNameEntries > 0
                && corpus.RepeatedNameEntries > 0
                && corpus.HardLinkAdditionalRate >= 0.009;
            var g11Eligible = options.Backend == BackendMode.Both
                && options.Records >= G11MinimumCorpusSize
                && options.Iterations >= RequiredMeasuredIterations
                && options.WarmupIterations >= RequiredWarmupIterations
                && corpusContractOk;
            var g11Pass = g11Eligible
                && semanticEquivalence == true
                && noP95Regression == true
                && full.P50Speedup >= 2.0
                && substring.P50Speedup >= 3.0;
            var productionGatePass = g11Pass && options.Records >= ProductionGateCorpusSize;

            var report = new AblationReport
            {
                Records = options.Records,
                Iterations = options.Iterations,
                WarmupIterations = options.WarmupIterations,
                Backend = options.BackendText,
                GeneratedAt = DateTimeOffset.UtcNow,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ProcessorCount = Environment.ProcessorCount,
                Corpus = corpus,
                Memory = memory,
                NameTableMemory = nameTable?.Engine.GetMemoryStats(),
                Queries = queryReports,
                Slices = slices,
                SemanticEquivalence = semanticEquivalence,
                NoSliceP95Regression = noP95Regression,
                OverallGeoMeanP50Speedup = full.P50Speedup,
                SubstringGeoMeanP50Speedup = substring.P50Speedup,
                G11Eligible = g11Eligible,
                G11Pass = g11Pass,
                ProductionGatePass = productionGatePass,
                Notes = CreateNotes(options, corpusContractOk, g11Pass, productionGatePass)
            };

            WriteConsole(report);
            if (!string.IsNullOrWhiteSpace(options.JsonPath))
            {
                var directory = Path.GetDirectoryName(options.JsonPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(
                        options.JsonPath,
                        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                        cancellationToken)
                    .ConfigureAwait(false);
                Console.WriteLine($"JSON: {Path.GetFullPath(options.JsonPath)}");
            }

            // Producing a failing report is a successful benchmark run. G11Pass and
            // ProductionGatePass are the fail-closed machine-readable decisions.
            return 0;
        }
        finally
        {
            if (nameTable is not null)
            {
                await nameTable.DisposeAsync().ConfigureAwait(false);
            }

            if (sqlite is not null)
            {
                await sqlite.DisposeAsync().ConfigureAwait(false);
            }

            DeleteSqliteFiles(dbPath);
        }
    }

    private static async Task<CorpusProfile> SeedBackendsAsync(
        int recordCount,
        SqliteSearchIndex? sqlite,
        NameTableSearchIndex? nameTable,
        CancellationToken cancellationToken)
    {
        var statistics = new CorpusStatisticsAccumulator();
        var sqliteBulkStarted = false;
        try
        {
            if (sqlite is not null)
            {
                await sqlite.BeginBulkIndexingAsync(cancellationToken).ConfigureAwait(false);
                sqliteBulkStarted = true;
            }

            for (var offset = 0; offset < recordCount; offset += SeedBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(SeedBatchSize, recordCount - offset);
                var batch = BuildCorpusBatch(offset, count, statistics);
                if (sqlite is not null)
                {
                    await sqlite.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);
                }

                if (nameTable is not null)
                {
                    await nameTable.UpsertManyAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }

            if (sqliteBulkStarted)
            {
                await sqlite!.EndBulkIndexingAsync(cancellationToken).ConfigureAwait(false);
                sqliteBulkStarted = false;
                await sqlite.WaitForBackgroundMaintenanceAsync().ConfigureAwait(false);
            }

            return statistics.CreateProfile(recordCount);
        }
        catch
        {
            if (sqliteBulkStarted && sqlite is not null)
            {
                await sqlite.AbortBulkIndexingAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static FileRecord[] BuildCorpusBatch(
        int offset,
        int count,
        CorpusStatisticsAccumulator statistics)
    {
        var records = new FileRecord[count];
        for (var index = 0; index < count; index++)
        {
            var globalIndex = offset + index;
            var generated = CreateCorpusRecord(globalIndex);
            records[index] = generated.Record;
            statistics.Add(generated);
        }

        return records;
    }

    private static GeneratedRecord CreateCorpusRecord(int globalIndex)
    {
        if (globalIndex < GoldenRecords.Length)
        {
            var definition = GoldenRecords[globalIndex];
            return new GeneratedRecord(
                FileRecord.Create(
                    definition.Path,
                    definition.IsDirectory,
                    definition.IsDirectory ? 0 : 100 + globalIndex,
                    CorpusTimestamp.AddMinutes(globalIndex),
                    definition.FileReferenceNumber),
                definition.HardLinkAdditional,
                definition.RepeatedName,
                definition.CjkName);
        }

        var generatedIndex = globalIndex - GoldenRecords.Length;
        var group = generatedIndex / 10;
        var slot = generatedIndex % 10;
        var drive = (group & 1) == 0 ? "C:" : "D:";
        var branch = group % 5 == 0 ? "项目" : "Generated";
        var groupPath = $@"{drive}\Corpus\{branch}\bucket-{group % 128:D3}\group-{group:D7}";
        var isDirectory = slot == 0;
        var repeatedName = slot is 1 or 2 or 3;
        var cjkName = slot == 3;
        var hardLinkAdditional = slot == 9 && group % 10 == 9;
        var fileReferenceNumber = 1_000_000UL + (ulong)generatedIndex;
        if (hardLinkAdditional)
        {
            fileReferenceNumber--;
        }

        var path = slot switch
        {
            0 => groupPath,
            1 => Path.Combine(groupPath, "README.md"),
            2 => Path.Combine(groupPath, "index.js"),
            3 => Path.Combine(groupPath, "合同.txt"),
            4 => Path.Combine(groupPath, $"invoice-{group:D7}-2025.txt"),
            5 => Path.Combine(groupPath, $"asset-{group:D7}-needle-payload.bin"),
            6 => Path.Combine(groupPath, $"ListaryOpen.Component{group:D7}.dll"),
            7 => Path.Combine(groupPath, $"archive-report-{group:D7}.pdf"),
            8 => Path.Combine(groupPath, $"unique-source-{group:D7}.dat"),
            9 => Path.Combine(groupPath, $"hardlink-copy-{group:D7}.dat"),
            _ => throw new UnreachableException()
        };

        return new GeneratedRecord(
            FileRecord.Create(
                path,
                isDirectory,
                isDirectory ? 0 : generatedIndex % 1_000_000,
                CorpusTimestamp.AddSeconds(generatedIndex % 31_536_000),
                fileReferenceNumber),
            hardLinkAdditional,
            repeatedName,
            cjkName);
    }

    private static IReadOnlyList<GoldenQuery> BuildGoldenQueries() =>
    [
        new("prefix-camel", "prefix", "ListaryOpen"),
        new("prefix-duplicate", "prefix", "read"),
        new("substring-rare", "substring", "middle-target"),
        new("substring-common", "substring", "needle"),
        new("substring-miss", "miss", "definitely-no-such-token-9f4c2b"),
        new("short-one", "short", "q"),
        new("short-two", "short", "qx"),
        new("pinyin-full", "pinyin", "hetong"),
        new("pinyin-initials", "pinyin", "ht"),
        new("path-ordered", "path", @"path:Projects\中大 图纸"),
        new("multi-term-reversed", "multi_term", "2026 invoice"),
        new("phrase", "multi_term", "\"invoice final\""),
        new("preferred-root", "preferred_root", "report", @"C:\Corpus\Preferred")
    ];

    private static async Task<QueryReport> MeasureQueryAsync(
        GoldenQuery golden,
        SqliteSearchIndex? sqlite,
        NameTableSearchIndex? nameTable,
        int warmupIterations,
        int measuredIterations,
        CancellationToken cancellationToken)
    {
        var query = new SearchQuery(
            golden.Text,
            SearchMode.FilesAndFolders,
            limit: 50,
            preferredRoot: golden.PreferredRoot);

        for (var iteration = 0; iteration < warmupIterations; iteration++)
        {
            if (sqlite is not null && nameTable is not null && (iteration & 1) != 0)
            {
                _ = await nameTable.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                _ = await sqlite.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (sqlite is not null)
                {
                    _ = await sqlite.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                }

                if (nameTable is not null)
                {
                    _ = await nameTable.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var sqliteSamples = sqlite is null ? null : new MeasurementAccumulator();
        var nameTableSamples = nameTable is null ? null : new MeasurementAccumulator();
        IReadOnlyList<SearchResult>? sqliteResults = null;
        IReadOnlyList<SearchResult>? nameTableResults = null;

        for (var iteration = 0; iteration < measuredIterations; iteration++)
        {
            if (sqlite is not null && nameTable is not null && (iteration & 1) != 0)
            {
                nameTableResults = await MeasureOnceAsync(
                        nameTable,
                        query,
                        nameTableSamples!,
                        cancellationToken)
                    .ConfigureAwait(false);
                sqliteResults = await MeasureOnceAsync(
                        sqlite,
                        query,
                        sqliteSamples!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                if (sqlite is not null)
                {
                    sqliteResults = await MeasureOnceAsync(
                            sqlite,
                            query,
                            sqliteSamples!,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (nameTable is not null)
                {
                    nameTableResults = await MeasureOnceAsync(
                            nameTable,
                            query,
                            nameTableSamples!,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        bool? semanticOk = null;
        string? mismatch = null;
        if (sqliteResults is not null && nameTableResults is not null)
        {
            semanticOk = TryCompareOrderedResults(sqliteResults, nameTableResults, out mismatch);
        }

        return new QueryReport
        {
            Id = golden.Id,
            Slice = golden.Slice,
            Text = golden.Text,
            PreferredRoot = golden.PreferredRoot,
            Sqlite = sqliteSamples?.CreateReport(sqliteResults?.Count ?? 0),
            NameTable = nameTableSamples?.CreateReport(nameTableResults?.Count ?? 0),
            SemanticOk = semanticOk,
            SemanticMismatch = mismatch
        };
    }

    private static async Task<IReadOnlyList<SearchResult>> MeasureOnceAsync(
        ISearchIndex index,
        SearchQuery query,
        MeasurementAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        using var measurement = PerformanceMetrics.Begin("nametable-ab.query");
        var results = await index.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        var metric = measurement.Complete("ok", results.Count);
        stopwatch.Stop();
        var allocated = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
        accumulator.Add(stopwatch.Elapsed.TotalMilliseconds, allocated, metric.Stages);
        return results;
    }

    private static bool TryCompareOrderedResults(
        IReadOnlyList<SearchResult> sqlite,
        IReadOnlyList<SearchResult> nameTable,
        out string? mismatch)
    {
        if (sqlite.Count != nameTable.Count)
        {
            mismatch = $"result-count sqlite={sqlite.Count} nametable={nameTable.Count}";
            return false;
        }

        for (var index = 0; index < sqlite.Count; index++)
        {
            var expected = sqlite[index];
            var actual = nameTable[index];
            if (!string.Equals(expected.Record.PathKey, actual.Record.PathKey, StringComparison.Ordinal)
                || expected.Score != actual.Score
                || !string.Equals(expected.MatchReason, actual.MatchReason, StringComparison.Ordinal))
            {
                mismatch =
                    $"rank={index} sqlite='{expected.Record.FullPath}'/{expected.MatchReason}/{expected.Score:R} " +
                    $"nametable='{actual.Record.FullPath}'/{actual.MatchReason}/{actual.Score:R}";
                return false;
            }
        }

        mismatch = null;
        return true;
    }

    private static SliceResult CreateSliceResult(
        string slice,
        IEnumerable<QueryReport> source)
    {
        var queries = source.ToArray();
        var sqlite = queries
            .Where(query => query.Sqlite is not null)
            .Select(query => query.Sqlite!)
            .ToArray();
        var nameTable = queries
            .Where(query => query.NameTable is not null)
            .Select(query => query.NameTable!)
            .ToArray();

        double? sqliteP50 = sqlite.Length == 0 ? null : GeoMean(sqlite.Select(item => item.P50Milliseconds));
        double? sqliteP95 = sqlite.Length == 0 ? null : GeoMean(sqlite.Select(item => item.P95Milliseconds));
        double? sqliteP99 = sqlite.Length == 0 ? null : GeoMean(sqlite.Select(item => item.P99Milliseconds));
        double? nameP50 = nameTable.Length == 0 ? null : GeoMean(nameTable.Select(item => item.P50Milliseconds));
        double? nameP95 = nameTable.Length == 0 ? null : GeoMean(nameTable.Select(item => item.P95Milliseconds));
        double? nameP99 = nameTable.Length == 0 ? null : GeoMean(nameTable.Select(item => item.P99Milliseconds));
        var semanticComparisons = queries
            .Where(query => query.SemanticOk is not null)
            .Select(query => query.SemanticOk!.Value)
            .ToArray();

        return new SliceResult
        {
            Slice = slice,
            QueryCount = queries.Length,
            SqliteGeoMeanP50Milliseconds = sqliteP50,
            SqliteGeoMeanP95Milliseconds = sqliteP95,
            SqliteGeoMeanP99Milliseconds = sqliteP99,
            NameTableGeoMeanP50Milliseconds = nameP50,
            NameTableGeoMeanP95Milliseconds = nameP95,
            NameTableGeoMeanP99Milliseconds = nameP99,
            SqliteMedianAllocatedBytes = sqlite.Length == 0
                ? null
                : Median(sqlite.Select(item => item.MedianAllocatedBytes)),
            NameTableMedianAllocatedBytes = nameTable.Length == 0
                ? null
                : Median(nameTable.Select(item => item.MedianAllocatedBytes)),
            P50Speedup = Ratio(sqliteP50, nameP50),
            P95Speedup = Ratio(sqliteP95, nameP95),
            P99Speedup = Ratio(sqliteP99, nameP99),
            SemanticOk = semanticComparisons.Length == 0 ? null : semanticComparisons.All(value => value)
        };
    }

    private static MemoryReport CreateMemoryReport(
        string scope,
        int recordCount,
        ProcessMemorySnapshot empty,
        ProcessMemorySnapshot loaded,
        ProcessMemorySnapshot afterQueries,
        long buildAllocatedBytes,
        double buildSeconds)
    {
        var privateDelta = Math.Max(0, loaded.PrivateBytes - empty.PrivateBytes);
        var workingSetDelta = Math.Max(0, loaded.WorkingSetBytes - empty.WorkingSetBytes);
        var managedDelta = Math.Max(0, loaded.ManagedHeapBytes - empty.ManagedHeapBytes);
        var afterQueriesPrivateDelta = Math.Max(0, afterQueries.PrivateBytes - empty.PrivateBytes);
        var afterQueriesWorkingSetDelta = Math.Max(0, afterQueries.WorkingSetBytes - empty.WorkingSetBytes);
        return new MemoryReport
        {
            Scope = scope == "both" ? "combined-sqlite-and-nametable" : scope,
            Empty = empty,
            Loaded = loaded,
            AfterQueries = afterQueries,
            PrivateBytesDelta = privateDelta,
            WorkingSetBytesDelta = workingSetDelta,
            ManagedHeapBytesDelta = managedDelta,
            PrivateBytesPerRecord = privateDelta / (double)recordCount,
            WorkingSetBytesPerRecord = workingSetDelta / (double)recordCount,
            ManagedHeapBytesPerRecord = managedDelta / (double)recordCount,
            AfterQueriesPrivateBytesDelta = afterQueriesPrivateDelta,
            AfterQueriesWorkingSetBytesDelta = afterQueriesWorkingSetDelta,
            AfterQueriesPrivateBytesPerRecord = afterQueriesPrivateDelta / (double)recordCount,
            AfterQueriesWorkingSetBytesPerRecord = afterQueriesWorkingSetDelta / (double)recordCount,
            BuildAllocatedBytes = buildAllocatedBytes,
            BuildAllocatedBytesPerRecord = buildAllocatedBytes / (double)recordCount,
            BuildSeconds = buildSeconds,
            BuildRecordsPerSecond = buildSeconds <= 0 ? 0 : recordCount / buildSeconds
        };
    }

    private static ProcessMemorySnapshot StabilizeAndCaptureMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        return CaptureMemory();
    }

    private static ProcessMemorySnapshot CaptureMemory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProcessMemorySnapshot
        {
            PrivateBytes = process.PrivateMemorySize64,
            WorkingSetBytes = process.WorkingSet64,
            PeakWorkingSetBytes = process.PeakWorkingSet64,
            ManagedHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
            ManagedLiveBytes = GC.GetTotalMemory(forceFullCollection: false)
        };
    }

    private static double GeoMean(IEnumerable<double> values)
    {
        var materialized = values.ToArray();
        if (materialized.Length == 0)
        {
            return 0;
        }

        return Math.Exp(materialized.Sum(value => Math.Log(Math.Max(value, 1e-9))) / materialized.Length);
    }

    private static double? Ratio(double? numerator, double? denominator)
    {
        if (numerator is null || denominator is null)
        {
            return null;
        }

        return numerator.Value / Math.Max(denominator.Value, 1e-9);
    }

    private static long Median(IEnumerable<long> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
    }

    private static string CreateNotes(
        BenchmarkOptions options,
        bool corpusContractOk,
        bool g11Pass,
        bool productionGatePass)
    {
        if (productionGatePass)
        {
            return "Production G11 gate passed at 5M+ with semantic, p50, and p95 gates green.";
        }

        if (g11Pass)
        {
            return "G11 passed at 1M+; run the identical locked corpus at 5M for the production gate.";
        }

        if (options.Backend != BackendMode.Both)
        {
            return "Single-backend run: memory is attributable, but semantic/speedup G11 gates require --backend both.";
        }

        if (!corpusContractOk)
        {
            return "Corpus contract failed; G11 is ineligible.";
        }

        if (options.Records < G11MinimumCorpusSize)
        {
            return "Diagnostic run only: G11 requires at least 1,000,000 records; production requires 5,000,000.";
        }

        if (options.Iterations < RequiredMeasuredIterations || options.WarmupIterations < RequiredWarmupIterations)
        {
            return "Diagnostic run only: G11 requires 5 warmups and 20 measured iterations.";
        }

        return "G11 failed one or more semantic or latency gates; continue optimization before cutover.";
    }

    private static void WriteConsole(AblationReport report)
    {
        Console.WriteLine("=== NameTable / SQLite ablation (G11) ===");
        Console.WriteLine(
            $"records={report.Records:N0} backend={report.Backend} warmup={report.WarmupIterations} " +
            $"iterations={report.Iterations}");
        Console.WriteLine(
            $"corpus directories={report.Corpus.DirectoryEntries:N0} cjk_names={report.Corpus.CjkNameEntries:N0} " +
            $"repeated_names={report.Corpus.RepeatedNameEntries:N0} hardlink_extra_rate={report.Corpus.HardLinkAdditionalRate:P2} " +
            $"nonzero_frn={report.Corpus.NonZeroFrnEntries:N0}");
        Console.WriteLine(
            $"memory scope={report.Memory.Scope} loaded_private={FormatBytes(report.Memory.Loaded.PrivateBytes)} " +
            $"loaded_working_set={FormatBytes(report.Memory.Loaded.WorkingSetBytes)} " +
            $"query_warm_private={FormatBytes(report.Memory.AfterQueries.PrivateBytes)} " +
            $"query_warm_working_set={FormatBytes(report.Memory.AfterQueries.WorkingSetBytes)} " +
            $"peak_working_set={FormatBytes(report.Memory.AfterQueries.PeakWorkingSetBytes)} " +
            $"private_delta={FormatBytes(report.Memory.PrivateBytesDelta)} " +
            $"private_bytes_per_record={report.Memory.PrivateBytesPerRecord:F2} " +
            $"query_warm_private_bytes_per_record={report.Memory.AfterQueriesPrivateBytesPerRecord:F2} " +
            $"working_set_bytes_per_record={report.Memory.WorkingSetBytesPerRecord:F2} " +
            $"managed_bytes_per_record={report.Memory.ManagedHeapBytesPerRecord:F2}");
        Console.WriteLine(
            $"build seconds={report.Memory.BuildSeconds:F2} records_per_second={report.Memory.BuildRecordsPerSecond:N0} " +
            $"gc_allocated={FormatBytes(report.Memory.BuildAllocatedBytes)} " +
            $"gc_allocated_per_record={report.Memory.BuildAllocatedBytesPerRecord:F2}");
        if (report.NameTableMemory is { } engineMemory)
        {
            Console.WriteLine(
                $"nametable_compact live={engineMemory.LiveRecords:N0} unique_names={engineMemory.UniqueNames:N0} " +
                $"retained={FormatBytes(engineMemory.EstimatedRetainedBytes)} " +
                $"retained_bytes_per_live={engineMemory.BytesPerLiveRecord:F2} " +
                $"name_heap={FormatBytes(engineMemory.NameHeapBytes)} " +
                $"sorted_indexes={FormatBytes(engineMemory.SortedIndexBytes)} " +
                $"build_indexes={FormatBytes(engineMemory.BuildIndexBytes)}");
        }

        foreach (var query in report.Queries)
        {
            Console.WriteLine(
                $"query={query.Id} slice={query.Slice} text='{query.Text}' " +
                $"sqlite={FormatQueryMeasurement(query.Sqlite)} nametable={FormatQueryMeasurement(query.NameTable)} " +
                $"semantic={FormatNullableBoolean(query.SemanticOk)}" +
                (query.SemanticMismatch is null ? string.Empty : $" mismatch=\"{query.SemanticMismatch}\""));
        }

        foreach (var slice in report.Slices)
        {
            Console.WriteLine(
                $"slice={slice.Slice} queries={slice.QueryCount} " +
                $"sqlite_p50/p95/p99={FormatMilliseconds(slice.SqliteGeoMeanP50Milliseconds)}/" +
                $"{FormatMilliseconds(slice.SqliteGeoMeanP95Milliseconds)}/" +
                $"{FormatMilliseconds(slice.SqliteGeoMeanP99Milliseconds)} " +
                $"nametable_p50/p95/p99={FormatMilliseconds(slice.NameTableGeoMeanP50Milliseconds)}/" +
                $"{FormatMilliseconds(slice.NameTableGeoMeanP95Milliseconds)}/" +
                $"{FormatMilliseconds(slice.NameTableGeoMeanP99Milliseconds)} " +
                $"speedup_p50/p95/p99={FormatRatio(slice.P50Speedup)}/" +
                $"{FormatRatio(slice.P95Speedup)}/{FormatRatio(slice.P99Speedup)} " +
                $"semantic={FormatNullableBoolean(slice.SemanticOk)}");
        }

        Console.WriteLine(
            $"semantic_ok={FormatNullableBoolean(report.SemanticEquivalence)} " +
            $"no_slice_p95_regression={FormatNullableBoolean(report.NoSliceP95Regression)} " +
            $"overall_p50_speedup={FormatRatio(report.OverallGeoMeanP50Speedup)} " +
            $"substring_p50_speedup={FormatRatio(report.SubstringGeoMeanP50Speedup)}");
        Console.WriteLine(
            $"g11_eligible={report.G11Eligible} g11_pass={report.G11Pass} " +
            $"production_gate_pass={report.ProductionGatePass}");
        Console.WriteLine(report.Notes);
    }

    private static string FormatQueryMeasurement(BackendQueryReport? value)
    {
        return value is null
            ? "n/a"
            : $"{value.P50Milliseconds:F3}/{value.P95Milliseconds:F3}/{value.P99Milliseconds:F3}ms," +
              $"alloc={FormatBytes(value.MedianAllocatedBytes)},count={value.ResultCount}";
    }

    private static string FormatMilliseconds(double? value) => value is null ? "n/a" : $"{value:F3}";

    private static string FormatRatio(double? value) => value is null ? "n/a" : $"{value:F2}x";

    private static string FormatNullableBoolean(bool? value) => value?.ToString() ?? "n/a";

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

    private static bool TryParseOptions(string[] args, out BenchmarkOptions options)
    {
        var records = G11MinimumCorpusSize;
        var iterations = RequiredMeasuredIterations;
        var warmupIterations = RequiredWarmupIterations;
        var backend = BackendMode.Both;
        string? jsonPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (string.Equals(option, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(option, "-h", StringComparison.OrdinalIgnoreCase))
            {
                options = default!;
                return false;
            }

            if (index + 1 >= args.Length)
            {
                options = default!;
                return false;
            }

            var value = args[++index];
            switch (option)
            {
                case "--records" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRecords):
                    records = parsedRecords;
                    break;
                case "--iterations" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIterations):
                    iterations = parsedIterations;
                    break;
                case "--warmup" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWarmup):
                    warmupIterations = parsedWarmup;
                    break;
                case "--backend" when TryParseBackend(value, out var parsedBackend):
                    backend = parsedBackend;
                    break;
                case "--json":
                    jsonPath = value;
                    break;
                default:
                    options = default!;
                    return false;
            }
        }

        if (records < MinimumCorpusSize || iterations <= 0 || warmupIterations < 0)
        {
            options = default!;
            return false;
        }

        options = new BenchmarkOptions(records, iterations, warmupIterations, backend, jsonPath);
        return true;
    }

    private static bool TryParseBackend(string value, out BackendMode backend)
    {
        if (string.Equals(value, "nametable", StringComparison.OrdinalIgnoreCase))
        {
            backend = BackendMode.NameTable;
            return true;
        }

        if (string.Equals(value, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            backend = BackendMode.Sqlite;
            return true;
        }

        if (string.Equals(value, "both", StringComparison.OrdinalIgnoreCase))
        {
            backend = BackendMode.Both;
            return true;
        }

        backend = default;
        return false;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: nametable-ab [--backend nametable|sqlite|both] [--records N] " +
            "[--warmup N] [--iterations N] [--json path]");
        Console.Error.WriteLine(
            "Defaults: --backend both --records 1000000 --warmup 5 --iterations 20. " +
            "Use isolated backend runs for attributable memory; 5M is the production gate.");
    }

    private static void DeleteSqliteFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup of an isolated benchmark database.
            }
        }
    }

    private enum BackendMode
    {
        NameTable,
        Sqlite,
        Both
    }

    private sealed record BenchmarkOptions(
        int Records,
        int Iterations,
        int WarmupIterations,
        BackendMode Backend,
        string? JsonPath)
    {
        public bool IncludesNameTable => Backend is BackendMode.NameTable or BackendMode.Both;

        public bool IncludesSqlite => Backend is BackendMode.Sqlite or BackendMode.Both;

        public string BackendText => Backend.ToString().ToLowerInvariant();
    }

    private sealed record GoldenQuery(
        string Id,
        string Slice,
        string Text,
        string? PreferredRoot = null);

    private sealed record GoldenRecordDefinition(
        string Path,
        bool IsDirectory,
        ulong FileReferenceNumber,
        bool HardLinkAdditional = false,
        bool RepeatedName = false,
        bool CjkName = false);

    private readonly record struct GeneratedRecord(
        FileRecord Record,
        bool HardLinkAdditional,
        bool RepeatedName,
        bool CjkName);

    private sealed class CorpusStatisticsAccumulator
    {
        private int _directories;
        private int _nonZeroFrn;
        private int _hardLinkAdditional;
        private int _repeatedNames;
        private int _cjkNames;

        public void Add(GeneratedRecord generated)
        {
            if (generated.Record.IsDirectory)
            {
                _directories++;
            }

            if (generated.Record.FileReferenceNumber != 0)
            {
                _nonZeroFrn++;
            }

            if (generated.HardLinkAdditional)
            {
                _hardLinkAdditional++;
            }

            if (generated.RepeatedName)
            {
                _repeatedNames++;
            }

            if (generated.CjkName)
            {
                _cjkNames++;
            }
        }

        public CorpusProfile CreateProfile(int records) => new()
        {
            DirectoryEntries = _directories,
            NonZeroFrnEntries = _nonZeroFrn,
            HardLinkAdditionalEntries = _hardLinkAdditional,
            HardLinkAdditionalRate = _hardLinkAdditional / (double)records,
            RepeatedNameEntries = _repeatedNames,
            CjkNameEntries = _cjkNames
        };
    }

    private sealed class MeasurementAccumulator
    {
        private readonly List<double> _milliseconds = new();
        private readonly List<long> _allocatedBytes = new();
        private readonly Dictionary<string, List<double>> _stageMilliseconds = new(StringComparer.Ordinal);

        public void Add(
            double milliseconds,
            long allocatedBytes,
            IReadOnlyDictionary<string, double> stages)
        {
            _milliseconds.Add(milliseconds);
            _allocatedBytes.Add(allocatedBytes);
            foreach (var stage in stages)
            {
                if (!_stageMilliseconds.TryGetValue(stage.Key, out var samples))
                {
                    samples = new List<double>();
                    _stageMilliseconds.Add(stage.Key, samples);
                }

                samples.Add(stage.Value);
            }
        }

        public BackendQueryReport CreateReport(int resultCount) => new()
        {
            ResultCount = resultCount,
            P50Milliseconds = Percentile(_milliseconds, 0.50),
            P95Milliseconds = Percentile(_milliseconds, 0.95),
            P99Milliseconds = Percentile(_milliseconds, 0.99),
            MedianAllocatedBytes = Percentile(_allocatedBytes, 0.50),
            P95AllocatedBytes = Percentile(_allocatedBytes, 0.95),
            StageP50Milliseconds = _stageMilliseconds.ToDictionary(
                stage => stage.Key,
                stage => Percentile(stage.Value, 0.50),
                StringComparer.Ordinal),
            StageP95Milliseconds = _stageMilliseconds.ToDictionary(
                stage => stage.Key,
                stage => Percentile(stage.Value, 0.95),
                StringComparer.Ordinal)
        };

        private static double Percentile(IReadOnlyList<double> values, double percentile)
        {
            var ordered = values.OrderBy(value => value).ToArray();
            var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
            return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
        }

        private static long Percentile(IReadOnlyList<long> values, double percentile)
        {
            var ordered = values.OrderBy(value => value).ToArray();
            var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
            return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
        }
    }

    private sealed class AblationReport
    {
        public int Records { get; set; }
        public int Iterations { get; set; }
        public int WarmupIterations { get; set; }
        public string Backend { get; set; } = "";
        public DateTimeOffset GeneratedAt { get; set; }
        public string Runtime { get; set; } = "";
        public string OperatingSystem { get; set; } = "";
        public int ProcessorCount { get; set; }
        public CorpusProfile Corpus { get; set; } = new();
        public MemoryReport Memory { get; set; } = new();
        public NameTableMemoryStats? NameTableMemory { get; set; }
        public List<QueryReport> Queries { get; set; } = new();
        public List<SliceResult> Slices { get; set; } = new();
        public bool? SemanticEquivalence { get; set; }
        public bool? NoSliceP95Regression { get; set; }
        public double? OverallGeoMeanP50Speedup { get; set; }
        public double? SubstringGeoMeanP50Speedup { get; set; }
        public bool G11Eligible { get; set; }
        public bool G11Pass { get; set; }
        public bool ProductionGatePass { get; set; }
        public string Notes { get; set; } = "";
    }

    private sealed class CorpusProfile
    {
        public int DirectoryEntries { get; set; }
        public int NonZeroFrnEntries { get; set; }
        public int HardLinkAdditionalEntries { get; set; }
        public double HardLinkAdditionalRate { get; set; }
        public int RepeatedNameEntries { get; set; }
        public int CjkNameEntries { get; set; }
    }

    private sealed class QueryReport
    {
        public string Id { get; set; } = "";
        public string Slice { get; set; } = "";
        public string Text { get; set; } = "";
        public string? PreferredRoot { get; set; }
        public BackendQueryReport? Sqlite { get; set; }
        public BackendQueryReport? NameTable { get; set; }
        public bool? SemanticOk { get; set; }
        public string? SemanticMismatch { get; set; }
    }

    private sealed class BackendQueryReport
    {
        public int ResultCount { get; set; }
        public double P50Milliseconds { get; set; }
        public double P95Milliseconds { get; set; }
        public double P99Milliseconds { get; set; }
        public long MedianAllocatedBytes { get; set; }
        public long P95AllocatedBytes { get; set; }
        public Dictionary<string, double> StageP50Milliseconds { get; set; } = new();
        public Dictionary<string, double> StageP95Milliseconds { get; set; } = new();
    }

    private sealed class SliceResult
    {
        public string Slice { get; set; } = "";
        public int QueryCount { get; set; }
        public double? SqliteGeoMeanP50Milliseconds { get; set; }
        public double? SqliteGeoMeanP95Milliseconds { get; set; }
        public double? SqliteGeoMeanP99Milliseconds { get; set; }
        public double? NameTableGeoMeanP50Milliseconds { get; set; }
        public double? NameTableGeoMeanP95Milliseconds { get; set; }
        public double? NameTableGeoMeanP99Milliseconds { get; set; }
        public long? SqliteMedianAllocatedBytes { get; set; }
        public long? NameTableMedianAllocatedBytes { get; set; }
        public double? P50Speedup { get; set; }
        public double? P95Speedup { get; set; }
        public double? P99Speedup { get; set; }
        public bool? SemanticOk { get; set; }
    }

    private sealed class MemoryReport
    {
        public string Scope { get; set; } = "";
        public ProcessMemorySnapshot Empty { get; set; } = new();
        public ProcessMemorySnapshot Loaded { get; set; } = new();
        public ProcessMemorySnapshot AfterQueries { get; set; } = new();
        public long PrivateBytesDelta { get; set; }
        public long WorkingSetBytesDelta { get; set; }
        public long ManagedHeapBytesDelta { get; set; }
        public double PrivateBytesPerRecord { get; set; }
        public double WorkingSetBytesPerRecord { get; set; }
        public double ManagedHeapBytesPerRecord { get; set; }
        public long AfterQueriesPrivateBytesDelta { get; set; }
        public long AfterQueriesWorkingSetBytesDelta { get; set; }
        public double AfterQueriesPrivateBytesPerRecord { get; set; }
        public double AfterQueriesWorkingSetBytesPerRecord { get; set; }
        public long BuildAllocatedBytes { get; set; }
        public double BuildAllocatedBytesPerRecord { get; set; }
        public double BuildSeconds { get; set; }
        public double BuildRecordsPerSecond { get; set; }
    }

    private sealed class ProcessMemorySnapshot
    {
        public long PrivateBytes { get; set; }
        public long WorkingSetBytes { get; set; }
        public long PeakWorkingSetBytes { get; set; }
        public long ManagedHeapBytes { get; set; }
        public long ManagedLiveBytes { get; set; }
    }
}
