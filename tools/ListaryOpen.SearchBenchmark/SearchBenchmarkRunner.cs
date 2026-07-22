using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.SearchBenchmark;

internal static class SearchBenchmarkRunner
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: search <index.db> [--root <path>] <query> [query ...]");
            return 2;
        }

        var queryOffset = 1;
        string? preferredRoot = null;
        if (args.Length >= 4 && string.Equals(args[1], "--root", StringComparison.Ordinal))
        {
            preferredRoot = args[2];
            queryOffset = 3;
        }

        if (queryOffset >= args.Length)
        {
            Console.Error.WriteLine("At least one query is required.");
            return 2;
        }

        await using var index = await SqliteSearchIndex.OpenAsync(args[0], cancellationToken);
        foreach (var text in args.Skip(queryOffset))
        {
            var query = new SearchQuery(
                text,
                SearchMode.FilesAndFolders,
                preferredRoot: preferredRoot);
            using var coldMeasurement = PerformanceMetrics.Begin("search.benchmark.cold");
            var coldResults = await index.SearchAsync(query, cancellationToken);
            var coldSnapshot = coldMeasurement.Complete("success", coldResults.Count);

            var timings = new List<double>();
            var stageTimings = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            IReadOnlyList<SearchResult> results = Array.Empty<SearchResult>();
            for (var iteration = 0; iteration < 5; iteration++)
            {
                using var measurement = PerformanceMetrics.Begin("search.benchmark");
                results = await index.SearchAsync(query, cancellationToken);
                var snapshot = measurement.Complete("success", results.Count);
                timings.Add(snapshot.TotalMilliseconds);
                foreach (var stage in snapshot.Stages)
                {
                    if (!stageTimings.TryGetValue(stage.Key, out var values))
                    {
                        values = new List<double>();
                        stageTimings.Add(stage.Key, values);
                    }

                    values.Add(stage.Value);
                }
            }

            var first = results.FirstOrDefault();
            Console.WriteLine(
                $"{text}\tcount={results.Count}\tfirst={first?.Record.Name ?? "<none>"}\t" +
                $"reason={first?.MatchReason ?? "<none>"}\tcold={coldSnapshot.TotalMilliseconds:F1}ms\t" +
                $"warm={string.Join(',', timings.Select(value => value.ToString("F1")))}ms");
            Console.WriteLine(string.Join(
                '\t',
                stageTimings
                    .OrderBy(stage => stage.Key, StringComparer.Ordinal)
                    .Select(stage => $"{stage.Key}={stage.Value.Average():F2}ms")));
        }

        return 0;
    }
}
