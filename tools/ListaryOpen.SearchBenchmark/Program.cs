using System.Diagnostics;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Search;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ListaryOpen.SearchBenchmark <index.db> <query> [query ...]");
    return 2;
}

await using var index = await SqliteSearchIndex.OpenAsync(args[0], CancellationToken.None);
foreach (var text in args.Skip(1))
{
    _ = await index.SearchAsync(new SearchQuery(text, SearchMode.FilesAndFolders), CancellationToken.None);

    var timings = new List<double>();
    IReadOnlyList<SearchResult> results = Array.Empty<SearchResult>();
    for (var iteration = 0; iteration < 5; iteration++)
    {
        var stopwatch = Stopwatch.StartNew();
        results = await index.SearchAsync(
            new SearchQuery(text, SearchMode.FilesAndFolders),
            CancellationToken.None);
        stopwatch.Stop();
        timings.Add(stopwatch.Elapsed.TotalMilliseconds);
    }

    var first = results.FirstOrDefault();
    Console.WriteLine(
        $"{text}\tcount={results.Count}\tfirst={first?.Record.Name ?? "<none>"}\t" +
        $"reason={first?.MatchReason ?? "<none>"}\tms={string.Join(',', timings.Select(value => value.ToString("F1")))}");
}

return 0;
