# ListaryOpen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows-only WPF desktop utility with Everything-like file indexing, instant file/folder search, and Quick Save / Open integration for standard Windows file dialogs.

**Architecture:** The solution is split into a WPF app, core domain library, infrastructure library, elevated indexer helper, and tests. Core search, ranking, settings, and indexing provider selection are UI-independent and testable; WPF owns tray, hotkeys, windows, and user interaction; infrastructure owns SQLite, Win32, UI Automation, file-system providers, and helper-process integration.

**Tech Stack:** .NET 8, WPF, xUnit, SQLite via `Microsoft.Data.Sqlite`, Windows UI Automation, Win32 P/Invoke, NTFS MFT/USN provider boundary with fallback recursive scanning.

---

## File Structure

- `ListaryOpen.sln`: solution file.
- `global.json`: pins the .NET SDK major version.
- `src/ListaryOpen.Core/ListaryOpen.Core.csproj`: domain models, search ranking, settings contracts, provider interfaces.
- `src/ListaryOpen.Core/Indexing/FileRecord.cs`: normalized file/folder metadata.
- `src/ListaryOpen.Core/Indexing/IndexProviderContracts.cs`: provider contracts and status models.
- `src/ListaryOpen.Core/Search/SearchQuery.cs`: query input model.
- `src/ListaryOpen.Core/Search/SearchResult.cs`: ranked result model.
- `src/ListaryOpen.Core/Search/ISearchIndex.cs`: index query/write contract.
- `src/ListaryOpen.Core/Search/FuzzyMatcher.cs`: ASCII and abbreviation fuzzy matching.
- `src/ListaryOpen.Core/Search/PinyinMatcher.cs`: Chinese pinyin and pinyin-initial matching.
- `src/ListaryOpen.Core/Search/ResultRanker.cs`: scoring and ordering.
- `src/ListaryOpen.Core/Usage/UsageRecord.cs`: usage history record.
- `src/ListaryOpen.Core/Settings/AppSettings.cs`: persisted settings model.
- `src/ListaryOpen.Infrastructure/ListaryOpen.Infrastructure.csproj`: SQLite storage, fallback indexing, NTFS provider boundary, UI Automation adapters, Win32 adapters.
- `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`: SQLite-backed index implementation.
- `src/ListaryOpen.Infrastructure/Indexing/FallbackIndexProvider.cs`: recursive scan and watcher provider.
- `src/ListaryOpen.Infrastructure/Indexing/VolumeIndexer.cs`: selects NTFS or fallback providers per volume.
- `src/ListaryOpen.Infrastructure/Indexing/NtfsIndexProvider.cs`: NTFS provider facade that delegates privileged metadata reads to the helper when available.
- `src/ListaryOpen.Infrastructure/Indexing/ElevatedIndexerClient.cs`: normal-process client for the helper.
- `src/ListaryOpen.Infrastructure/Indexing/Ntfs/UsnRecordParser.cs`: parses USN records emitted by the helper.
- `src/ListaryOpen.Infrastructure/Dialog/IDialogAutomation.cs`: abstraction for dialog control.
- `src/ListaryOpen.Infrastructure/Dialog/DialogBridge.cs`: Quick Save / Open coordinator.
- `src/ListaryOpen.Infrastructure/Dialog/WindowsDialogAutomation.cs`: Win32/UI Automation implementation.
- `src/ListaryOpen.Infrastructure/Windows/HotkeyService.cs`: global hotkey registration.
- `src/ListaryOpen.Infrastructure/Windows/ExplorerTracker.cs`: active Explorer folder tracking.
- `src/ListaryOpen.App/ListaryOpen.App.csproj`: WPF app.
- `src/ListaryOpen.App/App.xaml`: application resources.
- `src/ListaryOpen.App/App.xaml.cs`: startup, dependency wiring, single-instance boot.
- `src/ListaryOpen.App/MainWindow.xaml`: settings window shell.
- `src/ListaryOpen.App/MainWindow.xaml.cs`: settings window code-behind.
- `src/ListaryOpen.App/SearchPanel.xaml`: search overlay UI.
- `src/ListaryOpen.App/SearchPanel.xaml.cs`: search overlay interaction.
- `src/ListaryOpen.App/Tray/TrayController.cs`: tray icon and menu.
- `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`: search panel state and commands.
- `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`: settings state.
- `src/ListaryOpen.Indexer.Elevated/ListaryOpen.Indexer.Elevated.csproj`: helper executable for privileged NTFS metadata reads.
- `src/ListaryOpen.Indexer.Elevated/Program.cs`: helper entry point.
- `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsNativeMethods.cs`: native NTFS P/Invoke definitions.
- `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnJournalReader.cs`: reads MFT/USN metadata for NTFS volumes.
- `tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj`: xUnit tests for core.
- `tests/ListaryOpen.Core.Tests/Search/FuzzyMatcherTests.cs`: fuzzy matching tests.
- `tests/ListaryOpen.Core.Tests/Search/PinyinMatcherTests.cs`: pinyin tests.
- `tests/ListaryOpen.Core.Tests/Search/ResultRankerTests.cs`: ranking tests.
- `tests/ListaryOpen.Infrastructure.Tests/Indexing/ProviderSelectionTests.cs`: provider choice tests.
- `tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj`: xUnit tests for infrastructure.
- `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`: SQLite persistence tests.
- `tests/ListaryOpen.Infrastructure.Tests/Indexing/FallbackIndexProviderTests.cs`: fallback scan tests.
- `tests/ListaryOpen.Infrastructure.Tests/Indexing/UsnRecordParserTests.cs`: USN record parsing tests.
- `tests/ListaryOpen.Infrastructure.Tests/Dialog/DialogBridgeTests.cs`: mocked dialog bridge tests.
- `docs/manual-test-checklist.md`: Windows manual acceptance checklist.

## Milestone 0: Toolchain and Skeleton

### Task 1: Install or Verify .NET 8 SDK

**Files:**
- Create: `global.json`

- [ ] **Step 1: Check SDK availability**

Run:

```powershell
dotnet --list-sdks
```

Expected before installation in the current environment: no SDK versions are listed or `dotnet --version` reports "No .NET SDKs were found."

- [ ] **Step 2: Install .NET 8 SDK if missing**

Run:

```powershell
winget install Microsoft.DotNet.SDK.8 --accept-package-agreements --accept-source-agreements
```

Expected: winget installs the .NET 8 SDK. If winget is unavailable, install the .NET 8 SDK from Microsoft and re-open the terminal.

- [ ] **Step 3: Write SDK pin**

Create `global.json`:

```json
{
  "sdk": {
    "version": "8.0.0",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 4: Verify SDK**

Run:

```powershell
dotnet --version
```

Expected: prints an 8.0 SDK version or a compatible 8.0 feature-band version.

- [ ] **Step 5: Commit**

Run:

```powershell
git add global.json
git commit -m "chore: pin dotnet sdk"
```

### Task 2: Create Solution and Projects

**Files:**
- Create: `ListaryOpen.sln`
- Create: `src/ListaryOpen.Core/ListaryOpen.Core.csproj`
- Create: `src/ListaryOpen.Infrastructure/ListaryOpen.Infrastructure.csproj`
- Create: `src/ListaryOpen.App/ListaryOpen.App.csproj`
- Create: `src/ListaryOpen.Indexer.Elevated/ListaryOpen.Indexer.Elevated.csproj`
- Create: `tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj`
- Create: `tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj`

- [ ] **Step 1: Scaffold projects**

Run:

```powershell
dotnet new sln -n ListaryOpen
dotnet new classlib -n ListaryOpen.Core -o src/ListaryOpen.Core -f net8.0
dotnet new classlib -n ListaryOpen.Infrastructure -o src/ListaryOpen.Infrastructure -f net8.0
dotnet new wpf -n ListaryOpen.App -o src/ListaryOpen.App -f net8.0
dotnet new console -n ListaryOpen.Indexer.Elevated -o src/ListaryOpen.Indexer.Elevated -f net8.0
dotnet new xunit -n ListaryOpen.Core.Tests -o tests/ListaryOpen.Core.Tests -f net8.0
dotnet new xunit -n ListaryOpen.Infrastructure.Tests -o tests/ListaryOpen.Infrastructure.Tests -f net8.0
dotnet sln add src/ListaryOpen.Core/ListaryOpen.Core.csproj
dotnet sln add src/ListaryOpen.Infrastructure/ListaryOpen.Infrastructure.csproj
dotnet sln add src/ListaryOpen.App/ListaryOpen.App.csproj
dotnet sln add src/ListaryOpen.Indexer.Elevated/ListaryOpen.Indexer.Elevated.csproj
dotnet sln add tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj
dotnet sln add tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj
```

- [ ] **Step 2: Add project references**

Run:

```powershell
dotnet add src/ListaryOpen.Infrastructure/ListaryOpen.Infrastructure.csproj reference src/ListaryOpen.Core/ListaryOpen.Core.csproj
dotnet add src/ListaryOpen.App/ListaryOpen.App.csproj reference src/ListaryOpen.Core/ListaryOpen.Core.csproj
dotnet add src/ListaryOpen.App/ListaryOpen.App.csproj reference src/ListaryOpen.Infrastructure/ListaryOpen.Infrastructure.csproj
dotnet add src/ListaryOpen.Indexer.Elevated/ListaryOpen.Indexer.Elevated.csproj reference src/ListaryOpen.Core/ListaryOpen.Core.csproj
dotnet add src/ListaryOpen.Indexer.Elevated/ListaryOpen.Indexer.Elevated.csproj reference src/ListaryOpen.Infrastructure/ListaryOpen.Infrastructure.csproj
dotnet add tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj reference src/ListaryOpen.Core/ListaryOpen.Core.csproj
dotnet add tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj reference src/ListaryOpen.Core/ListaryOpen.Core.csproj
dotnet add tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj reference src/ListaryOpen.Infrastructure/ListaryOpen.Infrastructure.csproj
```

- [ ] **Step 3: Add packages**

Run:

```powershell
dotnet add src/ListaryOpen.Infrastructure/ListaryOpen.Infrastructure.csproj package Microsoft.Data.Sqlite
dotnet add src/ListaryOpen.App/ListaryOpen.App.csproj package Hardcodet.NotifyIcon.Wpf
dotnet add tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj package Microsoft.Data.Sqlite
```

- [ ] **Step 4: Enable Windows target frameworks**

Update the first `PropertyGroup` in `src/ListaryOpen.Infrastructure/ListaryOpen.Infrastructure.csproj` so it contains these properties while keeping the `PackageReference` entries created by `dotnet add package`:

```xml
<PropertyGroup>
  <TargetFramework>net8.0-windows</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <UseWPF>true</UseWPF>
</PropertyGroup>
```

Update the first `PropertyGroup` in `src/ListaryOpen.Indexer.Elevated/ListaryOpen.Indexer.Elevated.csproj`:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net8.0-windows</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

Update the first `PropertyGroup` in `tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj`:

```xml
<PropertyGroup>
  <TargetFramework>net8.0-windows</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <IsPackable>false</IsPackable>
  <IsTestProject>true</IsTestProject>
</PropertyGroup>
```

- [ ] **Step 5: Build**

Run:

```powershell
dotnet build ListaryOpen.sln
```

Expected: build succeeds.

- [ ] **Step 6: Commit**

Run:

```powershell
git add ListaryOpen.sln src tests
git commit -m "chore: scaffold ListaryOpen solution"
```

## Milestone 1: Search Core

### Task 3: File Records and Index Contracts

**Files:**
- Create: `src/ListaryOpen.Core/Indexing/FileRecord.cs`
- Create: `src/ListaryOpen.Core/Search/SearchQuery.cs`
- Create: `src/ListaryOpen.Core/Search/SearchResult.cs`
- Create: `src/ListaryOpen.Core/Search/ISearchIndex.cs`
- Create: `tests/ListaryOpen.Core.Tests/Indexing/FileRecordTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/ListaryOpen.Core.Tests/Indexing/FileRecordTests.cs`:

```csharp
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Core.Tests.Indexing;

public sealed class FileRecordTests
{
    [Fact]
    public void CreateNormalizesPathAndName()
    {
        var record = FileRecord.Create("C:\\Users\\Paul\\Documents\\Report.docx", false, 123, new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero));

        Assert.Equal("C:\\Users\\Paul\\Documents\\Report.docx", record.FullPath);
        Assert.Equal("Report.docx", record.Name);
        Assert.Equal("C:\\Users\\Paul\\Documents", record.ParentPath);
        Assert.False(record.IsDirectory);
        Assert.Equal(123, record.SizeBytes);
    }

    [Fact]
    public void CreateRejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => FileRecord.Create(" ", false, 0, DateTimeOffset.UnixEpoch));
    }
}
```

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj --filter FileRecordTests
```

Expected: fails because `FileRecord` does not exist.

- [ ] **Step 3: Implement records and contracts**

Create `src/ListaryOpen.Core/Indexing/FileRecord.cs`:

```csharp
namespace ListaryOpen.Core.Indexing;

public sealed record FileRecord(
    string FullPath,
    string Name,
    string ParentPath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastWriteTime)
{
    public static FileRecord Create(string fullPath, bool isDirectory, long sizeBytes, DateTimeOffset lastWriteTime)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new ArgumentException("Path is required.", nameof(fullPath));
        }

        var normalized = Path.GetFullPath(fullPath.Trim());
        var name = Path.GetFileName(normalized);
        var parent = Path.GetDirectoryName(normalized) ?? string.Empty;
        return new FileRecord(normalized, name, parent, isDirectory, sizeBytes, lastWriteTime);
    }
}
```

Create `src/ListaryOpen.Core/Search/SearchQuery.cs`:

```csharp
namespace ListaryOpen.Core.Search;

public sealed record SearchQuery(string Text, SearchMode Mode, int Limit = 50)
{
    public string NormalizedText => Text.Trim();
}

public enum SearchMode
{
    FilesAndFolders,
    FoldersOnly
}
```

Create `src/ListaryOpen.Core/Search/SearchResult.cs`:

```csharp
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Core.Search;

public sealed record SearchResult(FileRecord Record, double Score, string MatchReason);
```

Create `src/ListaryOpen.Core/Search/ISearchIndex.cs`:

```csharp
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Core.Search;

public interface ISearchIndex
{
    Task UpsertAsync(FileRecord record, CancellationToken cancellationToken);
    Task DeleteAsync(string fullPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Verify green**

Run:

```powershell
dotnet test tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj --filter FileRecordTests
```

Expected: tests pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add src/ListaryOpen.Core tests/ListaryOpen.Core.Tests
git commit -m "feat: add search index domain contracts"
```

### Task 4: Fuzzy Matching and Pinyin Matching

**Files:**
- Create: `src/ListaryOpen.Core/Search/FuzzyMatcher.cs`
- Create: `src/ListaryOpen.Core/Search/PinyinMatcher.cs`
- Create: `tests/ListaryOpen.Core.Tests/Search/FuzzyMatcherTests.cs`
- Create: `tests/ListaryOpen.Core.Tests/Search/PinyinMatcherTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/ListaryOpen.Core.Tests/Search/FuzzyMatcherTests.cs`:

```csharp
using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class FuzzyMatcherTests
{
    [Fact]
    public void ScoreRewardsContiguousNameMatch()
    {
        var score = FuzzyMatcher.Score("invoice", "Invoice-2026.xlsx");
        Assert.True(score > 80);
    }

    [Fact]
    public void ScoreSupportsAbbreviationMatch()
    {
        var score = FuzzyMatcher.Score("iv26", "Invoice 2026.xlsx");
        Assert.True(score > 20);
    }

    [Fact]
    public void ScoreReturnsZeroWhenCharactersAreMissing()
    {
        Assert.Equal(0, FuzzyMatcher.Score("xyz", "Invoice 2026.xlsx"));
    }
}
```

Create `tests/ListaryOpen.Core.Tests/Search/PinyinMatcherTests.cs`:

```csharp
using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class PinyinMatcherTests
{
    [Fact]
    public void ScoreMatchesKnownChinesePinyin()
    {
        var score = PinyinMatcher.Score("fapiao", "发票2026.xlsx");
        Assert.True(score > 50);
    }

    [Fact]
    public void ScoreMatchesKnownChineseInitials()
    {
        var score = PinyinMatcher.Score("fp", "发票2026.xlsx");
        Assert.True(score > 50);
    }
}
```

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj --filter "FuzzyMatcherTests|PinyinMatcherTests"
```

Expected: fails because matchers do not exist.

- [ ] **Step 3: Implement matchers**

Create `src/ListaryOpen.Core/Search/FuzzyMatcher.cs`:

```csharp
namespace ListaryOpen.Core.Search;

public static class FuzzyMatcher
{
    public static double Score(string query, string candidate)
    {
        var q = Normalize(query);
        var c = Normalize(candidate);
        if (q.Length == 0 || c.Length == 0) return 0;
        if (c.Contains(q, StringComparison.Ordinal)) return 100 + q.Length;

        var qi = 0;
        var score = 0d;
        var streak = 0;
        for (var ci = 0; ci < c.Length && qi < q.Length; ci++)
        {
            if (c[ci] != q[qi])
            {
                streak = 0;
                continue;
            }

            qi++;
            streak++;
            score += 8 + streak * 2;
            if (ci == 0 || IsSeparator(c[ci - 1])) score += 10;
        }

        return qi == q.Length ? score : 0;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static bool IsSeparator(char value) => value is ' ' or '-' or '_' or '.';
}
```

Create `src/ListaryOpen.Core/Search/PinyinMatcher.cs`:

```csharp
namespace ListaryOpen.Core.Search;

public static class PinyinMatcher
{
    private static readonly IReadOnlyDictionary<char, string> KnownPinyin = new Dictionary<char, string>
    {
        ['发'] = "fa",
        ['票'] = "piao",
        ['文'] = "wen",
        ['件'] = "jian",
        ['图'] = "tu",
        ['片'] = "pian",
        ['项'] = "xiang",
        ['目'] = "mu"
    };

    public static double Score(string query, string candidate)
    {
        var pinyin = ToPinyin(candidate);
        var initials = ToInitials(candidate);
        return Math.Max(FuzzyMatcher.Score(query, pinyin), FuzzyMatcher.Score(query, initials));
    }

    private static string ToPinyin(string value)
    {
        return string.Concat(value.Select(ch => KnownPinyin.TryGetValue(ch, out var py) ? py : ch.ToString()));
    }

    private static string ToInitials(string value)
    {
        return string.Concat(value.Select(ch => KnownPinyin.TryGetValue(ch, out var py) ? py[0].ToString() : ch.ToString()));
    }
}
```

- [ ] **Step 4: Verify green**

Run:

```powershell
dotnet test tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj --filter "FuzzyMatcherTests|PinyinMatcherTests"
```

Expected: tests pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add src/ListaryOpen.Core/Search tests/ListaryOpen.Core.Tests/Search
git commit -m "feat: add fuzzy and pinyin matching"
```

### Task 5: Result Ranking

**Files:**
- Create: `src/ListaryOpen.Core/Usage/UsageRecord.cs`
- Create: `src/ListaryOpen.Core/Search/ResultRanker.cs`
- Create: `tests/ListaryOpen.Core.Tests/Search/ResultRankerTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/ListaryOpen.Core.Tests/Search/ResultRankerTests.cs`:

```csharp
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;

namespace ListaryOpen.Core.Tests.Search;

public sealed class ResultRankerTests
{
    [Fact]
    public void RankBoostsFrequentlyUsedRecord()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 1, now),
            FileRecord.Create("C:\\Archive\\Invoice.xlsx", false, 1, now)
        };
        var usage = new[]
        {
            new UsageRecord("C:\\Archive\\Invoice.xlsx", 10, now)
        };

        var ranked = ResultRanker.Rank(new SearchQuery("invoice", SearchMode.FilesAndFolders), records, usage, Array.Empty<string>());

        Assert.Equal("C:\\Archive\\Invoice.xlsx", ranked[0].Record.FullPath);
    }

    [Fact]
    public void RankFiltersFoldersOnly()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 1, now),
            FileRecord.Create("C:\\Docs\\Invoices", true, 0, now)
        };

        var ranked = ResultRanker.Rank(new SearchQuery("invoice", SearchMode.FoldersOnly), records, Array.Empty<UsageRecord>(), Array.Empty<string>());

        Assert.Single(ranked);
        Assert.True(ranked[0].Record.IsDirectory);
    }
}
```

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj --filter ResultRankerTests
```

Expected: fails because `ResultRanker` and `UsageRecord` do not exist.

- [ ] **Step 3: Implement ranking**

Create `src/ListaryOpen.Core/Usage/UsageRecord.cs`:

```csharp
namespace ListaryOpen.Core.Usage;

public sealed record UsageRecord(string FullPath, int OpenCount, DateTimeOffset LastUsedAt);
```

Create `src/ListaryOpen.Core/Search/ResultRanker.cs`:

```csharp
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Usage;

namespace ListaryOpen.Core.Search;

public static class ResultRanker
{
    public static IReadOnlyList<SearchResult> Rank(
        SearchQuery query,
        IEnumerable<FileRecord> records,
        IEnumerable<UsageRecord> usageRecords,
        IEnumerable<string> pinnedFolders)
    {
        var usage = usageRecords.ToDictionary(x => x.FullPath, StringComparer.OrdinalIgnoreCase);
        var pinned = pinnedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return records
            .Where(record => query.Mode == SearchMode.FilesAndFolders || record.IsDirectory)
            .Select(record => ScoreRecord(query, record, usage, pinned))
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Record.Name, StringComparer.OrdinalIgnoreCase)
            .Take(query.Limit)
            .ToArray();
    }

    private static SearchResult ScoreRecord(
        SearchQuery query,
        FileRecord record,
        IReadOnlyDictionary<string, UsageRecord> usage,
        IReadOnlySet<string> pinned)
    {
        var nameScore = FuzzyMatcher.Score(query.NormalizedText, record.Name);
        var pathScore = FuzzyMatcher.Score(query.NormalizedText, record.FullPath) * 0.6;
        var pinyinScore = PinyinMatcher.Score(query.NormalizedText, record.Name) * 0.9;
        var score = Math.Max(Math.Max(nameScore, pathScore), pinyinScore);
        var reason = "name";

        if (usage.TryGetValue(record.FullPath, out var used))
        {
            score += Math.Min(50, used.OpenCount * 5);
            score += Math.Max(0, 20 - (DateTimeOffset.UtcNow - used.LastUsedAt).TotalDays);
            reason = "usage";
        }

        if (record.IsDirectory && pinned.Contains(record.FullPath))
        {
            score += 40;
            reason = "pinned";
        }

        return new SearchResult(record, score, reason);
    }
}
```

- [ ] **Step 4: Verify green**

Run:

```powershell
dotnet test tests/ListaryOpen.Core.Tests/ListaryOpen.Core.Tests.csproj --filter ResultRankerTests
```

Expected: tests pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add src/ListaryOpen.Core tests/ListaryOpen.Core.Tests
git commit -m "feat: rank search results"
```

## Milestone 2: Persistence and Index Providers

### Task 6: SQLite Search Index

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Create: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`:

```csharp
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Tests.Search;

public sealed class SqliteSearchIndexTests
{
    [Fact]
    public async Task SearchReturnsInsertedRecord()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
        await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

        var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Invoice.xlsx", results[0].Record.Name);
    }

    [Fact]
    public async Task DeleteRemovesRecord()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
        await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);
        await index.DeleteAsync("C:\\Docs\\Invoice.xlsx", CancellationToken.None);

        var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

        Assert.Empty(results);
    }
}
```

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter SqliteSearchIndexTests
```

Expected: fails because `SqliteSearchIndex` does not exist.

- [ ] **Step 3: Implement SQLite index**

Create `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs` with an `IAsyncDisposable` class that:

```csharp
public sealed class SqliteSearchIndex : ISearchIndex, IAsyncDisposable
{
    public static Task<SqliteSearchIndex> OpenAsync(string dbPath, CancellationToken cancellationToken);
    public Task UpsertAsync(FileRecord record, CancellationToken cancellationToken);
    public Task DeleteAsync(string fullPath, CancellationToken cancellationToken);
    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
    public ValueTask DisposeAsync();
}
```

Implementation rules:

- Use `Microsoft.Data.Sqlite.SqliteConnection`.
- Create tables `files(full_path text primary key, name text, parent_path text, is_directory integer, size_bytes integer, last_write_time text)` and `usage(full_path text primary key, open_count integer, last_used_at text)`.
- `UpsertAsync` uses `insert ... on conflict(full_path) do update`.
- `DeleteAsync` deletes by `full_path`.
- `SearchAsync` reads a bounded candidate set large enough for ranker-compatible matching instead of relying only on SQL `LIKE`. For version 1, query recent rows plus a broad ordered slice such as `select ... from files order by name limit 5000`, map rows to `FileRecord`, read usage rows, and call `ResultRanker.Rank`. This keeps fuzzy, abbreviation, and pinyin matching inside the ranker effective until a dedicated trigram/pinyin candidate table is added.

- [ ] **Step 4: Verify green**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter SqliteSearchIndexTests
```

Expected: tests pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add src/ListaryOpen.Infrastructure tests/ListaryOpen.Infrastructure.Tests
git commit -m "feat: persist searchable file index"
```

### Task 7: Index Provider Contracts and Fallback Provider

**Files:**
- Create: `src/ListaryOpen.Core/Indexing/IndexProviderContracts.cs`
- Create: `src/ListaryOpen.Infrastructure/Indexing/FallbackIndexProvider.cs`
- Create: `tests/ListaryOpen.Infrastructure.Tests/Indexing/FallbackIndexProviderTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Indexing/FallbackIndexProviderTests.cs`:

```csharp
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class FallbackIndexProviderTests
{
    [Fact]
    public async Task ScanAsyncReturnsFilesAndFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "Nested", "Invoice.txt"), "test");

        var provider = new FallbackIndexProvider();
        var records = new List<FileRecord>();
        await foreach (var record in provider.ScanAsync(new IndexRoot(root), CancellationToken.None))
        {
            records.Add(record);
        }

        Assert.Contains(records, x => x.IsDirectory && x.Name == "Nested");
        Assert.Contains(records, x => !x.IsDirectory && x.Name == "Invoice.txt");
    }
}
```

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FallbackIndexProviderTests
```

Expected: fails because provider contracts do not exist.

- [ ] **Step 3: Implement contracts and fallback provider**

Create `src/ListaryOpen.Core/Indexing/IndexProviderContracts.cs`:

```csharp
namespace ListaryOpen.Core.Indexing;

public sealed record IndexRoot(string Path);

public sealed record VolumeInfo(string RootPath, string FileSystemName, bool IsReady);

public sealed record IndexProviderStatus(string ProviderName, string RootPath, bool IsAvailable, string Message);

public interface IIndexProvider
{
    string Name { get; }
    bool CanIndex(VolumeInfo volume);
    IAsyncEnumerable<FileRecord> ScanAsync(IndexRoot root, CancellationToken cancellationToken);
}
```

Create `src/ListaryOpen.Infrastructure/Indexing/FallbackIndexProvider.cs`:

```csharp
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class FallbackIndexProvider : IIndexProvider
{
    public string Name => "Fallback";

    public bool CanIndex(VolumeInfo volume) => volume.IsReady;

    public async IAsyncEnumerable<FileRecord> ScanAsync(IndexRoot root, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(root.Path, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new DirectoryInfo(directory);
            yield return FileRecord.Create(info.FullName, true, 0, new DateTimeOffset(info.LastWriteTimeUtc));
            await Task.Yield();
        }

        foreach (var file in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            yield return FileRecord.Create(info.FullName, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
            await Task.Yield();
        }
    }
}
```

- [ ] **Step 4: Verify green**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FallbackIndexProviderTests
```

Expected: tests pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add src/ListaryOpen.Core src/ListaryOpen.Infrastructure tests/ListaryOpen.Infrastructure.Tests
git commit -m "feat: add fallback indexing provider"
```

### Task 8: NTFS Provider Boundary and Volume Selection

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Indexing/NtfsIndexProvider.cs`
- Create: `src/ListaryOpen.Infrastructure/Indexing/ElevatedIndexerClient.cs`
- Create: `src/ListaryOpen.Infrastructure/Indexing/VolumeIndexer.cs`
- Create: `tests/ListaryOpen.Infrastructure.Tests/Indexing/ProviderSelectionTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Indexing/ProviderSelectionTests.cs`:

```csharp
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ProviderSelectionTests
{
    [Fact]
    public void SelectProviderPrefersNtfsForReadyNtfsVolume()
    {
        var ntfs = new NtfsIndexProvider(new DisabledElevatedIndexerClient());
        var fallback = new FallbackIndexProvider();
        var indexer = new VolumeIndexer(new IIndexProvider[] { fallback, ntfs });

        var selected = indexer.SelectProvider(new VolumeInfo("C:\\", "NTFS", true));

        Assert.Equal("NTFS", selected.Name);
    }

    [Fact]
    public void SelectProviderFallsBackForNonNtfsVolume()
    {
        var ntfs = new NtfsIndexProvider(new DisabledElevatedIndexerClient());
        var fallback = new FallbackIndexProvider();
        var indexer = new VolumeIndexer(new IIndexProvider[] { ntfs, fallback });

        var selected = indexer.SelectProvider(new VolumeInfo("Z:\\", "exFAT", true));

        Assert.Equal("Fallback", selected.Name);
    }
}
```

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter ProviderSelectionTests
```

Expected: fails because NTFS provider and selector do not exist.

- [ ] **Step 3: Implement provider boundary**

Create `src/ListaryOpen.Infrastructure/Indexing/ElevatedIndexerClient.cs`:

```csharp
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public interface IElevatedIndexerClient
{
    bool IsAvailable { get; }
    IAsyncEnumerable<FileRecord> ScanNtfsAsync(IndexRoot root, CancellationToken cancellationToken);
}

public sealed class DisabledElevatedIndexerClient : IElevatedIndexerClient
{
    public bool IsAvailable => false;

    public async IAsyncEnumerable<FileRecord> ScanNtfsAsync(IndexRoot root, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
```

Create `src/ListaryOpen.Infrastructure/Indexing/NtfsIndexProvider.cs`:

```csharp
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class NtfsIndexProvider : IIndexProvider
{
    private readonly IElevatedIndexerClient _client;

    public NtfsIndexProvider(IElevatedIndexerClient client)
    {
        _client = client;
    }

    public string Name => "NTFS";

    public bool CanIndex(VolumeInfo volume)
    {
        return volume.IsReady && string.Equals(volume.FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase);
    }

    public IAsyncEnumerable<FileRecord> ScanAsync(IndexRoot root, CancellationToken cancellationToken)
    {
        return _client.ScanNtfsAsync(root, cancellationToken);
    }
}
```

Create `src/ListaryOpen.Infrastructure/Indexing/VolumeIndexer.cs`:

```csharp
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class VolumeIndexer
{
    private readonly IReadOnlyList<IIndexProvider> _providers;

    public VolumeIndexer(IEnumerable<IIndexProvider> providers)
    {
        _providers = providers.ToArray();
    }

    public IIndexProvider SelectProvider(VolumeInfo volume)
    {
        return _providers
            .OrderByDescending(provider => provider.Name == "NTFS")
            .First(provider => provider.CanIndex(volume));
    }
}
```

- [ ] **Step 4: Verify green**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter ProviderSelectionTests
```

Expected: tests pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add src/ListaryOpen.Infrastructure tests/ListaryOpen.Infrastructure.Tests
git commit -m "feat: select ntfs-first indexing providers"
```

## Milestone 3: Dialog Integration

### Task 9: DialogBridge with Permission Boundary

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Dialog/IDialogAutomation.cs`
- Create: `src/ListaryOpen.Infrastructure/Dialog/DialogBridge.cs`
- Create: `tests/ListaryOpen.Infrastructure.Tests/Dialog/DialogBridgeTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Dialog/DialogBridgeTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Dialog;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class DialogBridgeTests
{
    [Fact]
    public async Task JumpToFolderReportsUnsupportedWhenNoStandardDialogIsActive()
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.Unsupported("No standard dialog"));
        var bridge = new DialogBridge(automation);

        var result = await bridge.JumpToFolderAsync("C:\\Docs", CancellationToken.None);

        Assert.Equal(DialogJumpStatus.UnsupportedDialog, result.Status);
    }

    [Fact]
    public async Task JumpToFolderReportsPermissionLimitedForElevatedTarget()
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.PermissionLimited("Elevated target"));
        var bridge = new DialogBridge(automation);

        var result = await bridge.JumpToFolderAsync("C:\\Docs", CancellationToken.None);

        Assert.Equal(DialogJumpStatus.PermissionLimited, result.Status);
    }

    [Fact]
    public async Task JumpToFolderChangesFolderForStandardDialog()
    {
        var automation = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(automation);

        var result = await bridge.JumpToFolderAsync("C:\\Docs", CancellationToken.None);

        Assert.Equal(DialogJumpStatus.Success, result.Status);
        Assert.Equal("C:\\Docs", automation.LastFolder);
    }
}
```

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter DialogBridgeTests
```

Expected: fails because dialog contracts do not exist.

- [ ] **Step 3: Implement DialogBridge contracts**

Create `src/ListaryOpen.Infrastructure/Dialog/IDialogAutomation.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Dialog;

public enum DialogProbeStatus
{
    StandardDialog,
    UnsupportedDialog,
    PermissionLimited
}

public sealed record DialogProbeResult(DialogProbeStatus Status, string Message)
{
    public static DialogProbeResult StandardDialog() => new(DialogProbeStatus.StandardDialog, "Standard dialog active.");
    public static DialogProbeResult Unsupported(string message) => new(DialogProbeStatus.UnsupportedDialog, message);
    public static DialogProbeResult PermissionLimited(string message) => new(DialogProbeStatus.PermissionLimited, message);
}

public enum DialogJumpStatus
{
    Success,
    UnsupportedDialog,
    PermissionLimited,
    Failed
}

public sealed record DialogJumpResult(DialogJumpStatus Status, string Message);

public interface IDialogAutomation
{
    DialogProbeResult ProbeActiveDialog();
    Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken);
}
```

Create `src/ListaryOpen.Infrastructure/Dialog/DialogBridge.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Dialog;

public sealed class DialogBridge
{
    private readonly IDialogAutomation _automation;

    public DialogBridge(IDialogAutomation automation)
    {
        _automation = automation;
    }

    public async Task<DialogJumpResult> JumpToFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        var probe = _automation.ProbeActiveDialog();
        if (probe.Status == DialogProbeStatus.UnsupportedDialog)
        {
            return new DialogJumpResult(DialogJumpStatus.UnsupportedDialog, probe.Message);
        }

        if (probe.Status == DialogProbeStatus.PermissionLimited)
        {
            return new DialogJumpResult(DialogJumpStatus.PermissionLimited, probe.Message);
        }

        var success = await _automation.SetFolderAsync(folderPath, cancellationToken);
        return success
            ? new DialogJumpResult(DialogJumpStatus.Success, "Dialog folder changed.")
            : new DialogJumpResult(DialogJumpStatus.Failed, "Dialog folder could not be changed.");
    }
}
```

Append this fake to the test file:

```csharp
internal sealed class FakeDialogAutomation : IDialogAutomation
{
    private readonly DialogProbeResult _probe;

    public FakeDialogAutomation(DialogProbeResult probe)
    {
        _probe = probe;
    }

    public string? LastFolder { get; private set; }

    public DialogProbeResult ProbeActiveDialog() => _probe;

    public Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        LastFolder = folderPath;
        return Task.FromResult(true);
    }
}
```

- [ ] **Step 4: Verify green**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter DialogBridgeTests
```

Expected: tests pass.

- [ ] **Step 5: Commit**

Run:

```powershell
git add src/ListaryOpen.Infrastructure/Dialog tests/ListaryOpen.Infrastructure.Tests/Dialog
git commit -m "feat: add dialog bridge permission boundary"
```

### Task 10: Windows Dialog Automation Adapter

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Dialog/WindowsDialogAutomation.cs`
- Create: `src/ListaryOpen.Infrastructure/Windows/NativeMethods.cs`
- Modify: `docs/manual-test-checklist.md`

- [ ] **Step 1: Add manual acceptance checklist**

Create `docs/manual-test-checklist.md`:

```markdown
# Manual Windows Acceptance Checklist

- Start ListaryOpen.
- Open Notepad.
- Open Save As.
- Press Ctrl+G.
- Choose a known folder.
- Verify the Save As dialog changes to that folder.
- Start a non-elevated Win32 or .NET application with an Open dialog.
- Press Ctrl+G and verify folder jump.
- Start an administrator-elevated app with an Open dialog.
- Press Ctrl+G and verify ListaryOpen reports a permission-limited target.
- Open a custom unsupported dialog and verify ListaryOpen reports unsupported dialog.
```

- [ ] **Step 2: Implement native adapter**

Create `src/ListaryOpen.Infrastructure/Windows/NativeMethods.cs`:

```csharp
using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Windows;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassName(IntPtr hWnd, char[] className, int maxCount);
}
```

Create `src/ListaryOpen.Infrastructure/Dialog/WindowsDialogAutomation.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Dialog;

public sealed class WindowsDialogAutomation : IDialogAutomation
{
    private AutomationElement? _activeDialog;

    public DialogProbeResult ProbeActiveDialog()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return DialogProbeResult.Unsupported("No foreground window.");
        }

        var className = GetClassName(handle);
        if (!string.Equals(className, "#32770", StringComparison.Ordinal))
        {
            return DialogProbeResult.Unsupported("The active window is not a standard dialog.");
        }

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        try
        {
            var process = Process.GetProcessById((int)processId);
            if (process.MainWindowHandle == IntPtr.Zero)
            {
                return DialogProbeResult.Unsupported("Dialog process has no main window.");
            }
        }
        catch (ArgumentException)
        {
            return DialogProbeResult.Unsupported("Dialog process exited.");
        }

        _activeDialog = AutomationElement.FromHandle(handle);
        return _activeDialog is null
            ? DialogProbeResult.Unsupported("Dialog automation tree is unavailable.")
            : DialogProbeResult.StandardDialog();
    }

    public Task<bool> SetFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        if (_activeDialog is null || !Directory.Exists(folderPath))
        {
            return Task.FromResult(false);
        }

        var edit = _activeDialog.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        if (edit is null)
        {
            return Task.FromResult(false);
        }

        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern valuePattern)
        {
            valuePattern.SetValue(folderPath);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private static string GetClassName(IntPtr handle)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassName(handle, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }
}
```

- [ ] **Step 3: Build**

Run:

```powershell
dotnet build ListaryOpen.sln
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

Run:

```powershell
git add src/ListaryOpen.Infrastructure docs/manual-test-checklist.md
git commit -m "feat: add windows dialog automation adapter"
```

## Milestone 4: WPF App Shell

### Task 11: Tray, Hotkeys, and Search Panel Shell

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml`
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Modify: `src/ListaryOpen.App/MainWindow.xaml`
- Create: `src/ListaryOpen.App/SearchPanel.xaml`
- Create: `src/ListaryOpen.App/SearchPanel.xaml.cs`
- Create: `src/ListaryOpen.App/Tray/TrayController.cs`
- Create: `src/ListaryOpen.Infrastructure/Windows/HotkeyService.cs`

- [ ] **Step 1: Implement hotkey service**

Create `src/ListaryOpen.Infrastructure/Windows/HotkeyService.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Windows;

public sealed class HotkeyService : IDisposable
{
    public event EventHandler<string>? HotkeyPressed;

    public void RegisterDefaults()
    {
    }

    public void RaiseForTests(string name)
    {
        HotkeyPressed?.Invoke(this, name);
    }

    public void Dispose()
    {
    }
}
```

- [ ] **Step 2: Create search panel XAML**

Create `src/ListaryOpen.App/SearchPanel.xaml`:

```xml
<Window x:Class="ListaryOpen.App.SearchPanel"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="ListaryOpen Search"
        Width="720"
        Height="420"
        WindowStartupLocation="CenterScreen"
        Topmost="True">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
        <TextBox x:Name="QueryBox" FontSize="22" Margin="0,0,0,12" />
        <ListBox x:Name="ResultsList" Grid.Row="1" />
    </Grid>
</Window>
```

Create `src/ListaryOpen.App/SearchPanel.xaml.cs`:

```csharp
using System.Windows;

namespace ListaryOpen.App;

public partial class SearchPanel : Window
{
    public SearchPanel()
    {
        InitializeComponent();
    }

    public void ActivateSearch()
    {
        Show();
        Activate();
        QueryBox.Focus();
    }
}
```

- [ ] **Step 3: Create tray controller**

Create `src/ListaryOpen.App/Tray/TrayController.cs`:

```csharp
using Hardcodet.Wpf.TaskbarNotification;
using System.Windows;

namespace ListaryOpen.App.Tray;

public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayController(Window settingsWindow)
    {
        _icon = new TaskbarIcon { ToolTipText = "ListaryOpen" };
        _icon.TrayMouseDoubleClick += (_, _) => settingsWindow.Show();
    }

    public void Dispose()
    {
        _icon.Dispose();
    }
}
```

- [ ] **Step 4: Wire app startup**

Update `src/ListaryOpen.App/App.xaml.cs` to create `MainWindow`, `SearchPanel`, `TrayController`, and `HotkeyService`. Register default hotkeys and call `SearchPanel.ActivateSearch()` when the search hotkey is raised.

- [ ] **Step 5: Build and run**

Run:

```powershell
dotnet build ListaryOpen.sln
dotnet run --project src/ListaryOpen.App/ListaryOpen.App.csproj
```

Expected: settings window can open, tray icon exists, and the search panel can be shown from the wired hotkey path or test hook.

- [ ] **Step 6: Commit**

Run:

```powershell
git add src/ListaryOpen.App src/ListaryOpen.Infrastructure
git commit -m "feat: add wpf tray and search shell"
```

### Task 12: Search Panel ViewModel and Real Queries

**Files:**
- Create: `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`
- Modify: `src/ListaryOpen.App/SearchPanel.xaml`
- Modify: `src/ListaryOpen.App/SearchPanel.xaml.cs`

- [ ] **Step 1: Implement view model**

Create `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ListaryOpen.Core.Search;

namespace ListaryOpen.App.ViewModels;

public sealed class SearchPanelViewModel : INotifyPropertyChanged
{
    private readonly ISearchIndex _index;
    private string _queryText = string.Empty;

    public SearchPanelViewModel(ISearchIndex index)
    {
        _index = index;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SearchResult> Results { get; } = new();

    public string QueryText
    {
        get => _queryText;
        set
        {
            if (_queryText == value) return;
            _queryText = value;
            OnPropertyChanged();
            _ = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        Results.Clear();
        if (string.IsNullOrWhiteSpace(_queryText)) return;
        var results = await _index.SearchAsync(new SearchQuery(_queryText, SearchMode.FilesAndFolders), CancellationToken.None);
        foreach (var result in results)
        {
            Results.Add(result);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
```

- [ ] **Step 2: Bind search panel**

Update `SearchPanel.xaml` so `QueryBox.Text` binds to `QueryText` with `UpdateSourceTrigger=PropertyChanged` and `ResultsList.ItemsSource` binds to `Results`.

- [ ] **Step 3: Build**

Run:

```powershell
dotnet build ListaryOpen.sln
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

Run:

```powershell
git add src/ListaryOpen.App
git commit -m "feat: bind search panel to index"
```

## Milestone 5: Settings, Explorer Context, and Helper

### Task 13: Settings Model and Settings Window

**Files:**
- Create: `src/ListaryOpen.Core/Settings/AppSettings.cs`
- Create: `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/ListaryOpen.App/MainWindow.xaml`

- [ ] **Step 1: Add settings model**

Create `src/ListaryOpen.Core/Settings/AppSettings.cs`:

```csharp
namespace ListaryOpen.Core.Settings;

public sealed record AppSettings(
    IReadOnlyList<string> IndexedRoots,
    IReadOnlyList<string> ExcludedPaths,
    IReadOnlyList<string> PinnedFolders,
    string SearchHotkey,
    string DialogHotkey,
    bool QuickSaveOpenEnabled)
{
    public static AppSettings Defaults() => new(
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) },
        Array.Empty<string>(),
        Array.Empty<string>(),
        "Ctrl+Space",
        "Ctrl+G",
        true);
}
```

- [ ] **Step 2: Add settings view model and bind window**

Create `SettingsViewModel` with an `AppSettings Settings` property initialized from `AppSettings.Defaults()`. Bind `MainWindow.xaml` to show indexed roots, hotkeys, and Quick Save / Open enabled state.

- [ ] **Step 3: Build**

Run:

```powershell
dotnet build ListaryOpen.sln
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

Run:

```powershell
git add src/ListaryOpen.Core src/ListaryOpen.App
git commit -m "feat: add settings model and window"
```

### Task 14: Explorer Folder Tracking

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Windows/ExplorerTracker.cs`

- [ ] **Step 1: Implement tracker contract**

Create `src/ListaryOpen.Infrastructure/Windows/ExplorerTracker.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Windows;

public sealed class ExplorerTracker
{
    private string? _lastFolder;

    public string? LastFolder => _lastFolder;

    public void ObserveFolderForTests(string folderPath)
    {
        if (Directory.Exists(folderPath))
        {
            _lastFolder = folderPath;
        }
    }
}
```

- [ ] **Step 2: Wire into dialog mode**

When `Ctrl+G` is invoked and `ExplorerTracker.LastFolder` exists, show that folder as the first item in folder-focused search results.

- [ ] **Step 3: Build**

Run:

```powershell
dotnet build ListaryOpen.sln
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

Run:

```powershell
git add src/ListaryOpen.Infrastructure src/ListaryOpen.App
git commit -m "feat: track explorer folder jump target"
```

### Task 15: Elevated Indexer Helper Shell

**Files:**
- Modify: `src/ListaryOpen.Indexer.Elevated/Program.cs`
- Modify: `src/ListaryOpen.Infrastructure/Indexing/ElevatedIndexerClient.cs`

- [ ] **Step 1: Implement helper command protocol**

Update `src/ListaryOpen.Indexer.Elevated/Program.cs`:

```csharp
using System.Text.Json;

if (args.Length == 2 && args[0] == "scan")
{
    var root = args[1];
    Console.WriteLine(JsonSerializer.Serialize(new { type = "scan-started", root }));
    return 0;
}

Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan <root>");
return 2;
```

- [ ] **Step 2: Implement client availability**

Update `ElevatedIndexerClient` so a concrete client can locate the helper executable path, report `IsAvailable`, and return an empty stream with a diagnostic message when helper execution fails.

- [ ] **Step 3: Build**

Run:

```powershell
dotnet build ListaryOpen.sln
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

Run:

```powershell
git add src/ListaryOpen.Indexer.Elevated src/ListaryOpen.Infrastructure
git commit -m "feat: add elevated indexer helper shell"
```

## Milestone 6: End-to-End MVP Wiring

### Task 16: NTFS USN Reader and Helper Output

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Indexing/Ntfs/UsnRecordParser.cs`
- Create: `tests/ListaryOpen.Infrastructure.Tests/Indexing/UsnRecordParserTests.cs`
- Create: `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsNativeMethods.cs`
- Create: `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnJournalReader.cs`
- Modify: `src/ListaryOpen.Indexer.Elevated/Program.cs`

- [ ] **Step 1: Write parser tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Indexing/UsnRecordParserTests.cs`:

```csharp
using System.Text;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class UsnRecordParserTests
{
    [Fact]
    public void ParseV2ReadsFileNameAndAttributes()
    {
        var name = Encoding.Unicode.GetBytes("Invoice.txt");
        var buffer = new byte[60 + name.Length];
        BitConverter.GetBytes(buffer.Length).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
        BitConverter.GetBytes(0x80u).CopyTo(buffer, 52);
        BitConverter.GetBytes((ushort)name.Length).CopyTo(buffer, 56);
        BitConverter.GetBytes((ushort)60).CopyTo(buffer, 58);
        name.CopyTo(buffer, 60);

        var parsed = UsnRecordParser.ParseV2(buffer);

        Assert.Equal("Invoice.txt", parsed.Name);
        Assert.False(parsed.IsDirectory);
    }

    [Fact]
    public void ParseV2DetectsDirectoryAttribute()
    {
        var name = Encoding.Unicode.GetBytes("Projects");
        var buffer = new byte[60 + name.Length];
        BitConverter.GetBytes(buffer.Length).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
        BitConverter.GetBytes(0x10u).CopyTo(buffer, 52);
        BitConverter.GetBytes((ushort)name.Length).CopyTo(buffer, 56);
        BitConverter.GetBytes((ushort)60).CopyTo(buffer, 58);
        name.CopyTo(buffer, 60);

        var parsed = UsnRecordParser.ParseV2(buffer);

        Assert.Equal("Projects", parsed.Name);
        Assert.True(parsed.IsDirectory);
    }
}
```

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter UsnRecordParserTests
```

Expected: fails because `UsnRecordParser` does not exist.

- [ ] **Step 3: Implement parser**

Create `src/ListaryOpen.Infrastructure/Indexing/Ntfs/UsnRecordParser.cs`:

```csharp
using System.Buffers.Binary;
using System.Text;

namespace ListaryOpen.Infrastructure.Indexing.Ntfs;

public sealed record ParsedUsnRecord(string Name, bool IsDirectory);

public static class UsnRecordParser
{
    private const uint FileAttributeDirectory = 0x10;

    public static ParsedUsnRecord ParseV2(ReadOnlySpan<byte> record)
    {
        if (record.Length < 60)
        {
            throw new ArgumentException("USN record is too short.", nameof(record));
        }

        var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(record[0..4]);
        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(record[4..6]);
        if (majorVersion != 2)
        {
            throw new NotSupportedException("Only USN_RECORD_V2 is supported in version 1.");
        }

        if (recordLength > record.Length)
        {
            throw new ArgumentException("USN record length exceeds buffer length.", nameof(record));
        }

        var fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..56]);
        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..58]);
        var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..60]);
        var nameBytes = record.Slice(fileNameOffset, fileNameLength);
        var name = Encoding.Unicode.GetString(nameBytes);
        return new ParsedUsnRecord(name, (fileAttributes & FileAttributeDirectory) != 0);
    }
}
```

- [ ] **Step 4: Verify parser green**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter UsnRecordParserTests
```

Expected: tests pass.

- [ ] **Step 5: Add native NTFS reader**

Create `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsNativeMethods.cs` with P/Invoke definitions for `CreateFileW`, `DeviceIoControl`, `CloseHandle`, `FSCTL_ENUM_USN_DATA`, and `FSCTL_QUERY_USN_JOURNAL`.

Create `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnJournalReader.cs` with this public API:

```csharp
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

public sealed class NtfsUsnJournalReader
{
    public IAsyncEnumerable<FileRecord> EnumerateVolumeAsync(string volumeRoot, CancellationToken cancellationToken);
}
```

Implementation rules:

- Open the volume path in the form `\\.\C:` with read sharing.
- Use `FSCTL_ENUM_USN_DATA` to enumerate file reference records for the initial snapshot.
- Parse each USN record with the shared `UsnRecordParser`.
- Reconstruct full paths from parent file reference numbers before emitting `FileRecord`.
- Skip records without a resolved parent path.
- Emit directories and files.
- Stop cleanly when `DeviceIoControl` reports no more data.

- [ ] **Step 6: Emit helper records**

Update `src/ListaryOpen.Indexer.Elevated/Program.cs` so `scan <root>` streams newline-delimited JSON records:

```json
{"fullPath":"C:\\Docs\\Invoice.xlsx","isDirectory":false,"sizeBytes":0,"lastWriteTime":"2026-07-03T00:00:00+00:00"}
```

The helper exits with code `0` on successful scan, code `2` for invalid arguments, and code `5` for NTFS access failure.

- [ ] **Step 7: Build**

Run:

```powershell
dotnet build ListaryOpen.sln
```

Expected: build succeeds.

- [ ] **Step 8: Commit**

Run:

```powershell
git add src/ListaryOpen.Infrastructure src/ListaryOpen.Indexer.Elevated tests/ListaryOpen.Infrastructure.Tests
git commit -m "feat: add ntfs usn indexing reader"
```

### Task 17: Startup Composition

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`

- [ ] **Step 1: Compose services**

Update app startup to:

- Open SQLite index at `%LocalAppData%\ListaryOpen\index.db`.
- Create `FallbackIndexProvider`, `NtfsIndexProvider`, and `VolumeIndexer`.
- Create `WindowsDialogAutomation` and `DialogBridge`.
- Create `SearchPanelViewModel` with the SQLite index.
- Register `Ctrl+Space` to show normal search.
- Register `Ctrl+G` to show folder-focused search and call `DialogBridge`.

- [ ] **Step 2: Build**

Run:

```powershell
dotnet build ListaryOpen.sln
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

Run:

```powershell
git add src/ListaryOpen.App
git commit -m "feat: compose app services"
```

### Task 18: Verification and Manual Acceptance

**Files:**
- Modify: `docs/manual-test-checklist.md`

- [ ] **Step 1: Run automated tests**

Run:

```powershell
dotnet test ListaryOpen.sln
```

Expected: all tests pass.

- [ ] **Step 2: Run app**

Run:

```powershell
dotnet run --project src/ListaryOpen.App/ListaryOpen.App.csproj
```

Expected: app starts, tray icon appears, settings window opens, search panel opens.

- [ ] **Step 3: Complete manual tests**

Use `docs/manual-test-checklist.md` and record pass/fail notes under each item.

- [ ] **Step 4: Commit manual checklist results**

Run:

```powershell
git add docs/manual-test-checklist.md
git commit -m "test: record manual windows acceptance results"
```
