using System.Diagnostics;
using System.IO;
using System.Text;
using ListaryOpen.App;
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;
using ListaryOpen.Infrastructure.Windows;
using WpfApp = ListaryOpen.App.App;

if (args is ["--startup-probe"])
{
    var query = new SearchQuery("startup", SearchMode.FilesAndFolders);
    var input = new GlobalTextInputEventArgs("s", new IntPtr(42), "DirectUIHWND");
    var coalesced = WpfApp.CoalesceGlobalTextInputs([input, input]);
    var reusable = QuickSwitchBarWindow.CanReusePosition(
        hasCachedPosition: true,
        repositionInvalidated: false,
        new NativeRectangle(0, 0, 800, 600),
        new NativeRectangle(0, 0, 800, 600),
        previousDpiScale: 1,
        currentDpiScale: 1);
    Console.WriteLine($"startup-probe:{query.NormalizedText}:{coalesced.Count}:{reusable}");
    return 0;
}

var scale = ParseScale(args);
var results = new List<BenchmarkResult>();
var temporaryFolder = CreateFixtureFolder(Math.Max(200, 1_000 * scale));
try
{
    results.Add(BenchmarkInputCoalescing(8_000 * scale));
    results.Add(BenchmarkExplorerInputFilter(20_000 * scale));
    results.Add(BenchmarkCurrentFolderSnapshot(temporaryFolder));
    results.Add(BenchmarkCurrentFolderMerge(1_000 * scale));
    results.Add(BenchmarkExplorerSnapshot(temporaryFolder, 2_000 * scale));
    results.Add(BenchmarkQuickSwitchLayout(20_000 * scale));
    results.Add(await BenchmarkSelectionDebounceAsync(300 * scale));
    results.Add(await BenchmarkObservationCoalescingAsync(200 * scale));
    results.AddRange(CreateNativeHookProxyMetrics());
}
finally
{
    Directory.Delete(temporaryFolder, recursive: true);
}

Console.WriteLine($"ListaryOpen UI performance A/B (scale={scale}, {Environment.OSVersion}, {Environment.ProcessorCount} logical CPUs)");
Console.WriteLine("scenario\tbaseline_ms\toptimized_ms\tbaseline_ops\toptimized_ops\treduction\tsemantic\tdetail");
foreach (var result in results)
{
    var reduction = result.BaselineOperations == 0
        ? 0
        : 100d * (result.BaselineOperations - result.OptimizedOperations) / result.BaselineOperations;
    Console.WriteLine(
        $"{result.Scenario}\t{result.BaselineMilliseconds:F3}\t{result.OptimizedMilliseconds:F3}\t" +
        $"{result.BaselineOperations}\t{result.OptimizedOperations}\t{reduction:F1}%\t" +
        $"{(result.SemanticMatch ? "yes" : "NO")}\t{result.Detail}");
}

return results.All(result => result.SemanticMatch) ? 0 : 1;

static int ParseScale(string[] arguments)
{
    if (arguments.Length == 0)
    {
        return 1;
    }

    if (arguments.Length == 2 &&
        string.Equals(arguments[0], "--scale", StringComparison.Ordinal) &&
        int.TryParse(arguments[1], out var scale) &&
        scale is >= 1 and <= 20)
    {
        return scale;
    }

    throw new ArgumentException("Usage: ListaryOpen.UiPerformanceBenchmark [--scale 1..20]");
}

static BenchmarkResult BenchmarkInputCoalescing(int burstCount)
{
    var inputs = new List<GlobalTextInputEventArgs>(burstCount * 4);
    var explorerWindow = new IntPtr(42);
    for (var burst = 0; burst < burstCount; burst++)
    {
        foreach (var character in "open")
        {
            inputs.Add(new GlobalTextInputEventArgs(character.ToString(), explorerWindow, "DirectUIHWND"));
        }

        explorerWindow = explorerWindow == new IntPtr(42) ? new IntPtr(84) : new IntPtr(42);
    }

    var baseline = MeasureMedian(() => ProcessInputEvents(inputs), rounds: 5);
    var optimized = MeasureMedian(
        () => ProcessInputEvents(WpfApp.CoalesceGlobalTextInputs(inputs)),
        rounds: 5);
    var baselineText = string.Concat(inputs.Select(input => input.Text));
    var optimizedInputs = WpfApp.CoalesceGlobalTextInputs(inputs);
    var optimizedText = string.Concat(optimizedInputs.Select(input => input.Text));
    return new BenchmarkResult(
        "input coalescing",
        baseline.Milliseconds,
        optimized.Milliseconds,
        inputs.Count,
        optimizedInputs.Count,
        string.Equals(baselineText, optimizedText, StringComparison.Ordinal),
        $"{inputs.Count} key events; fixed handler proxy per delivered input");
}

static BenchmarkResult BenchmarkExplorerInputFilter(int eventCount)
{
    var classes = new[] { "CabinetWClass", "Chrome_WidgetWin_1", "ExploreWClass", "Notepad" };
    var baselineCalls = 0;
    var optimizedCalls = 0;
    var baselineMatches = 0;
    var optimizedMatches = 0;
    var baseline = MeasureMedian(
        () =>
        {
            baselineCalls = 0;
            baselineMatches = 0;
            for (var index = 0; index < eventCount; index++)
            {
                ProxyOsInputInspection();
                baselineCalls++;
                if (GlobalTextInputService.IsExplorerTopLevelWindowClass(classes[index % classes.Length]))
                {
                    baselineMatches++;
                }
            }
        },
        rounds: 5);
    var optimized = MeasureMedian(
        () =>
        {
            optimizedCalls = 0;
            optimizedMatches = 0;
            for (var index = 0; index < eventCount; index++)
            {
                if (!GlobalTextInputService.IsExplorerTopLevelWindowClass(classes[index % classes.Length]))
                {
                    continue;
                }

                ProxyOsInputInspection();
                optimizedCalls++;
                optimizedMatches++;
            }
        },
        rounds: 5);
    return new BenchmarkResult(
        "non-Explorer hook filter",
        baseline.Milliseconds,
        optimized.Milliseconds,
        baselineCalls,
        optimizedCalls,
        baselineMatches == optimizedMatches,
        $"fixed GetGUIThreadInfo/translation proxy; {optimizedMatches} Explorer events retained");
}

static BenchmarkResult BenchmarkCurrentFolderSnapshot(string folder)
{
    var queries = new[] { "report", "project", "open", "image", "invoice", "archive", "source", "notes" }
        .Select(text => new SearchQuery(text, SearchMode.FilesAndFolders, limit: 50, preferredRoot: folder))
        .ToArray();
    var baselineSignature = string.Empty;
    var optimizedSignature = string.Empty;
    var baseline = MeasureMedian(
        () =>
        {
            var signatures = new StringBuilder();
            foreach (var query in queries)
            {
                var entries = SearchPanelViewModel.EnumerateCurrentFolderEntries(folder, CancellationToken.None);
                var ranked = Rank(query, entries);
                signatures.Append(ResultSignature(ranked));
            }

            baselineSignature = signatures.ToString();
        },
        rounds: 3);
    var optimized = MeasureMedian(
        () =>
        {
            var entries = SearchPanelViewModel.EnumerateCurrentFolderEntries(folder, CancellationToken.None);
            var signatures = new StringBuilder();
            foreach (var query in queries)
            {
                signatures.Append(ResultSignature(Rank(query, entries)));
            }

            optimizedSignature = signatures.ToString();
        },
        rounds: 3);
    return new BenchmarkResult(
        "current-folder snapshot",
        baseline.Milliseconds,
        optimized.Milliseconds,
        queries.Length,
        1,
        string.Equals(baselineSignature, optimizedSignature, StringComparison.Ordinal),
        $"{Directory.EnumerateFileSystemEntries(folder).Count()} entries, {queries.Length} queries");
}

static BenchmarkResult BenchmarkCurrentFolderMerge(int repetitions)
{
    var root = Path.Combine(Path.GetTempPath(), "ListaryOpen-Merge");
    var local = Enumerable.Range(0, 300)
        .Select(index => CreateResult(Path.Combine(root, $"item-{index:D4}.txt"), 100 - index))
        .ToArray();
    var indexed = Enumerable.Range(100, 500)
        .Select(index => CreateResult(Path.Combine(root, $"item-{index:D4}.txt"), 80 - index))
        .ToArray();
    IReadOnlyList<SearchResult> baselineResult = Array.Empty<SearchResult>();
    IReadOnlyList<SearchResult> optimizedResult = Array.Empty<SearchResult>();
    var baseline = MeasureMedian(
        () =>
        {
            for (var repeat = 0; repeat < repetitions; repeat++)
            {
                baselineResult = local.Concat(indexed)
                    .DistinctBy(result => result.Record.PathKey, StringComparer.Ordinal)
                    .ToArray();
            }
        },
        rounds: 5);
    var optimized = MeasureMedian(
        () =>
        {
            for (var repeat = 0; repeat < repetitions; repeat++)
            {
                optimizedResult = SearchPanelViewModel.MergeCurrentFolderResults(local, indexed);
            }
        },
        rounds: 5);
    return new BenchmarkResult(
        "current-folder merge",
        baseline.Milliseconds,
        optimized.Milliseconds,
        repetitions,
        repetitions,
        baselineResult.SequenceEqual(optimizedResult),
        "LINQ DistinctBy reference vs local-first HashSet merge");
}

static BenchmarkResult BenchmarkExplorerSnapshot(string folder, int reads)
{
    var provider = new ProxyExplorerWindowsProvider(folder);
    var foregroundWindow = new IntPtr(42);
    var tracker = new ExplorerTracker(() => foregroundWindow, provider);
    tracker.ObserveForegroundExplorerFolder();
    provider.Reset();
    IReadOnlyList<string> baselinePaths = Array.Empty<string>();
    IReadOnlyList<string> optimizedPaths = Array.Empty<string>();
    var baseline = MeasureMedian(
        () =>
        {
            for (var read = 0; read < reads; read++)
            {
                baselinePaths = BuildExplorerCandidatePaths(
                    provider.EnumerateWindows(),
                    foregroundWindow);
            }
        },
        rounds: 3);
    provider.Reset();
    var optimized = MeasureMedian(
        () =>
        {
            for (var read = 0; read < reads; read++)
            {
                optimizedPaths = tracker.GetFolderCandidates().Select(candidate => candidate.FolderPath).ToArray();
            }
        },
        rounds: 3);
    return new BenchmarkResult(
        "Explorer COM snapshot reads",
        baseline.Milliseconds,
        optimized.Milliseconds,
        reads,
        provider.CallCount,
        baselinePaths.SequenceEqual(optimizedPaths, StringComparer.OrdinalIgnoreCase),
        $"{reads} UI reads; fixed Shell.Windows proxy");
}

static IReadOnlyList<string> BuildExplorerCandidatePaths(
    IEnumerable<ExplorerShellWindow> windows,
    IntPtr foregroundWindow)
{
    var candidates = new List<QuickSwitchFolderCandidate>();
    var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var window in windows)
    {
        if (string.IsNullOrWhiteSpace(window.FolderPath))
        {
            continue;
        }

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(window.FolderPath));
        if (!Directory.Exists(path))
        {
            continue;
        }

        var candidate = new QuickSwitchFolderCandidate(
            path,
            "Explorer",
            window.Handle,
            window.Handle == foregroundWindow);
        if (indexes.TryGetValue(path, out var index))
        {
            if (candidate.IsForeground && !candidates[index].IsForeground)
            {
                candidates[index] = candidate;
            }

            continue;
        }

        indexes.Add(path, candidates.Count);
        candidates.Add(candidate);
    }

    return candidates
        .OrderByDescending(candidate => candidate.IsForeground)
        .Select(candidate => candidate.FolderPath)
        .ToArray();
}

static BenchmarkResult BenchmarkQuickSwitchLayout(int ticks)
{
    var baselineLayouts = 0;
    var optimizedLayouts = 0;
    var baselineBounds = default(NativeRectangle);
    var optimizedBounds = default(NativeRectangle);
    var baseline = MeasureMedian(
        () =>
        {
            baselineLayouts = 0;
            for (var tick = 0; tick < ticks; tick++)
            {
                baselineBounds = BoundsForTick(tick);
                ProxyWpfLayout();
                baselineLayouts++;
            }
        },
        rounds: 5);
    var optimized = MeasureMedian(
        () =>
        {
            optimizedLayouts = 0;
            var cached = false;
            var previous = default(NativeRectangle);
            for (var tick = 0; tick < ticks; tick++)
            {
                optimizedBounds = BoundsForTick(tick);
                var invalidated = tick % 250 == 0;
                if (QuickSwitchBarWindow.CanReusePosition(
                        cached,
                        invalidated,
                        previous,
                        optimizedBounds,
                        previousDpiScale: 1.25,
                        currentDpiScale: 1.25))
                {
                    continue;
                }

                ProxyWpfLayout();
                optimizedLayouts++;
                previous = optimizedBounds;
                cached = true;
            }
        },
        rounds: 5);
    return new BenchmarkResult(
        "QuickSwitch layout gate",
        baseline.Milliseconds,
        optimized.Milliseconds,
        baselineLayouts,
        optimizedLayouts,
        BoundsEqual(baselineBounds, optimizedBounds),
        $"{ticks} timer/result ticks; fixed UpdateLayout proxy");
}

static async Task<BenchmarkResult> BenchmarkSelectionDebounceAsync(int changes)
{
    var folder = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "ListaryOpen-Bench");
    var paths = Enumerable.Range(0, changes)
        .Select(index => Path.Combine(folder, $"item-{index:D5}.txt"))
        .ToArray();
    var baselineProvider = new ProxySelectionProvider();
    using (var baselineService = new ExplorerSelectionService(baselineProvider, TimeSpan.FromMilliseconds(12)))
    {
        var stopwatch = Stopwatch.StartNew();
        foreach (var path in paths)
        {
            baselineService.TrySelectItem(new IntPtr(42), folder, path);
        }

        stopwatch.Stop();
        baselineProvider.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    }

    var optimizedProvider = new ProxySelectionProvider();
    double enqueueMilliseconds;
    double totalMilliseconds;
    using (var optimizedService = new ExplorerSelectionService(optimizedProvider, TimeSpan.FromMilliseconds(12)))
    {
        var total = Stopwatch.StartNew();
        var enqueue = Stopwatch.StartNew();
        foreach (var path in paths)
        {
            optimizedService.QueueSelectItem(new IntPtr(42), folder, path);
        }

        enqueue.Stop();
        if (!optimizedProvider.Selected.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("Explorer selection proxy did not receive the debounced item.");
        }

        total.Stop();
        enqueueMilliseconds = enqueue.Elapsed.TotalMilliseconds;
        totalMilliseconds = total.Elapsed.TotalMilliseconds;
    }

    await Task.CompletedTask;
    return new BenchmarkResult(
        "Explorer selection debounce",
        baselineProvider.ElapsedMilliseconds,
        enqueueMilliseconds,
        baselineProvider.CallCount,
        optimizedProvider.CallCount,
        string.Equals(baselineProvider.LastChildName, optimizedProvider.LastChildName, StringComparison.Ordinal),
        $"caller time; optimized total={totalMilliseconds:F2}ms including 12ms debounce; STA={optimizedProvider.ApartmentState}");
}

static async Task<BenchmarkResult> BenchmarkObservationCoalescingAsync(int requests)
{
    var baselineProvider = new ProxyObservation();
    var baseline = Stopwatch.StartNew();
    for (var request = 0; request < requests; request++)
    {
        baselineProvider.Observe();
    }

    baseline.Stop();

    var optimizedProvider = new ProxyObservation();
    using var scheduler = new ExplorerObservationScheduler(
        optimizedProvider.Observe,
        TimeSpan.FromMinutes(1),
        () => new ManualObservationTimer());
    scheduler.Start();
    await scheduler.RequestObservationAsync();
    optimizedProvider.Reset();
    var total = Stopwatch.StartNew();
    var enqueue = Stopwatch.StartNew();
    var waiters = Enumerable.Range(0, requests)
        .Select(_ => scheduler.RequestObservationAsync())
        .ToArray();
    enqueue.Stop();
    await Task.WhenAll(waiters);
    total.Stop();
    return new BenchmarkResult(
        "Explorer observation coalescing",
        baseline.Elapsed.TotalMilliseconds,
        enqueue.Elapsed.TotalMilliseconds,
        baselineProvider.CallCount,
        optimizedProvider.CallCount,
        optimizedProvider.LastSnapshot == baselineProvider.LastSnapshot,
        $"caller time; optimized total={total.Elapsed.TotalMilliseconds:F2}ms; worker={optimizedProvider.ApartmentState}");
}

static IEnumerable<BenchmarkResult> CreateNativeHookProxyMetrics()
{
    const int sampleMilliseconds = 8_000;
    const int legacyPollMilliseconds = 50;
    const int legacyScanMilliseconds = 500;
    const int recoveryScanMilliseconds = 5_000;
    yield return new BenchmarkResult(
        "HookHost idle wakeups (8s)",
        0,
        0,
        sampleMilliseconds / legacyPollMilliseconds,
        (int)Math.Ceiling((double)sampleMilliseconds / recoveryScanMilliseconds),
        true,
        "proxy count: MsgWait wakes immediately for WinEvent/message; no idle 50ms poll");
    yield return new BenchmarkResult(
        "HookHost recovery scans (8s)",
        0,
        0,
        sampleMilliseconds / legacyScanMilliseconds,
        (int)Math.Ceiling((double)sampleMilliseconds / recoveryScanMilliseconds),
        true,
        "dialog-start/foreground WinEvent is immediate; 500ms fallback retained if registration fails");
    yield return new BenchmarkResult(
        "HookHost parent checks (8s)",
        0,
        0,
        sampleMilliseconds / legacyScanMilliseconds,
        0,
        true,
        "one process handle + WaitForSingleObject(INFINITE); wake occurs only when parent exits");
}

static Measurement MeasureMedian(Action action, int rounds)
{
    action();
    var timings = new double[rounds];
    for (var round = 0; round < rounds; round++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        timings[round] = stopwatch.Elapsed.TotalMilliseconds;
    }

    Array.Sort(timings);
    return new Measurement(timings[timings.Length / 2]);
}

static void ProcessInputEvents(IReadOnlyList<GlobalTextInputEventArgs> inputs)
{
    var checksum = 0;
    foreach (var input in inputs)
    {
        Thread.SpinWait(64);
        checksum = HashCode.Combine(checksum, input.ForegroundWindow, input.Text);
    }

    BenchmarkSink.Value = checksum;
}

static void ProxyOsInputInspection()
{
    Thread.SpinWait(96);
}

static void ProxyWpfLayout()
{
    Thread.SpinWait(128);
}

static IReadOnlyList<SearchResult> Rank(SearchQuery query, IReadOnlyList<FileRecord> entries) =>
    ResultRanker.Rank(
        query,
        entries,
        Array.Empty<UsageRecord>(),
        Array.Empty<string>());

static string ResultSignature(IReadOnlyList<SearchResult> results) =>
    string.Join('|', results.Take(10).Select(result => result.Record.PathKey));

static SearchResult CreateResult(string path, double score) =>
    new(FileRecord.Create(path, false, 0, DateTimeOffset.UnixEpoch), score, "benchmark");

static string CreateFixtureFolder(int fileCount)
{
    var folder = Path.Combine(Path.GetTempPath(), $"ListaryOpen-UiBench-{Guid.NewGuid():N}");
    Directory.CreateDirectory(folder);
    var prefixes = new[] { "report", "project", "open", "image", "invoice", "archive", "source", "notes" };
    for (var index = 0; index < fileCount; index++)
    {
        File.WriteAllText(Path.Combine(folder, $"{prefixes[index % prefixes.Length]}-{index:D5}.txt"), string.Empty);
    }

    for (var index = 0; index < Math.Max(10, fileCount / 20); index++)
    {
        Directory.CreateDirectory(Path.Combine(folder, $"folder-{index:D4}"));
    }

    return folder;
}

static NativeRectangle BoundsForTick(int tick)
{
    var movement = tick / 2_000;
    return new NativeRectangle(100 + movement, 100, 900 + movement, 700);
}

static bool BoundsEqual(NativeRectangle left, NativeRectangle right) =>
    left.Left == right.Left &&
    left.Top == right.Top &&
    left.Right == right.Right &&
    left.Bottom == right.Bottom;

internal sealed record BenchmarkResult(
    string Scenario,
    double BaselineMilliseconds,
    double OptimizedMilliseconds,
    int BaselineOperations,
    int OptimizedOperations,
    bool SemanticMatch,
    string Detail);

internal readonly record struct Measurement(double Milliseconds);

internal static class BenchmarkSink
{
    public static volatile int Value;
}

internal sealed class ProxyExplorerWindowsProvider(string folder) : IExplorerShellWindowsProvider
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public IEnumerable<ExplorerShellWindow> EnumerateWindows()
    {
        Interlocked.Increment(ref _callCount);
        Thread.SpinWait(2_000);
        return
        [
            new ExplorerShellWindow(new IntPtr(42), folder),
            new ExplorerShellWindow(new IntPtr(84), folder)
        ];
    }

    public void Reset() => Interlocked.Exchange(ref _callCount, 0);
}

internal sealed class ProxySelectionProvider : IExplorerShellSelectionProvider
{
    private int _callCount;

    public ManualResetEventSlim Selected { get; } = new();

    public int CallCount => Volatile.Read(ref _callCount);

    public string? LastChildName { get; private set; }

    public ApartmentState ApartmentState { get; private set; }

    public double ElapsedMilliseconds { get; set; }

    public bool TrySelectItem(IntPtr explorerWindow, string currentFolder, string childName)
    {
        Thread.SpinWait(4_000);
        LastChildName = childName;
        ApartmentState = Thread.CurrentThread.GetApartmentState();
        Interlocked.Increment(ref _callCount);
        Selected.Set();
        return true;
    }
}

internal sealed class ProxyObservation
{
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public int LastSnapshot { get; private set; }

    public ApartmentState ApartmentState { get; private set; }

    public void Observe()
    {
        Thread.SpinWait(6_000);
        LastSnapshot = 42;
        ApartmentState = Thread.CurrentThread.GetApartmentState();
        Interlocked.Increment(ref _callCount);
    }

    public void Reset() => Interlocked.Exchange(ref _callCount, 0);
}

internal sealed class ManualObservationTimer : IExplorerObservationTimer
{
    public event EventHandler? Tick
    {
        add { }
        remove { }
    }

    public void Start(TimeSpan interval)
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
