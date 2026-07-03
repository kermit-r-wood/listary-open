# ListaryOpen Full Experience Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the final-review MVP gaps so ListaryOpen indexes real files, returns actionable search results, jumps standard dialogs to selected folders, tracks Explorer context, reports failures visibly, and enforces a single instance.

**Architecture:** Keep the existing WPF app, SQLite index, provider boundary, and DialogBridge. Add small focused services for helper-output parsing, indexing coordination, result activation, Explorer observation, and single-instance ownership; wire them from App startup without running the UI elevated.

**Tech Stack:** .NET 8, WPF, xUnit, SQLite via `Microsoft.Data.Sqlite`, Win32 hotkeys/window handles, Shell COM, Windows UI Automation, NTFS helper process.

---

## Task 1: Parse Elevated Helper Output In The Client

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Indexing/ElevatedIndexerClient.cs`
- Modify: `tests/ListaryOpen.Infrastructure.Tests/Indexing/ElevatedIndexerClientTests.cs`

- [ ] **Step 1: Write failing stdout parsing test**

Add a test that uses the existing fake `IElevatedIndexerProcess` seam to return two newline-delimited JSON records:

```json
{"fullPath":"C:\\Docs\\Invoice.xlsx","isDirectory":false,"sizeBytes":12,"lastWriteTime":"2026-07-03T00:00:00+00:00"}
{"fullPath":"C:\\Docs\\Projects","isDirectory":true,"sizeBytes":0,"lastWriteTime":"2026-07-03T00:01:00+00:00"}
```

Assert `ScanNtfsAsync(new IndexRoot("C:\\Docs"), token)` yields two `FileRecord`s with matching path, directory flag, size, and timestamp.

- [ ] **Step 2: Write failing malformed-output test**

Add a test where stdout contains `{not-json}` and the process exits `0`. Assert enumeration throws `InvalidDataException` or a narrow helper-output exception. This proves malformed helper data is not treated as successful empty indexing.

- [ ] **Step 3: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~ElevatedIndexerClientTests --no-restore
```

Expected: new tests fail because `ScanNtfsAsync` still discards stdout.

- [ ] **Step 4: Implement parser**

Update `ElevatedIndexerClient` so `RunHelperAsync` returns parsed records or exposes stdout to `ScanNtfsAsync`.

Implementation constraints:

- Use `System.Text.Json`.
- Parse one JSON object per non-empty stdout line.
- DTO properties: `fullPath`, `isDirectory`, `sizeBytes`, `lastWriteTime`.
- Convert each DTO through `FileRecord.Create(...)`.
- Preserve cancellation cleanup behavior.
- If process exits non-zero, trace stderr and return no records.
- If JSON or record conversion fails, throw `InvalidDataException` with the line number.

- [ ] **Step 5: Verify green**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~ElevatedIndexerClientTests --no-restore
dotnet test ListaryOpen.sln --no-restore
```

- [ ] **Step 6: Commit**

```powershell
git add src/ListaryOpen.Infrastructure tests/ListaryOpen.Infrastructure.Tests
git commit -m "fix: parse elevated indexer output"
```

## Task 2: Add Indexing Coordinator And SQLite Upsert Flow

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Indexing/IndexingCoordinator.cs`
- Create: `tests/ListaryOpen.Infrastructure.Tests/Indexing/IndexingCoordinatorTests.cs`
- Modify: `src/ListaryOpen.Infrastructure/Indexing/VolumeIndexer.cs` if a safe fallback-selection helper is needed

- [ ] **Step 1: Write fallback upsert test**

Create a temp folder with one file and one subfolder. Open a temp `SqliteSearchIndex`. Use an `IndexingCoordinator` configured with:

- an NTFS provider that cannot index or returns no records,
- a real `FallbackIndexProvider`,
- a volume resolver returning a ready non-NTFS volume or an NTFS provider failure.

Run indexing for that root, then query SQLite for the file/folder names. Assert results include both records.

- [ ] **Step 2: Write NTFS-empty-falls-back test**

Create fake providers:

- primary provider name `NTFS`, can index, yields no records,
- fallback provider yields one known record.

Run coordinator and assert the fallback record is upserted and status indicates fallback was used.

- [ ] **Step 3: Write status failure test**

Use a missing root or provider that throws. Assert the coordinator reports `Failed` status and does not crash the caller.

- [ ] **Step 4: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~IndexingCoordinatorTests --no-restore
```

Expected: fails because `IndexingCoordinator` does not exist.

- [ ] **Step 5: Implement coordinator**

Create:

```csharp
public enum IndexingRunState { Idle, Indexing, Completed, Failed }
public sealed record IndexingStatus(IndexingRunState State, string Message, int IndexedCount);
public sealed class IndexingCoordinator
{
    public event EventHandler<IndexingStatus>? StatusChanged;
    public Task IndexRootsAsync(IReadOnlyList<IndexRoot> roots, CancellationToken cancellationToken);
}
```

Constructor dependencies:

- `SqliteSearchIndex index`
- `VolumeIndexer volumeIndexer`
- `FallbackIndexProvider fallbackProvider`
- optional `Func<IndexRoot, VolumeInfo>` volume resolver for tests
- optional batch size, default around `500`

Behavior:

- Skip missing roots with failed status.
- Select provider for each root.
- Scan selected provider.
- If selected provider is NTFS and scan throws or yields zero records, scan fallback.
- Batch upsert with `SqliteSearchIndex.UpsertManyAsync`.
- Raise status changes on start, completion, and failures.
- Cancellation propagates as cancellation.

- [ ] **Step 6: Verify green**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~IndexingCoordinatorTests --no-restore
dotnet test ListaryOpen.sln --no-restore
```

- [ ] **Step 7: Commit**

```powershell
git add src/ListaryOpen.Infrastructure tests/ListaryOpen.Infrastructure.Tests
git commit -m "feat: index roots into sqlite"
```

## Task 3: Wire Background Indexing And Status Into App Startup

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Modify: `src/ListaryOpen.App/MainWindow.xaml`
- Modify: `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/ListaryOpen.App/Tray/TrayController.cs`
- Test: existing app/infrastructure test project if focused non-WPF seams are available

- [ ] **Step 1: Add status to settings view model**

Expose a string property such as `IndexingStatusText`, initially `Indexing: idle`.

- [ ] **Step 2: Wire coordinator in App startup**

In `App`:

- add `CancellationTokenSource _shutdownCancellation`,
- create `IndexingCoordinator` after providers and SQLite index,
- subscribe to `StatusChanged` and update `SettingsViewModel` through dispatcher,
- start indexing after settings window is shown:

```csharp
_ = RunInitialIndexAsync(_shutdownCancellation.Token);
```

Use `AppSettings.Defaults().IndexedRoots` as roots for this repair.

- [ ] **Step 3: Add tray reindex command**

Add `Reindex` to tray menu and call an app-provided callback. Keep existing `Open Settings` and `Exit`.

- [ ] **Step 4: Add shutdown cancellation**

Cancel the coordinator in `OnExit` before disposing SQLite.

- [ ] **Step 5: Verify**

Run:

```powershell
dotnet test ListaryOpen.sln --no-restore
dotnet build ListaryOpen.sln --no-restore
```

Manual smoke:

```powershell
dotnet run --project src/ListaryOpen.App/ListaryOpen.App.csproj
```

Expected: app starts, settings displays indexing status, and the SQLite DB receives records after background indexing.

- [ ] **Step 6: Commit**

```powershell
git add src/ListaryOpen.App
git commit -m "feat: start background indexing"
```

## Task 4: Add Search Result Activation And Visible Status

**Files:**
- Modify: `src/ListaryOpen.App/SearchPanel.xaml`
- Modify: `src/ListaryOpen.App/SearchPanel.xaml.cs`
- Modify: `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`
- Create: `src/ListaryOpen.App/Search/SearchResultActivator.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs`

- [ ] **Step 1: Write activation tests**

Add tests for `SearchPanelViewModel`:

- when a selected file result activates in normal mode, the injected open callback receives the full path,
- `Ctrl+Enter` style reveal callback receives full path,
- copy callback receives full path and sets status,
- missing selection sets a status message and does not throw.

- [ ] **Step 2: Verify red**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~SearchPanelViewModelTests --no-restore
```

- [ ] **Step 3: Implement view model activation API**

Add:

- `SearchResult? SelectedResult`
- `string StatusText`
- `Task ActivateSelectedAsync()`
- `Task RevealSelectedAsync()`
- `void CopySelectedPath()`

Inject an activation service/callback object for tests.

- [ ] **Step 4: Implement production activator**

`SearchResultActivator` should:

- open path with `ProcessStartInfo { FileName = path, UseShellExecute = true }`,
- reveal files with `explorer.exe /select,"path"`,
- reveal folders by opening their parent or the folder itself as appropriate,
- copy path through WPF Clipboard from UI thread.

Expected filesystem/process exceptions become status messages.

- [ ] **Step 5: Wire XAML events**

Update SearchPanel:

- bind `ListBox.SelectedItem`,
- handle `Enter`, `Ctrl+Enter`, `Ctrl+C`,
- handle double-click,
- show `StatusText`.

- [ ] **Step 6: Verify**

Run:

```powershell
dotnet test ListaryOpen.sln --no-restore
dotnet build ListaryOpen.sln --no-restore
```

- [ ] **Step 7: Commit**

```powershell
git add src/ListaryOpen.App tests/ListaryOpen.Infrastructure.Tests
git commit -m "feat: activate search results"
```

## Task 5: Complete Folder-Mode Dialog Jump Flow

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Modify: `src/ListaryOpen.App/SearchPanel.xaml.cs`
- Modify: `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs`

- [ ] **Step 1: Write dialog activation tests**

Add tests that:

- folder-mode activation calls injected folder callback with selected directory path,
- callback success sets status `Dialog folder changed.`,
- permission-limited/unsupported/failed callback statuses are surfaced visibly,
- non-directory selection is ignored in folder mode.

- [ ] **Step 2: Verify red**

Run the focused ViewModel tests and confirm failure.

- [ ] **Step 3: Implement folder activation callback**

Add an injected callback:

```csharp
Func<string, CancellationToken, Task<DialogJumpResult>>
```

or a small interface so App can supply `DialogBridge.JumpToFolderAsync`.

Folder mode `ActivateSelectedAsync` should call it for directories.

- [ ] **Step 4: Wire App dialog mode**

In `App.HandleDialogHotkey`:

- call `_explorerTracker.ObserveForegroundExplorerFolder()` before reading `LastFolder`,
- open folder-focused search,
- do not call `DialogBridge` until the user activates a folder result.

- [ ] **Step 5: Verify**

Run:

```powershell
dotnet test ListaryOpen.sln --no-restore
dotnet build ListaryOpen.sln --no-restore
```

- [ ] **Step 6: Commit**

```powershell
git add src/ListaryOpen.App tests/ListaryOpen.Infrastructure.Tests
git commit -m "feat: jump dialogs from folder search"
```

## Task 6: Implement Runtime Explorer Folder Tracking

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Windows/ExplorerTracker.cs`
- Modify: `src/ListaryOpen.Infrastructure/Windows/NativeMethods.cs` if needed
- Test: `tests/ListaryOpen.Infrastructure.Tests/Windows/ExplorerTrackerTests.cs`

- [ ] **Step 1: Write tests with seams**

Add tests for:

- foreground Explorer window maps to an existing folder and updates `LastFolder`,
- non-Explorer foreground window leaves `LastFolder` unchanged,
- Shell COM failure leaves `LastFolder` unchanged,
- test observer still preserves exact observed path for existing folder.

- [ ] **Step 2: Verify red**

Run focused ExplorerTracker tests.

- [ ] **Step 3: Implement runtime observation**

Add:

```csharp
public void ObserveForegroundExplorerFolder()
```

Implementation:

- use `NativeMethods.GetForegroundWindow()` and `GetWindowThreadProcessId`,
- query Shell.Application Windows collection,
- match Explorer window by HWND,
- read `Document.Folder.Self.Path`,
- if `Directory.Exists(path)`, store it.

Use internal interfaces/delegates for tests. Catch expected COM/Win32/path exceptions and fail quietly.

- [ ] **Step 4: Verify**

Run:

```powershell
dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~ExplorerTrackerTests --no-restore
dotnet test ListaryOpen.sln --no-restore
```

- [ ] **Step 5: Commit**

```powershell
git add src/ListaryOpen.Infrastructure tests/ListaryOpen.Infrastructure.Tests
git commit -m "feat: track foreground explorer folder"
```

## Task 7: Enforce Single Instance

**Files:**
- Create: `src/ListaryOpen.App/SingleInstanceGuard.cs`
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/SingleInstanceGuardTests.cs`

- [ ] **Step 1: Write guard tests**

Using an injectable mutex seam, test:

- first instance reports ownership,
- second instance reports already running,
- dispose releases owned mutex,
- dispose is idempotent.

- [ ] **Step 2: Verify red**

Run focused tests.

- [ ] **Step 3: Implement guard**

Create guard with a stable name such as:

```text
Local\ListaryOpen.SingleInstance
```

App startup should acquire it before opening SQLite/hotkeys. If not acquired, show a short message and shutdown without registering hotkeys.

- [ ] **Step 4: Verify**

Run:

```powershell
dotnet test ListaryOpen.sln --no-restore
dotnet build ListaryOpen.sln --no-restore
```

- [ ] **Step 5: Commit**

```powershell
git add src/ListaryOpen.App tests/ListaryOpen.Infrastructure.Tests
git commit -m "feat: enforce single app instance"
```

## Task 8: Final Verification And Manual Acceptance

**Files:**
- Modify: `docs/manual-test-checklist.md`

- [ ] **Step 1: Run full tests**

```powershell
dotnet test ListaryOpen.sln
dotnet build ListaryOpen.sln --no-restore
```

- [ ] **Step 2: Run app**

```powershell
dotnet run --project src/ListaryOpen.App/ListaryOpen.App.csproj
```

Verify:

- app starts,
- settings/tray appear,
- indexing status reaches completed or reports fallback,
- SQLite contains indexed rows.

- [ ] **Step 3: Manual non-elevated workflow**

Verify:

- `Ctrl+Space` search returns a known file/folder,
- `Enter` opens selected result,
- `Ctrl+Enter` reveals selected result,
- `Ctrl+C` copies path,
- standard Save/Open dialog plus `Ctrl+G` lets user select a folder and changes dialog location.

- [ ] **Step 4: Manual elevated/unsupported workflow**

Record results for:

- elevated dialog permission-limited,
- unsupported dialog unsupported,
- second instance exits cleanly.

- [ ] **Step 5: Update checklist and commit**

```powershell
git add docs/manual-test-checklist.md
git commit -m "test: verify full listary workflows"
```
