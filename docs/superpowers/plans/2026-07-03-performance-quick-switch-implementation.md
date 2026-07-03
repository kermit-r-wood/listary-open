# Performance Quick Switch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tune SQLite search performance and expand Quick Switch folder mode from one Explorer folder to multiple window-derived folder candidates.

**Architecture:** Quick Switch gets a small provider abstraction that returns folder candidates from file-management windows. The first production implementation uses the existing Explorer Shell COM seam and upgrades folder-mode search to pin multiple candidates. SQLite search keeps managed ranking but bounds SQL candidate reads and scopes usage reads to candidate path keys.

**Tech Stack:** C#/.NET 8, WPF, Microsoft.Data.Sqlite, xUnit, Windows Shell COM seams.

---

## File Map

- Create: `src/ListaryOpen.Infrastructure/Windows/QuickSwitchFolderCandidate.cs`
  - Candidate record and provider interface for folder-producing windows.
- Modify: `src/ListaryOpen.Infrastructure/Windows/ExplorerTracker.cs`
  - Implement candidate enumeration from Shell Explorer windows and preserve `LastFolder` compatibility.
- Modify: `src/ListaryOpen.App/App.xaml.cs`
  - Route dialog hotkey to multiple candidates.
- Modify: `src/ListaryOpen.App/SearchPanel.xaml.cs`
  - Add `ActivateQuickSwitchFolderSearch(IReadOnlyList<QuickSwitchFolderCandidate>)`.
- Modify: `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`
  - Pin multiple Quick Switch folder results above indexed folder results.
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
  - Add schema indexes, candidate caps, chunked usage lookup.
- Test: `tests/ListaryOpen.Infrastructure.Tests/Windows/ExplorerTrackerTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/AppDialogHotkeyTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`
- Modify: `docs/manual-test-checklist.md`
  - Record performance and Quick Switch manual verification status.

---

### Task 1: Quick Switch Candidate Provider

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Windows/QuickSwitchFolderCandidate.cs`
- Modify: `src/ListaryOpen.Infrastructure/Windows/ExplorerTracker.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Windows/ExplorerTrackerTests.cs`

- [ ] **Step 1: Write failing tests for multiple Explorer candidates**

Add tests to `ExplorerTrackerTests`:

```csharp
[Fact]
public void GetFolderCandidatesReturnsForegroundExplorerFolderFirstAndDeduplicates()
{
    var first = Directory.CreateTempSubdirectory("listary-open-first-");
    var second = Directory.CreateTempSubdirectory("listary-open-second-");

    try
    {
        var foregroundHandle = new IntPtr(2222);
        var tracker = new ExplorerTracker(
            () => foregroundHandle,
            new RecordingExplorerShellWindowsProvider(new[]
            {
                new ExplorerShellWindow(new IntPtr(1111), first.FullName),
                new ExplorerShellWindow(foregroundHandle, second.FullName),
                new ExplorerShellWindow(new IntPtr(3333), first.FullName + Path.DirectorySeparatorChar)
            }));

        var candidates = tracker.GetFolderCandidates();

        Assert.Equal(new[] { second.FullName, first.FullName }, candidates.Select(candidate => candidate.FolderPath));
        Assert.True(candidates[0].IsForeground);
        Assert.Equal("Explorer", candidates[0].SourceName);
        Assert.Equal(foregroundHandle, candidates[0].WindowHandle);
    }
    finally
    {
        first.Delete(recursive: true);
        second.Delete(recursive: true);
    }
}

[Fact]
public void GetFolderCandidatesSkipsMissingFolders()
{
    var existing = Directory.CreateTempSubdirectory("listary-open-existing-");
    var missing = Path.Combine(Path.GetTempPath(), "listary-open-missing-" + Guid.NewGuid());

    try
    {
        var tracker = new ExplorerTracker(
            () => new IntPtr(1234),
            new RecordingExplorerShellWindowsProvider(new[]
            {
                new ExplorerShellWindow(new IntPtr(1234), missing),
                new ExplorerShellWindow(new IntPtr(5678), existing.FullName)
            }));

        var candidate = Assert.Single(tracker.GetFolderCandidates());

        Assert.Equal(existing.FullName, candidate.FolderPath);
        Assert.False(candidate.IsForeground);
    }
    finally
    {
        existing.Delete(recursive: true);
    }
}
```

- [ ] **Step 2: Run tests and verify red**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~ExplorerTrackerTests --no-restore
```

Expected: compile failure because `QuickSwitchFolderCandidate` and `GetFolderCandidates` do not exist.

- [ ] **Step 3: Add candidate model and provider interface**

Create `QuickSwitchFolderCandidate.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Windows;

public sealed record QuickSwitchFolderCandidate(
    string FolderPath,
    string SourceName,
    IntPtr WindowHandle,
    bool IsForeground);

public interface IQuickSwitchWindowProvider
{
    IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates();
}
```

- [ ] **Step 4: Implement Explorer candidate enumeration**

Modify `ExplorerTracker`:

```csharp
public sealed class ExplorerTracker : IQuickSwitchWindowProvider
{
    public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
    {
        try
        {
            var foregroundHandle = _foregroundWindowProvider();
            var candidates = new List<QuickSwitchFolderCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var window in _shellWindowsProvider.EnumerateWindows())
            {
                if (string.IsNullOrWhiteSpace(window.FolderPath) || !Directory.Exists(window.FolderPath))
                {
                    continue;
                }

                var folderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(window.FolderPath));
                var pathKey = folderPath.ToUpperInvariant();
                if (!seen.Add(pathKey))
                {
                    continue;
                }

                candidates.Add(new QuickSwitchFolderCandidate(
                    folderPath,
                    "Explorer",
                    window.Handle,
                    window.Handle == foregroundHandle));
            }

            return candidates
                .OrderByDescending(candidate => candidate.IsForeground)
                .ToArray();
        }
        catch (Exception exception) when (IsExpectedExplorerObservationException(exception))
        {
            Trace.TraceWarning("Explorer candidate observation failed: {0}", exception.Message);
            return Array.Empty<QuickSwitchFolderCandidate>();
        }
    }
}
```

Keep `ObserveForegroundExplorerFolder` and `LastFolder` for existing callers.

- [ ] **Step 5: Run tests and commit**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~ExplorerTrackerTests --no-restore
```

Expected: all Explorer tracker tests pass.

Commit:

```powershell
git add src\ListaryOpen.Infrastructure\Windows\QuickSwitchFolderCandidate.cs src\ListaryOpen.Infrastructure\Windows\ExplorerTracker.cs tests\ListaryOpen.Infrastructure.Tests\Windows\ExplorerTrackerTests.cs
git commit -m "feat: collect quick switch folder candidates"
```

---

### Task 2: Folder Search Pins Multiple Quick Switch Candidates

**Files:**
- Modify: `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`
- Modify: `src/ListaryOpen.App/SearchPanel.xaml.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs`

- [ ] **Step 1: Write failing ViewModel test**

Add test:

```csharp
[Fact]
public async Task ActivateQuickSwitchFolderSearchAsyncPinsMultipleQuickSwitchCandidatesAboveIndexResults()
{
    var first = Directory.CreateTempSubdirectory("listary-open-first-");
    var second = Directory.CreateTempSubdirectory("listary-open-second-");
    var indexedFolder = CreateResult("C:\\Docs\\Invoices", isDirectory: true);
    var index = new RecordingSearchIndex(new[] { indexedFolder });
    var viewModel = new SearchPanelViewModel(index)
    {
        QueryText = "invoice"
    };

    try
    {
        await index.WaitForSearchCountAsync(1);
        index.ClearObservedQueries();

        await viewModel.ActivateQuickSwitchFolderSearchAsync(new[]
        {
            new QuickSwitchFolderCandidate(first.FullName, "Explorer", new IntPtr(1), isForeground: true),
            new QuickSwitchFolderCandidate(second.FullName, "Explorer", new IntPtr(2), isForeground: false)
        });

        Assert.Equal(SearchMode.FoldersOnly, Assert.Single(index.ObservedQueries).Mode);
        Assert.Equal(
            new[] { first.FullName, second.FullName, indexedFolder.Record.FullPath },
            viewModel.Results.Select(result => result.Record.FullPath));
        Assert.Equal(first.FullName, viewModel.SelectedResult?.Record.FullPath);
    }
    finally
    {
        first.Delete(recursive: true);
        second.Delete(recursive: true);
    }
}
```

Add `using ListaryOpen.Infrastructure.Windows;` to the test file.

- [ ] **Step 2: Run test and verify red**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~ActivateQuickSwitchFolderSearchAsyncPinsMultipleQuickSwitchCandidatesAboveIndexResults --no-restore
```

Expected: compile failure because the quick-switch method does not exist.

- [ ] **Step 3: Implement multi-candidate folder mode**

In `SearchPanelViewModel`:

- Add field:

```csharp
private IReadOnlyList<QuickSwitchFolderCandidate> _quickSwitchCandidates = Array.Empty<QuickSwitchFolderCandidate>();
```

- Keep existing single-folder method by converting to one candidate internally:

```csharp
public Task ActivateFolderSearchAsync(string? trackedFolder)
{
    var normalized = TryNormalizeExistingFolder(trackedFolder);
    return ActivateQuickSwitchFolderSearchAsync(normalized is null
        ? Array.Empty<QuickSwitchFolderCandidate>()
        : new[] { new QuickSwitchFolderCandidate(normalized, "Explorer", IntPtr.Zero, true) });
}
```

- Add separate quick-switch method:

```csharp
public Task ActivateQuickSwitchFolderSearchAsync(IReadOnlyList<QuickSwitchFolderCandidate> candidates)
{
    ArgumentNullException.ThrowIfNull(candidates);

    _searchMode = SearchMode.FoldersOnly;
    _quickSwitchCandidates = NormalizeQuickSwitchCandidates(candidates);
    StatusText = "Select a folder to jump the dialog.";
    return RefreshAsync();
}
```

- Replace tracked folder result creation with `TryCreateQuickSwitchResults`.
- Deduplicate pinned candidates by `PathKey`.
- Preserve first candidate selection through existing `UpdateSelectedResultAfterRefresh`.

- [ ] **Step 4: Add SearchPanel window quick-switch method**

In `SearchPanel.xaml.cs` add:

```csharp
public void ActivateQuickSwitchFolderSearch(IReadOnlyList<QuickSwitchFolderCandidate> candidates)
{
    _ = ViewModel.ActivateQuickSwitchFolderSearchAsync(candidates);
    ShowAndFocusQuery();
}
```

Add `using ListaryOpen.Infrastructure.Windows;`.

- [ ] **Step 5: Run tests and commit**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~SearchPanelViewModelTests --no-restore
```

Expected: all SearchPanelViewModel tests pass.

Commit:

```powershell
git add src\ListaryOpen.App\ViewModels\SearchPanelViewModel.cs src\ListaryOpen.App\SearchPanel.xaml.cs tests\ListaryOpen.Infrastructure.Tests\App\SearchPanelViewModelTests.cs
git commit -m "feat: pin quick switch folders in search"
```

---

### Task 3: App Hotkey Routes Multiple Candidates

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/AppDialogHotkeyTests.cs`

- [ ] **Step 1: Write failing routing test**

Add test:

```csharp
[Fact]
public void ObserveQuickSwitchFolderCandidatesRefreshesExplorerTrackerAndReturnsCandidates()
{
    var first = Directory.CreateTempSubdirectory("listary-open-first-");
    var second = Directory.CreateTempSubdirectory("listary-open-second-");

    try
    {
        var foregroundHandle = new IntPtr(2222);
        var tracker = new ExplorerTracker(
            () => foregroundHandle,
            new RecordingExplorerShellWindowsProvider(new[]
            {
                new ExplorerShellWindow(new IntPtr(1111), first.FullName),
                new ExplorerShellWindow(foregroundHandle, second.FullName)
            }));

        var candidates = ListaryOpen.App.App.ObserveQuickSwitchFolderCandidates(tracker);

        Assert.Equal(new[] { second.FullName, first.FullName }, candidates.Select(candidate => candidate.FolderPath));
    }
    finally
    {
        first.Delete(recursive: true);
        second.Delete(recursive: true);
    }
}
```

- [ ] **Step 2: Run test and verify red**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~AppDialogHotkeyTests --no-restore
```

Expected: compile failure because `ObserveQuickSwitchFolderCandidates` does not exist.

- [ ] **Step 3: Implement app routing**

In `App.xaml.cs`:

```csharp
private void HandleDialogHotkey()
{
    var candidates = ObserveQuickSwitchFolderCandidates(_explorerTracker);
    _searchPanel?.ActivateQuickSwitchFolderSearch(candidates);
}

internal static IReadOnlyList<QuickSwitchFolderCandidate> ObserveQuickSwitchFolderCandidates(ExplorerTracker? explorerTracker)
{
    if (explorerTracker is null)
    {
        return Array.Empty<QuickSwitchFolderCandidate>();
    }

    explorerTracker.ObserveForegroundExplorerFolder();
    return explorerTracker.GetFolderCandidates();
}
```

Keep `ObserveAndGetExistingTrackedFolder` for compatibility tests by returning the first candidate path or `LastFolder` fallback.

- [ ] **Step 4: Run tests and commit**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~AppDialogHotkeyTests --no-restore
```

Expected: app dialog hotkey tests pass.

Commit:

```powershell
git add src\ListaryOpen.App\App.xaml.cs tests\ListaryOpen.Infrastructure.Tests\App\AppDialogHotkeyTests.cs
git commit -m "feat: route dialog hotkey quick switch candidates"
```

---

### Task 4: SQLite Candidate and Usage Performance Tuning

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] **Step 1: Write failing usage-scope test**

Add test using reflection only if necessary is not allowed. Instead, use observable behavior: many unrelated usage rows should not make search slow or change results. Add helper to insert usage rows through raw SQLite after index disposal or direct connection.

```csharp
[Fact]
public async Task SearchReadsUsageOnlyForCandidatesAndKeepsUsedCandidateBoost()
{
    var dbPath = CreateTempDbPath();

    try
    {
        await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
        {
            await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);
            await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice Archive.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);
        }

        await InsertUsageRowsAsync(dbPath, unrelatedRows: 5000, usedPath: "C:\\Docs\\Invoice Archive.xlsx");

        await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
        {
            var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

            Assert.Equal("Invoice Archive.xlsx", results[0].Record.Name);
        }
    }
    finally
    {
        DeleteIfExists(dbPath);
    }
}
```

- [ ] **Step 2: Write performance regression test**

Add a conservative 50k-row test:

```csharp
[Fact]
public async Task SearchOnLargeIndexCompletesWithinRegressionBudget()
{
    var dbPath = CreateTempDbPath();
    var lastWriteTime = DateTimeOffset.UtcNow;

    try
    {
        await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
        for (var batch = 0; batch < 50; batch++)
        {
            var records = Enumerable
                .Range(batch * 1000, 1000)
                .Select(i => FileRecord.Create($"C:\\Docs\\Noise-{i:D5}.txt", false, 1, lastWriteTime));
            await index.UpsertManyAsync(records, CancellationToken.None);
        }

        await index.UpsertAsync(FileRecord.Create("C:\\Docs\\QuarterlyInvoice2026.xlsx", false, 10, lastWriteTime), CancellationToken.None);

        var started = Stopwatch.GetTimestamp();
        var results = await index.SearchAsync(new SearchQuery("qi26", SearchMode.FilesAndFolders), CancellationToken.None);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal("QuarterlyInvoice2026.xlsx", results[0].Record.Name);
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"Search took {elapsed}.");
    }
    finally
    {
        DeleteIfExists(dbPath);
    }
}
```

Add `using System.Diagnostics;`.

- [ ] **Step 3: Run tests and verify current behavior**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~SqliteSearchIndexTests --no-restore
```

Expected: at least the usage/performance intent is not guaranteed before implementation. If the performance threshold passes on the current machine, keep the test as a guard and continue to implementation because the code still has unbounded candidate/usage reads by inspection.

- [ ] **Step 4: Add schema indexes**

In `CreateSchemaAsync`, after table creation:

```sql
create index if not exists ix_files_name on files(name);
create index if not exists ix_files_is_directory_name on files(is_directory, name);
create index if not exists ix_files_search_text on files(search_text);
create index if not exists ix_usage_path_key on usage(path_key);
```

Also run those statements from `MigrateSchemaAsync` for existing databases.

- [ ] **Step 5: Add candidate limits**

Add constants:

```csharp
private const int ExactCandidateMultiplier = 20;
private const int FuzzyCandidateMultiplier = 40;
private const int MinimumCandidateLimit = 200;
private const int MaximumExactCandidateLimit = 2_000;
private const int MaximumFuzzyCandidateLimit = 4_000;
```

Add:

```csharp
private static int CreateCandidateLimit(SearchQuery query, int multiplier, int maximum)
{
    return Math.Clamp(query.Limit * multiplier, MinimumCandidateLimit, maximum);
}
```

Apply `limit $limit` to exact and fuzzy candidate SQL.

For fuzzy SQL, order by:

```sql
order by
    length(name),
    name,
    full_path
limit $limit;
```

- [ ] **Step 6: Scope usage reads to candidates**

Change `SearchAsync`:

```csharp
var candidates = await ReadCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
var usage = await ReadUsageAsync(candidates.Select(candidate => candidate.PathKey), cancellationToken).ConfigureAwait(false);
return ResultRanker.Rank(query, candidates, usage, Array.Empty<string>());
```

Implement chunked usage reads:

```csharp
private async Task<IReadOnlyList<UsageRecord>> ReadUsageAsync(
    IEnumerable<string> candidatePathKeys,
    CancellationToken cancellationToken)
{
    var keys = candidatePathKeys.Distinct(StringComparer.Ordinal).ToArray();
    if (keys.Length == 0)
    {
        return Array.Empty<UsageRecord>();
    }

    var records = new List<UsageRecord>();
    foreach (var chunk in keys.Chunk(250))
    {
        using var command = _connection.CreateCommand();
        var parameterNames = new List<string>(chunk.Length);
        for (var i = 0; i < chunk.Length; i++)
        {
            var parameterName = "$path_key_" + i.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, chunk[i]);
        }

        command.CommandText = $"""
            select
                full_path,
                open_count,
                last_used_at
            from usage
            where path_key in ({string.Join(", ", parameterNames)});
            """;

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(new UsageRecord(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    ParseDateTime(reader.GetString(2))));
            }
        }
    }

    return records;
}
```

- [ ] **Step 7: Run tests and commit**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~SqliteSearchIndexTests --no-restore
```

Expected: all SQLite tests pass.

Commit:

```powershell
git add src\ListaryOpen.Infrastructure\Search\SqliteSearchIndex.cs tests\ListaryOpen.Infrastructure.Tests\Search\SqliteSearchIndexTests.cs
git commit -m "perf: bound sqlite search candidates"
```

---

### Task 5: Verification Checklist and Full Validation

**Files:**
- Modify: `docs/manual-test-checklist.md`

- [ ] **Step 1: Update manual checklist**

Update the checklist with:

- Full test count after changes.
- Build status.
- Performance regression test status.
- App startup smoke status.
- Quick Switch multi-Explorer manual verification status. If not run manually, mark it `NOT VERIFIED`, not `PASS`.

- [ ] **Step 2: Run complete tests**

Run:

```powershell
dotnet test ListaryOpen.sln --no-restore
```

Expected: all tests pass.

- [ ] **Step 3: Run build**

Run:

```powershell
dotnet build ListaryOpen.sln --no-restore
```

Expected: build succeeds with 0 warnings and 0 errors.

- [ ] **Step 4: App smoke**

Run the built app for a short smoke:

```powershell
$exe = Resolve-Path 'src\ListaryOpen.App\bin\Debug\net8.0-windows\ListaryOpen.App.exe'
$p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 12
"PID=$($p.Id);Alive=$(-not $p.HasExited)"
if (-not $p.HasExited) {
    try { [void]$p.CloseMainWindow() } catch {}
    Start-Sleep -Seconds 2
    $p.Refresh()
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
}
```

Expected: app remains alive during the smoke run. Do not kill any pre-existing user-owned process.

- [ ] **Step 5: Commit verification**

Commit:

```powershell
git add docs\manual-test-checklist.md
git commit -m "test: verify performance quick switch tuning"
```

- [ ] **Step 6: Request final code review**

Dispatch a reviewer over the range from the design commit to HEAD. Ask it to verify:

- Quick Switch candidates support multiple Explorer windows.
- Candidate order and deduplication are correct.
- SQLite candidate limiting does not drop important exact/pinyin/fuzzy results.
- Usage lookup is candidate-scoped.
- Tests and manual checklist do not overclaim.

- [ ] **Step 7: Final verification**

After review fixes, rerun:

```powershell
dotnet test ListaryOpen.sln --no-restore
dotnet build ListaryOpen.sln --no-restore
git status --short
```

Expected: tests pass, build has 0 warnings/errors, working tree is clean.

---

## Plan Self-Review

- Spec coverage: Tasks 1-3 cover Quick Switch multi-window candidates. Task 4 covers SQLite candidate/usage performance. Task 5 covers complete validation and manual checklist.
- Completion-marker scan: no unfinished markers are intentionally left.
- Type consistency: `QuickSwitchFolderCandidate` and `IQuickSwitchWindowProvider` are defined before all callers. `SearchPanelViewModel` keeps the single-folder method for existing tests and adds a separately named candidate-list method for new routing so `null` calls remain source-compatible.
