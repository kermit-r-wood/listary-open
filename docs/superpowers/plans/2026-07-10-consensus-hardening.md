# Consensus Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the agreed correctness, safety, responsiveness, and keyboard-workflow fixes to search and Quick Switch without adding speculative infrastructure.

**Architecture:** Keep the existing WPF, SQLite, provider, and hook-host boundaries. Strengthen the existing contracts at their current ownership points: indexing completeness in `IndexingCoordinator`, usage and candidate selection in `SqliteSearchIndex`, dialog identity in the existing bridge, and hook authorization/health in the host bridge. Prefer conservative failure over unsafe prune or false success.

**Tech Stack:** C#/.NET 8, WPF, Microsoft.Data.Sqlite, xUnit, Rust/windows-sys, Win32 named pipes and hooks.

---

## Working-Tree Constraint

The current `hook-quick-switch` checkout contains intentional uncommitted Blender adapter work. Work in place, do not reset or commit, and do not rewrite unrelated files. Each implementation task owns the files listed below.

### Task 1: USN Range Guards

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Indexing/Ntfs/UsnJournalCatchUpPlanner.cs`
- Modify: `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnJournalReader.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/UsnJournalCatchUpPlannerTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/NtfsUsnJournalReaderTests.cs`

- [ ] **Step 1: Add failing future-checkpoint and busy-volume tests**

```csharp
[Fact]
public void PlanRequestsFullRescanWhenCheckpointIsAheadOfJournalTip()
{
    var checkpoint = new UsnJournalCheckpoint("C:\\", "NTFS", 1, 201, 2, DateTimeOffset.UtcNow);
    var journal = new UsnJournalState(1, LowestValidUsn: 50, NextUsn: 200);

    Assert.Equal(UsnCatchUpAction.FullRescan,
        UsnJournalCatchUpPlanner.Plan(checkpoint, journal, currentRulesVersion: 2).Action);
}

[Fact]
public void JournalSnapshotMayAdvancePastRequestedEnd()
{
    var journal = new NtfsNativeMethods.UsnJournalDataV0
    {
        UsnJournalId = 9,
        LowestValidUsn = 50,
        NextUsn = 201
    };

    Assert.False(NtfsUsnJournalReader.RequiresFullRescanForJournalSnapshot(
        journal, expectedUsnJournalId: 9, endUsn: 200));
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --filter "FullyQualifiedName~UsnJournalCatchUpPlannerTests|FullyQualifiedName~NtfsUsnJournalReaderTests"
```

Expected: the future checkpoint is reported as `None`, and an advanced live journal is reported as requiring a full rescan.

- [ ] **Step 3: Implement the two minimal comparisons**

Use `>` for corrupt/future checkpoints, `==` for no work, and allow a live journal whose `NextUsn` is at or beyond the planned end while still rejecting a changed journal id or unavailable range.

- [ ] **Step 4: Re-run the focused tests and verify GREEN**

### Task 2: Conservative Scan Completeness And Prune

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Indexing/FallbackIndexProvider.cs`
- Modify: `src/ListaryOpen.Infrastructure/Indexing/IndexingCoordinator.cs`
- Modify: `src/ListaryOpen.Infrastructure/Indexing/Ntfs/UsnJournalChange.cs`
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/FallbackIndexProviderTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/IndexingCoordinatorTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] **Step 1: Add failing tests proving incomplete fallback and post-scan ambiguity never prune**

The fallback test must expect a child enumeration failure to propagate. The coordinator test must retain an old indexed record when post-full-scan journal completion cannot be proved. The SQLite test must prove post-scan USN upserts can carry the active index generation.

- [ ] **Step 2: Run those exact tests and verify RED**

- [ ] **Step 3: Make fallback child errors fail the scan**

Set child enumeration to report access failures and throw `IOException` instead of silently ending a subtree. Reuse the coordinator's existing failure path so prune is never reached.

- [ ] **Step 4: Protect NTFS prune with a bounded post-scan catch-up**

Use the already queried pre-scan journal state as a conservative lower bound. Query the post-scan state, read `[pre.NextUsn, post.NextUsn)`, apply unambiguous changes with the current index generation, then prune and save the checkpoint. If any step is unavailable or ambiguous, preserve existing records and skip prune/checkpoint advancement.

- [ ] **Step 5: Run focused indexing tests and verify GREEN**

### Task 3: Search Usage Write-Back

**Files:**
- Modify: `src/ListaryOpen.Core/Search/ISearchIndex.cs`
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Modify: `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs`

- [ ] **Step 1: Add failing persistence and activation tests**

```csharp
await index.RecordUsageAsync(record.FullPath, now, CancellationToken.None);
await index.RecordUsageAsync(record.FullPath, now.AddMinutes(1), CancellationToken.None);
```

Assert one usage row, `open_count == 2`, and the later timestamp. In the ViewModel test, assert usage is recorded only after a successful open or dialog jump.

- [ ] **Step 2: Verify RED**

- [ ] **Step 3: Add one SQLite upsert and call it after successful activation**

Use the existing `usage(path_key primary key)` table and `CreatePathKey`. A usage-write failure must be traced but must not turn an already successful shell open or dialog jump into a user-visible failure.

- [ ] **Step 4: Verify GREEN**

### Task 4: Filter-Only Search Fast Path

**Files:**
- Modify: `src/ListaryOpen.Core/Search/ParsedSearchQuery.cs`
- Modify: `src/ListaryOpen.Infrastructure/Search/SqliteSearchIndex.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Search/SqliteSearchIndexTests.cs`

- [ ] **Step 1: Add a failing test that `ext:pdf` skips expensive fuzzy candidates**

Assert `UsesExpensiveFuzzyCandidatesForTests(new SearchQuery("ext:pdf", ...))` is false and the filtered result order is deterministic.

- [ ] **Step 2: Verify RED**

- [ ] **Step 3: Route queries without positive terms or phrases directly to the filtered fallback pass**

Do not add FTS, WAL, a second connection, or a new diagnostics subsystem. Add one `Trace.TraceInformation` line for total candidate-read and ranking elapsed time.

- [ ] **Step 4: Verify GREEN**

### Task 5: Keyboard And Folder-Mode Contract

**Files:**
- Modify: `src/ListaryOpen.App/SearchPanel.xaml.cs`
- Modify: `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelKeyboardTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/SearchPanelViewModelTests.cs`

- [ ] **Step 1: Add failing Up/Down and `file:` folder-mode tests**

The keyboard test must prove Down selects the next result and Up selects the previous result while query focus remains. The folder-mode test must prove `file:` does not call the index and produces a clear status.

- [ ] **Step 2: Verify RED**

- [ ] **Step 3: Add clamped selection movement and reject `file:` in folder modes**

Keep current Ctrl+C behavior and do not add automatic panel closing.

- [ ] **Step 4: Verify GREEN**

### Task 6: Stable Quick Switch Target

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Modify: `src/ListaryOpen.Infrastructure/Dialog/DialogBridge.cs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/IHookQuickSwitchBridge.cs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/AppDialogHotkeyTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Dialog/DialogBridgeHookTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookQuickSwitchBridgeTests.cs`

- [ ] **Step 1: Add failing tests for a captured hook dialog surviving panel focus**

Capture the active `HookDialogContext` at hotkey time, show the panel, then assert the selected folder is sent to that dialog id and architecture rather than querying the new foreground window.

- [ ] **Step 2: Verify RED**

- [ ] **Step 3: Add the smallest explicit-dialog overload through the existing bridge**

Do not create a generic target framework. Keep the existing active-dialog API for direct jumps and add only the explicit hook-dialog path required by the panel fallback. Preserve the current Blender adapter changes.

- [ ] **Step 4: Verify GREEN**

### Task 7: Hook Success And Health Semantics

**Files:**
- Modify: `native/ListaryOpen.Hooks/hook_dll/src/lib.rs`
- Modify: `native/ListaryOpen.Hooks/hook_host/src/main.rs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs`
- Modify: `src/ListaryOpen.Infrastructure/Dialog/WindowsDialogAutomation.cs`
- Test: native unit tests in the same Rust modules
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookQuickSwitchBridgeTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Dialog/WindowsDialogAutomationTests.cs`

- [ ] **Step 1: Add failing tests that filename edit is not a navigation control, disabled status avoids IPC, and failed hook initialization reports unhealthy**

- [ ] **Step 2: Verify RED where the local toolchain permits; record linker limitations explicitly**

- [ ] **Step 3: Remove filename-edit success fallback and add the disabled fast path**

Only a real address/navigation control or the dedicated browse-folder message may report success. Health must include hook-thread readiness, not only pipe responsiveness.

- [ ] **Step 4: Run managed focused tests and native tests where available**

### Task 8: Hook Host Client And Parent Binding

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookHostProcessFactory.cs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridgeFactory.cs`
- Modify: `native/ListaryOpen.Hooks/hook_host/src/main.rs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookHostProcessFactoryTests.cs`
- Test: native unit tests in `hook_host/src/main.rs`

- [ ] **Step 1: Add failing argument and authorization tests**

The launch arguments must carry the managed App PID. The host must reject a pipe client whose PID does not match, reject remote pipe clients, and terminate after the parent process exits.

- [ ] **Step 2: Verify RED**

- [ ] **Step 3: Implement PID binding and parent watchdog with existing Win32 APIs**

Do not add an authentication package or service. Keep the same-user ACL as defense in depth.

- [ ] **Step 4: Verify GREEN where toolchains permit**

### Task 9: Quick Switch Candidate Hot Path

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Modify: `src/ListaryOpen.App/ExplorerObservationScheduler.cs`
- Modify: `src/ListaryOpen.Infrastructure/Windows/CompositeQuickSwitchWindowProvider.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/AppDialogHotkeyTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/ExplorerObservationSchedulerTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Windows/ExplorerTrackerTests.cs`

- [ ] **Step 1: Add a failing test proving hotkey reads a snapshot and last-active source wins**

- [ ] **Step 2: Verify RED**

- [ ] **Step 3: Cache one bounded candidate snapshot using the existing observation timer**

Do not convert the provider interface into an async plugin framework. The hotkey path must not launch `dopusrt` or enumerate every Total Commander child synchronously.

- [ ] **Step 4: Verify GREEN**

### Task 10: Cross-Review And Verification

- [ ] **Step 1: Run focused tests after every task**
- [ ] **Step 2: Run `dotnet test ListaryOpen.sln --no-restore`**
- [ ] **Step 3: Run Rust tests for common, host, and DLL; report missing linker/toolchain instead of claiming success**
- [ ] **Step 4: Dispatch independent correctness, security, and over-engineering reviews over the final diff**
- [ ] **Step 5: Confirm `git status --short` still contains all pre-existing user changes and only intentional new edits**

## Self-Review

- Spec coverage: all immediate consensus items are assigned; pinyin dependency, settings UI, FTS/WAL, new adapters, and automatic panel closing remain deliberately deferred.
- Placeholder scan: no TODO/TBD implementation placeholders remain.
- Type consistency: usage stays on `ISearchIndex`; explicit target stays on the existing hook bridge; scan completeness stays inside indexing infrastructure.
- Scope: tasks are independently testable and file ownership is separated for parallel execution.
