# Performance Tuning and Quick Switch Window Expansion Design

Date: 2026-07-03

## Goal

Improve search/index runtime behavior and expand Quick Switch from one remembered Explorer folder to multiple folder candidates from active file-management windows.

## Current State

The app already indexes file-system records into SQLite and uses `Ctrl+G` to open folder-only search for dialog jumping. The current Quick Switch path has two limits:

- `ExplorerTracker` stores only one `LastFolder`.
- `SearchPanelViewModel.ActivateFolderSearchAsync` accepts only one tracked folder to pin above indexed folder results.

The current search path also has performance limits:

- SQLite candidate reads use `%like%` and ordered-like scans without explicit candidate caps.
- `SearchAsync` reads all `usage` rows for every query, even when only a small candidate set will be ranked.
- There is no performance regression test that catches accidental full-candidate ranking on larger indexes.

## Scope

This repair covers:

- SQLite search candidate limiting and targeted usage lookup.
- Schema indexes that help equality, folder filtering, and deterministic ordered reads.
- Quick Switch folder candidates from multiple Explorer windows.
- A provider abstraction that can later add Total Commander, Directory Opus, XYplorer, or other file manager windows without changing the search panel.
- Automated tests and smoke/manual checklist updates.

This repair does not claim full UI Automation support for every non-standard Open/Save dialog. Standard dialog jumping remains handled through `DialogBridge` and `WindowsDialogAutomation`.

## Performance Design

`SqliteSearchIndex` will keep ranking in managed code, but it will reduce how much data reaches the ranker.

- Add candidate limits derived from `SearchQuery.Limit`.
- Exact/contains candidates are ordered by likely quality first and limited.
- Fuzzy candidates are ordered by shorter names first, then name, then path, and limited. This preserves useful abbreviation matches without reading every weak match first.
- Fallback candidates remain small and deterministic.
- `ReadUsageAsync` accepts candidate path keys and reads only matching `usage` rows, using chunked `IN` queries to stay below SQLite parameter limits.
- Schema creation/migration adds indexes:
  - `ix_files_name`
  - `ix_files_is_directory_name`
  - `ix_files_search_text`
  - `ix_usage_path_key`

Performance is verified with deterministic large-index tests. The tests should prove that:

- Exact matches are preserved beyond large filler sets.
- High-quality abbreviation matches survive candidate limiting.
- Usage lookup is bounded to candidate path keys.
- A 50k-row search finishes under a conservative threshold on the test machine. The threshold is intentionally loose enough for CI variance, but tight enough to catch obvious unbounded scans.

## Quick Switch Design

Add a model:

```csharp
public sealed record QuickSwitchFolderCandidate(
    string FolderPath,
    string SourceName,
    IntPtr WindowHandle,
    bool IsForeground);
```

Add a provider interface:

```csharp
public interface IQuickSwitchWindowProvider
{
    IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates();
}
```

The first production provider is Explorer-backed:

- Enumerates Shell.Application windows through the existing shell seam.
- Converts each existing folder into a `QuickSwitchFolderCandidate`.
- Marks the current foreground Explorer window as foreground when handles match.
- Deduplicates folders by normalized path key.
- Orders foreground candidate first, then remaining candidates by source/window order.

`ExplorerTracker` either becomes this provider or delegates to it. It should still expose a compatibility `LastFolder` property for existing app logic and tests, but the new path uses the candidate list.

`App.HandleDialogHotkey` changes from a single folder lookup to a candidate lookup:

- Observe current Explorer/file-manager windows.
- Pass candidates into folder-only search.
- The search panel pins every valid candidate above SQLite folder results.

`SearchPanelViewModel` changes:

- `ActivateFolderSearchAsync(string? trackedFolder)` remains as a compatibility overload.
- Add `ActivateFolderSearchAsync(IReadOnlyList<QuickSwitchFolderCandidate> candidates)`.
- It creates pinned folder search results for every existing candidate.
- It deduplicates pinned candidates against SQLite results.
- It keeps the first candidate selected by default.

## Future Providers

The provider abstraction is intentionally small. Future adapters can implement the interface by using window class/process inspection:

- Total Commander: active pane path extraction.
- Directory Opus: active lister path extraction.
- XYplorer: active tab/path extraction.

Those adapters are outside this repair unless a reliable local detection method already exists. The current deliverable is the extensible provider architecture plus Explorer multi-window support.

## Error Handling

- Inaccessible or missing folders are skipped.
- Shell COM failures leave existing candidates unchanged and do not crash hotkey handling.
- Duplicate folders are collapsed case-insensitively through normalized path keys.
- If no Quick Switch candidates are available, `Ctrl+G` still opens folder-only search normally.

## Testing

Automated tests cover:

- Multiple Explorer candidates are returned and foreground is first.
- Missing/inaccessible candidate folders are skipped.
- Duplicate candidate folders are collapsed.
- App hotkey routing passes multiple candidates to folder search.
- Search panel pins multiple Quick Switch folders and keeps them above index results.
- SQLite candidate limiting preserves exact, pinyin, and abbreviation matches.
- Usage reads are candidate-scoped.
- Full solution tests and build pass.

Manual verification records:

- App starts and indexes.
- Quick Switch with multiple Explorer windows should be manually verified on a real desktop.
- Third-party file-manager providers are architecture-ready but not claimed as manually verified until adapters exist.

## Acceptance Criteria

- `Ctrl+G` can present more than one Explorer folder candidate for dialog jumping.
- The first candidate reflects the foreground Explorer window when available.
- Search remains correct with large filler data.
- Tests include performance-oriented regression coverage.
- `dotnet test ListaryOpen.sln --no-restore` passes.
- `dotnet build ListaryOpen.sln --no-restore` passes with no warnings or errors.
