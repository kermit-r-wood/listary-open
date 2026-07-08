# Search Maturity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the P0 and P1 search maturity work from `docs/superpowers/specs/2026-07-09-search-maturity-design.md`.

**Architecture:** Keep the existing provider/coordinator/SQLite shape. Add small contracts where the current shape cannot express scan completeness or parsed queries. Implement USN catch-up as a minimal, testable core first; directory subtree diffs stay deferred by marking roots dirty.

**Tech Stack:** C#/.NET 8, xUnit, Microsoft.Data.Sqlite, Windows NTFS DeviceIoControl APIs.

---

## Scope

This plan implements:

- P0 fixed NTFS full-scan watermark.
- P0 safe prune behavior for incomplete scans.
- P0 network drive classification.
- P0 query gate reduction and one-character fuzzy guard.
- P1 volume checkpoint storage.
- P1 minimal USN catch-up decision/apply core.
- P1 query syntax V1: bare terms, quoted phrase, `ext:`, `file:`, `folder:`, `path:`, and `!`.

This plan does not implement P2 parity work: ReFS fast indexing, full Everything grammar, regex, size/date/attribute filters, remote servers, SDK, IPC, or a persistent watcher service.

## File Structure

- Modify: `src/ListaryOpen.Core/Indexing/IndexProviderContracts.cs`
  - Add optional `DriveType` to `VolumeInfo`.
  - Add a small scan completion result only if needed by coordinator tests.
- Modify: `src/ListaryOpen.Infrastructure/Indexing/IndexingCoordinator.cs`
  - Preserve old records when scans are incomplete.
  - Classify network drives before NTFS.
- Modify: `src/ListaryOpen.Infrastructure/Indexing/FallbackIndexProvider.cs`
  - Throw for root-level scan failures instead of returning a successful empty scan.
- Modify: `src/ListaryOpen.Infrastructure/Indexing/NtfsIndexProvider.cs`
  - Reject network volumes even if they report `NTFS`.
- Modify: `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsNativeMethods.cs`
  - Add `FSCTL_READ_USN_JOURNAL` structures for P1 catch-up.
- Modify: `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnJournalReader.cs`
  - Use one queried journal watermark for both full-scan enumeration passes.
  - Add minimal catch-up reader surface if the native API can be tested behind an internal adapter.
- Create: `src/ListaryOpen.Infrastructure/Indexing/Ntfs/UsnJournalCheckpoint.cs`
  - Store checkpoint value object and catch-up decision result.
- Create: `src/ListaryOpen.Infrastructure/Indexing/Ntfs/UsnJournalCatchUpPlanner.cs`
  - Decide full rescan vs catch-up and classify simple changes.
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
  - Add checkpoint table/storage.
  - Add parsed query filtering.
  - Release `_connectionGate` before ranking.
  - Skip expensive fuzzy UDF pass for one-character queries.
- Modify: `src/ListaryOpen.Core/Search/SearchQuery.cs`
  - Add parsed query model while preserving the public constructor.
- Create: `src/ListaryOpen.Core/Search/ParsedSearchQuery.cs`
  - Parse syntax V1.
- Modify tests under:
  - `tests/ListaryOpen.Infrastructure.Tests/Indexing/*`
  - `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`
  - `tests/ListaryOpen.Core.Tests/Search/SearchQueryTests.cs`

## Task 1: P0 Index Safety

**Files:**
- Modify: `src/ListaryOpen.Core/Indexing/IndexProviderContracts.cs`
- Modify: `src/ListaryOpen.Infrastructure/Indexing/NtfsIndexProvider.cs`
- Modify: `src/ListaryOpen.Infrastructure/Indexing/IndexingCoordinator.cs`
- Modify: `src/ListaryOpen.Infrastructure/Indexing/FallbackIndexProvider.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/ProviderSelectionTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/IndexingCoordinatorTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/FallbackIndexProviderTests.cs`

- [ ] **Step 1: Write failing network-drive provider test**

Add to `ProviderSelectionTests`:

```csharp
[Fact]
public void SelectProviderFallsBackForMappedNetworkDriveEvenWhenFormatReportsNtfs()
{
    var ntfs = new NtfsIndexProvider(new RecordingElevatedIndexerClient());
    var fallback = new FallbackIndexProvider();
    var indexer = new VolumeIndexer(new IIndexProvider[] { ntfs, fallback });

    var selected = indexer.SelectProvider(new VolumeInfo("Z:\\", "NTFS", true, DriveType.Network));

    Assert.Equal("Fallback", selected.Name);
}
```

- [ ] **Step 2: Run network-drive test and verify RED**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ProviderSelectionTests.SelectProviderFallsBackForMappedNetworkDriveEvenWhenFormatReportsNtfs"
```

Expected: FAIL because `NtfsIndexProvider` still accepts ready NTFS volumes without considering drive type.

- [ ] **Step 3: Implement network-drive classification**

Change `VolumeInfo` to:

```csharp
public sealed record VolumeInfo(
    string RootPath,
    string FileSystemName,
    bool IsReady,
    DriveType DriveType = DriveType.Unknown);
```

Change `NtfsIndexProvider.CanIndex` to also require:

```csharp
&& volume.DriveType != DriveType.Network
```

Change `IndexingCoordinator.ResolveVolume` so local drive roots return `drive.DriveType`, and UNC roots return `DriveType.Network`.

- [ ] **Step 4: Run provider selection tests and verify GREEN**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~ProviderSelectionTests"
```

Expected: PASS.

- [ ] **Step 5: Write failing incomplete-scan prune test**

Add to `IndexingCoordinatorTests`:

```csharp
[Fact]
public async Task IndexRootsAsyncDoesNotPruneWhenProviderFailsAfterPartialOutput()
{
    var rootPath = CreateTempDirectory();
    var dbPath = CreateTempDbPath();

    try
    {
        var currentRecord = FileRecord.Create(
            Path.Combine(rootPath, "CurrentRecord.txt"),
            isDirectory: false,
            sizeBytes: 1,
            DateTimeOffset.UtcNow);
        var staleRecord = FileRecord.Create(
            Path.Combine(rootPath, "KeepBecauseScanIncomplete.txt"),
            isDirectory: false,
            sizeBytes: 1,
            DateTimeOffset.UtcNow);

        await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
        await index.UpsertManyAsync(new[] { currentRecord, staleRecord }, CancellationToken.None);
        var statuses = new List<IndexingStatus>();
        var coordinator = new IndexingCoordinator(
            index,
            new VolumeIndexer(new IIndexProvider[] { new PartialThenThrowingProvider(currentRecord) }),
            new FallbackIndexProvider(),
            _ => new VolumeInfo(Path.GetPathRoot(rootPath)!, "PartialProvider", true),
            batchSize: 1);
        coordinator.StatusChanged += (_, status) => statuses.Add(status);

        await coordinator.IndexRootsAsync(new[] { new IndexRoot(rootPath) }, CancellationToken.None);

        var currentResults = await index.SearchAsync(new SearchQuery("CurrentRecord", SearchMode.FilesAndFolders), CancellationToken.None);
        var staleResults = await index.SearchAsync(new SearchQuery("KeepBecauseScanIncomplete", SearchMode.FilesAndFolders), CancellationToken.None);

        Assert.Contains(currentResults, result => result.Record.PathKey == currentRecord.PathKey);
        Assert.Contains(staleResults, result => result.Record.PathKey == staleRecord.PathKey);
        Assert.Equal(IndexingRunState.Failed, statuses[^1].State);
    }
    finally
    {
        DeleteDirectoryIfExists(rootPath);
        DeleteFileIfExists(dbPath);
    }
}
```

Add the helper:

```csharp
private sealed class PartialThenThrowingProvider : IIndexProvider
{
    private readonly FileRecord _record;

    public PartialThenThrowingProvider(FileRecord record)
    {
        _record = record;
    }

    public string Name => "PartialProvider";

    public bool CanIndex(VolumeInfo volume) => volume.IsReady;

    public async IAsyncEnumerable<FileRecord> ScanAsync(
        IndexRoot root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return _record;
        await Task.FromException(new IOException("Scan failed after partial output."));
    }
}
```

- [ ] **Step 6: Run incomplete-scan prune test and verify RED**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~IndexingCoordinatorTests.IndexRootsAsyncDoesNotPruneWhenProviderFailsAfterPartialOutput"
```

Expected: FAIL if the stale row is pruned or the failure is not reported.

- [ ] **Step 7: Implement incomplete-scan prune safety**

Update `IndexingCoordinator.ScanAndUpsertAsync` to throw an `IndexProviderScanException` that carries whether records were accepted before failure:

```csharp
private sealed class IndexProviderScanException : Exception
{
    public IndexProviderScanException(string message, Exception innerException, bool partialRecordsAccepted = false)
        : base(message, innerException)
    {
        PartialRecordsAccepted = partialRecordsAccepted;
    }

    public bool PartialRecordsAccepted { get; }
}
```

Track accepted records after each successful flush and set `partialRecordsAccepted: indexedCount > 0 || batch.Count > 0` when `MoveNextAsync` fails.

For non-NTFS providers, catch `IndexProviderScanException` in `IndexRootWithProviderAsync`, report failed status, and return `HadFailure: true` without prune.

For NTFS providers, if the exception has `PartialRecordsAccepted`, report failure and return without fallback or prune.

- [ ] **Step 8: Run coordinator tests and verify GREEN**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~IndexingCoordinatorTests"
```

Expected: PASS.

- [ ] **Step 9: Write failing fallback root failure test**

Add to `FallbackIndexProviderTests`:

```csharp
[Fact]
public async Task ScanAsyncThrowsForMissingRootInsteadOfReturningSuccessfulEmptyScan()
{
    var root = Path.Combine(Path.GetTempPath(), "listary-open-missing-" + Guid.NewGuid());
    var provider = new FallbackIndexProvider();

    await Assert.ThrowsAnyAsync<IOException>(async () =>
    {
        await foreach (var _ in provider.ScanAsync(new IndexRoot(root), CancellationToken.None))
        {
        }
    });
}
```

- [ ] **Step 10: Run fallback root failure test and verify RED**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~FallbackIndexProviderTests.ScanAsyncThrowsForMissingRootInsteadOfReturningSuccessfulEmptyScan"
```

Expected: FAIL because the provider currently returns an empty successful scan.

- [ ] **Step 11: Implement fallback root failure**

At the start of `FallbackIndexProvider.ScanAsync`, before pushing the root, add:

```csharp
if (!Directory.Exists(root.Path))
{
    throw new DirectoryNotFoundException($"Index root does not exist: {root.Path}");
}

using (Directory.EnumerateFileSystemEntries(root.Path, "*", EnumerationOptions).GetEnumerator())
{
}
```

Wrap only this root probe in expected filesystem exception handling and rethrow as `IOException` for coordinator handling.

- [ ] **Step 12: Run indexing tests and commit**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Indexing"
```

Expected: PASS.

Commit:

```powershell
git add src\ListaryOpen.Core\Indexing\IndexProviderContracts.cs src\ListaryOpen.Infrastructure\Indexing\NtfsIndexProvider.cs src\ListaryOpen.Infrastructure\Indexing\IndexingCoordinator.cs src\ListaryOpen.Infrastructure\Indexing\FallbackIndexProvider.cs tests\ListaryOpen.Infrastructure.Tests\Indexing\ProviderSelectionTests.cs tests\ListaryOpen.Infrastructure.Tests\Indexing\IndexingCoordinatorTests.cs tests\ListaryOpen.Infrastructure.Tests\Indexing\FallbackIndexProviderTests.cs
git commit -m "Harden indexing prune safety"
```

## Task 2: P0 NTFS Fixed Full-Scan Watermark

**Files:**
- Modify: `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnJournalReader.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/NtfsUsnRecordProjectorTests.cs` or a new `NtfsUsnJournalReaderTests.cs`

- [ ] **Step 1: Write failing reader watermark test**

Create `tests/ListaryOpen.Infrastructure.Tests/Indexing/NtfsUsnJournalReaderTests.cs` with an internal test seam that calls a new internal pure method:

```csharp
using ListaryOpen.Indexer.Elevated.Ntfs;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class NtfsUsnJournalReaderTests
{
    [Fact]
    public void CreateFullScanEnumDataUsesSameHighUsnForBothPasses()
    {
        var journal = new NtfsNativeMethods.UsnJournalDataV0
        {
            NextUsn = 12345
        };

        var firstPass = NtfsUsnJournalReader.CreateFullScanEnumData(journal);
        var secondPass = NtfsUsnJournalReader.CreateFullScanEnumData(journal);

        Assert.Equal(12345, firstPass.HighUsn);
        Assert.Equal(firstPass.HighUsn, secondPass.HighUsn);
    }
}
```

- [ ] **Step 2: Run watermark test and verify RED**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~NtfsUsnJournalReaderTests.CreateFullScanEnumDataUsesSameHighUsnForBothPasses"
```

Expected: FAIL because `CreateFullScanEnumData` does not exist.

- [ ] **Step 3: Implement fixed watermark seam**

Add to `NtfsUsnJournalReader`:

```csharp
internal static NtfsNativeMethods.MftEnumDataV0 CreateFullScanEnumData(
    NtfsNativeMethods.UsnJournalDataV0 journalData)
{
    return new NtfsNativeMethods.MftEnumDataV0
    {
        StartFileReferenceNumber = 0,
        LowUsn = 0,
        HighUsn = journalData.NextUsn
    };
}
```

Change `EnumerateVolumeAsync` to query the journal once and pass the same `journalData` into both `ReadDirectoryEntries` and `EnumerateEntries`.

- [ ] **Step 4: Run NTFS indexing tests and commit**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Ntfs"
```

Expected: PASS.

Commit:

```powershell
git add src\ListaryOpen.Indexer.Elevated\Ntfs\NtfsUsnJournalReader.cs tests\ListaryOpen.Infrastructure.Tests\Indexing\NtfsUsnJournalReaderTests.cs
git commit -m "Use fixed NTFS scan watermark"
```

## Task 3: Query Syntax V1 And Search Responsiveness

**Files:**
- Create: `src/ListaryOpen.Core/Search/ParsedSearchQuery.cs`
- Modify: `src/ListaryOpen.Core/Search/SearchQuery.cs`
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Test: `tests/ListaryOpen.Core.Tests/Search/SearchQueryTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] **Step 1: Write failing parser tests**

Add to `SearchQueryTests`:

```csharp
[Fact]
public void ParsedQueryReadsExtensionPathPhraseAndExclusions()
{
    var query = new SearchQuery("""ext:pdf path:src "search panel" !archive""", SearchMode.FilesAndFolders);

    Assert.Equal(new[] { "search panel" }, query.Parsed.Phrases);
    Assert.Equal(new[] { "src" }, query.Parsed.PathTerms);
    Assert.Equal(new[] { "pdf" }, query.Parsed.Extensions);
    Assert.Equal(new[] { "archive" }, query.Parsed.ExcludedTerms);
}

[Theory]
[InlineData("folder: report", SearchMode.FoldersOnly)]
[InlineData("file: report", SearchMode.FilesAndFolders)]
public void ParsedQueryReadsTypeFilters(string text, SearchMode expectedMode)
{
    var query = new SearchQuery(text, SearchMode.FilesAndFolders);

    Assert.Equal(expectedMode, query.EffectiveMode);
}
```

- [ ] **Step 2: Run parser tests and verify RED**

Run:

```powershell
dotnet test tests\ListaryOpen.Core.Tests\ListaryOpen.Core.Tests.csproj --filter "FullyQualifiedName~SearchQueryTests"
```

Expected: FAIL because `Parsed` and `EffectiveMode` do not exist.

- [ ] **Step 3: Implement parser minimally**

Create `ParsedSearchQuery` with immutable arrays:

```csharp
public sealed record ParsedSearchQuery(
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> Phrases,
    IReadOnlyList<string> PathTerms,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> ExcludedTerms,
    IReadOnlyList<string> ExcludedExtensions,
    SearchMode? ModeOverride);
```

Add a simple parser that handles quotes, `!`, `ext:`, `path:`, `file:`, and `folder:`. Keep malformed quotes as normal text by treating the remaining text as one token.

Add to `SearchQuery`:

```csharp
public ParsedSearchQuery Parsed { get; }

public SearchMode EffectiveMode => Parsed.ModeOverride ?? Mode;
```

Set `Parsed = ParsedSearchQuery.Parse(Text)` in the constructor.

- [ ] **Step 4: Run core search tests and verify GREEN**

Run:

```powershell
dotnet test tests\ListaryOpen.Core.Tests\ListaryOpen.Core.Tests.csproj --filter "FullyQualifiedName~Search"
```

Expected: PASS.

- [ ] **Step 5: Write failing SQLite syntax tests**

Add to `SqliteSearchIndexTests`:

```csharp
[Fact]
public async Task SearchAppliesExtensionPathPhraseAndExclusionFilters()
{
    var dbPath = CreateTempDbPath();

    try
    {
        await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
        await index.UpsertManyAsync(new[]
        {
            FileRecord.Create("C:\\src\\Search Panel.pdf", false, 10, DateTimeOffset.UtcNow),
            FileRecord.Create("C:\\src\\Search Panel archive.pdf", false, 10, DateTimeOffset.UtcNow),
            FileRecord.Create("C:\\docs\\Search Panel.pdf", false, 10, DateTimeOffset.UtcNow),
            FileRecord.Create("C:\\src\\Search Panel.txt", false, 10, DateTimeOffset.UtcNow)
        }, CancellationToken.None);

        var results = await index.SearchAsync(
            new SearchQuery("""ext:pdf path:src "Search Panel" !archive""", SearchMode.FilesAndFolders),
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("C:\\src\\Search Panel.pdf", result.Record.FullPath);
    }
    finally
    {
        DeleteIfExists(dbPath);
    }
}

[Fact]
public async Task SearchFolderFilterReturnsOnlyFolders()
{
    var dbPath = CreateTempDbPath();

    try
    {
        await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
        await index.UpsertManyAsync(new[]
        {
            FileRecord.Create("C:\\Reports", true, 0, DateTimeOffset.UtcNow),
            FileRecord.Create("C:\\Reports.txt", false, 10, DateTimeOffset.UtcNow)
        }, CancellationToken.None);

        var results = await index.SearchAsync(new SearchQuery("folder: report", SearchMode.FilesAndFolders), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Record.IsDirectory);
    }
    finally
    {
        DeleteIfExists(dbPath);
    }
}
```

- [ ] **Step 6: Run SQLite syntax tests and verify RED**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SqliteSearchIndexTests.SearchAppliesExtensionPathPhraseAndExclusionFilters|FullyQualifiedName~SqliteSearchIndexTests.SearchFolderFilterReturnsOnlyFolders"
```

Expected: FAIL because SQLite does not apply parsed filters.

- [ ] **Step 7: Implement SQLite parsed filtering**

Keep the candidate SQL simple:

- Use `query.EffectiveMode` in directory filters.
- Add SQL predicates for `ext:` using `name like '%.pdf'` and exact case-insensitive suffix checks.
- Add SQL predicates for `path:` and phrases using `search_text like`.
- Add exclusion predicates using `not like`.
- Keep all filter values parameterized.

After reading candidates, call `ResultRanker.Rank` with a query whose text is only searchable bare terms and phrases. If there are no positive terms, use path/phrase terms as search terms so filtered queries still rank.

- [ ] **Step 8: Move ranking outside connection gate**

Refactor `SearchAsync` to:

```csharp
IReadOnlyList<FileRecord> candidates;
IReadOnlyList<UsageRecord> usage;
await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
try
{
    ThrowIfDisposed();
    candidates = await ReadCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
    usage = await ReadUsageAsync(candidates.Select(record => record.PathKey), cancellationToken).ConfigureAwait(false);
}
finally
{
    _connectionGate.Release();
}

cancellationToken.ThrowIfCancellationRequested();
return ResultRanker.Rank(query, candidates, usage, Array.Empty<string>());
```

- [ ] **Step 9: Add one-character fuzzy guard**

In `ReadCandidatesAsync`, skip `AddFuzzyCandidatesAsync` and `AddCombinedScoreCandidatesAsync` when the searchable text length is one. Keep exact, usage, and fallback candidates.

- [ ] **Step 10: Run search tests and commit**

Run:

```powershell
dotnet test tests\ListaryOpen.Core.Tests\ListaryOpen.Core.Tests.csproj --filter "FullyQualifiedName~Search"
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SqliteSearchIndexTests"
```

Expected: PASS.

Commit:

```powershell
git add src\ListaryOpen.Core\Search\ParsedSearchQuery.cs src\ListaryOpen.Core\Search\SearchQuery.cs src\ListaryOpen.Infrastructure\Search\SqliteSearchIndex.cs tests\ListaryOpen.Core.Tests\Search\SearchQueryTests.cs tests\ListaryOpen.Infrastructure.Tests\Search\SqliteSearchIndexTests.cs
git commit -m "Add search syntax v1"
```

## Task 4: P1 USN Checkpoint And Catch-Up Core

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Indexing/Ntfs/UsnJournalCheckpoint.cs`
- Create: `src/ListaryOpen.Infrastructure/Indexing/Ntfs/UsnJournalCatchUpPlanner.cs`
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Modify: `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsNativeMethods.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/UsnJournalCatchUpPlannerTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] **Step 1: Write failing checkpoint storage test**

Add to `SqliteSearchIndexTests`:

```csharp
[Fact]
public async Task VolumeCheckpointRoundTripsUnsignedValuesAsText()
{
    var dbPath = CreateTempDbPath();

    try
    {
        await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
        var checkpoint = new UsnJournalCheckpoint(
            "C:\\",
            "NTFS",
            ulong.MaxValue,
            long.MaxValue,
            SqliteSearchIndex.CurrentIndexContentVersion,
            new DateTimeOffset(2026, 7, 9, 1, 2, 3, TimeSpan.Zero));

        await index.SaveVolumeCheckpointAsync(checkpoint, CancellationToken.None);
        var roundTripped = await index.ReadVolumeCheckpointAsync("C:\\", CancellationToken.None);

        Assert.Equal(checkpoint, roundTripped);
    }
    finally
    {
        DeleteIfExists(dbPath);
    }
}
```

- [ ] **Step 2: Run checkpoint storage test and verify RED**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SqliteSearchIndexTests.VolumeCheckpointRoundTripsUnsignedValuesAsText"
```

Expected: FAIL because checkpoint APIs do not exist.

- [ ] **Step 3: Implement checkpoint record and SQLite storage**

Create:

```csharp
public sealed record UsnJournalCheckpoint(
    string VolumeRoot,
    string FileSystemName,
    ulong UsnJournalId,
    long NextUsn,
    long RulesVersion,
    DateTimeOffset LastFullScanAt);
```

Add `volume_checkpoints` schema in `CreateSchemaAsync`.

Add public/internal methods on `SqliteSearchIndex`:

```csharp
internal Task SaveVolumeCheckpointAsync(UsnJournalCheckpoint checkpoint, CancellationToken cancellationToken)
internal Task<UsnJournalCheckpoint?> ReadVolumeCheckpointAsync(string volumeRoot, CancellationToken cancellationToken)
```

Store `UsnJournalId` and `NextUsn` as invariant-culture text.

- [ ] **Step 4: Write failing catch-up planner tests**

Create `UsnJournalCatchUpPlannerTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class UsnJournalCatchUpPlannerTests
{
    [Fact]
    public void PlanRequestsFullRescanWhenJournalIdChanges()
    {
        var checkpoint = new UsnJournalCheckpoint("C:\\", "NTFS", 1, 100, 2, DateTimeOffset.UtcNow);
        var journal = new UsnJournalState(2, lowestValidUsn: 50, nextUsn: 200);

        var plan = UsnJournalCatchUpPlanner.Plan(checkpoint, journal);

        Assert.Equal(UsnCatchUpAction.FullRescan, plan.Action);
    }

    [Fact]
    public void PlanRequestsFullRescanWhenCheckpointIsBeforeLowestValidUsn()
    {
        var checkpoint = new UsnJournalCheckpoint("C:\\", "NTFS", 1, 40, 2, DateTimeOffset.UtcNow);
        var journal = new UsnJournalState(1, lowestValidUsn: 50, nextUsn: 200);

        var plan = UsnJournalCatchUpPlanner.Plan(checkpoint, journal);

        Assert.Equal(UsnCatchUpAction.FullRescan, plan.Action);
    }

    [Fact]
    public void PlanReadsJournalRangeWhenCheckpointIsValid()
    {
        var checkpoint = new UsnJournalCheckpoint("C:\\", "NTFS", 1, 100, 2, DateTimeOffset.UtcNow);
        var journal = new UsnJournalState(1, lowestValidUsn: 50, nextUsn: 200);

        var plan = UsnJournalCatchUpPlanner.Plan(checkpoint, journal);

        Assert.Equal(UsnCatchUpAction.ReadJournal, plan.Action);
        Assert.Equal(100, plan.StartUsn);
        Assert.Equal(200, plan.EndUsn);
    }
}
```

- [ ] **Step 5: Run planner tests and verify RED**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~UsnJournalCatchUpPlannerTests"
```

Expected: FAIL because planner types do not exist.

- [ ] **Step 6: Implement catch-up planner core**

Create minimal types:

```csharp
public sealed record UsnJournalState(ulong UsnJournalId, long LowestValidUsn, long NextUsn);

public enum UsnCatchUpAction
{
    None,
    ReadJournal,
    FullRescan
}

public sealed record UsnCatchUpPlan(UsnCatchUpAction Action, long StartUsn, long EndUsn);
```

Implement `Plan`:

```csharp
public static UsnCatchUpPlan Plan(UsnJournalCheckpoint? checkpoint, UsnJournalState journal)
{
    if (checkpoint is null ||
        checkpoint.UsnJournalId != journal.UsnJournalId ||
        checkpoint.NextUsn < journal.LowestValidUsn)
    {
        return new UsnCatchUpPlan(UsnCatchUpAction.FullRescan, 0, journal.NextUsn);
    }

    return checkpoint.NextUsn >= journal.NextUsn
        ? new UsnCatchUpPlan(UsnCatchUpAction.None, journal.NextUsn, journal.NextUsn)
        : new UsnCatchUpPlan(UsnCatchUpAction.ReadJournal, checkpoint.NextUsn, journal.NextUsn);
}
```

- [ ] **Step 7: Add native constants and structs**

Add to `NtfsNativeMethods`:

```csharp
internal const uint FsctlReadUsnJournal = 0x000900bb;

[StructLayout(LayoutKind.Sequential)]
internal struct ReadUsnJournalDataV0
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalId;
}
```

Do not wire a persistent watcher in this task.

- [ ] **Step 8: Run checkpoint/planner tests and commit**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~UsnJournalCatchUpPlannerTests|FullyQualifiedName~SqliteSearchIndexTests.VolumeCheckpointRoundTripsUnsignedValuesAsText"
```

Expected: PASS.

Commit:

```powershell
git add src\ListaryOpen.Infrastructure\Indexing\Ntfs\UsnJournalCheckpoint.cs src\ListaryOpen.Infrastructure\Indexing\Ntfs\UsnJournalCatchUpPlanner.cs src\ListaryOpen.Infrastructure\Search\SqliteSearchIndex.cs src\ListaryOpen.Indexer.Elevated\Ntfs\NtfsNativeMethods.cs tests\ListaryOpen.Infrastructure.Tests\Indexing\UsnJournalCatchUpPlannerTests.cs tests\ListaryOpen.Infrastructure.Tests\Search\SqliteSearchIndexTests.cs
git commit -m "Add USN checkpoint planning"
```

## Task 5: Full Verification And Review

**Files:**
- No expected production edits unless review finds defects.

- [ ] **Step 1: Run full automated tests**

Run:

```powershell
dotnet test ListaryOpen.sln
```

Expected: PASS.

- [ ] **Step 2: Dispatch spec compliance review subagents**

Dispatch at least three independent reviewers:

- Index correctness reviewer: checks P0/P1 indexing requirements against code.
- Search syntax/performance reviewer: checks parsed query behavior and candidate/ranking changes.
- Platform/volume reviewer: checks network drive, fallback failure, and native USN API changes.

Each reviewer returns findings with file/line references and whether the spec item is satisfied.

- [ ] **Step 3: Fix review findings with TDD**

For each confirmed finding:

1. Add or adjust a failing test that proves the defect.
2. Run the focused test and confirm RED.
3. Apply the smallest fix.
4. Run the focused test and confirm GREEN.

- [ ] **Step 4: Dispatch final code quality review**

Dispatch one final reviewer over the full diff. Fix any correctness or maintainability findings with tests.

- [ ] **Step 5: Run final verification**

Run:

```powershell
dotnet test ListaryOpen.sln
git status --short
```

Expected:

- All tests pass.
- Only intentional files are modified or committed.
- Existing unrelated untracked `.github/` and `README.md` remain untouched unless the user separately asks to handle them.

## Self-Review

- Spec coverage: P0 and P1 requirements are represented by Tasks 1 through 4. P2 is explicitly out of scope by the spec and not planned.
- Placeholder scan: no unfinished markers or unbounded testing steps remain.
- Type consistency: `VolumeInfo.DriveType`, `ParsedSearchQuery`, `UsnJournalCheckpoint`, and `UsnJournalCatchUpPlanner` are introduced before later tasks use them.
