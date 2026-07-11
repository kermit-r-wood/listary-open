# Hook and Indexer Regression Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore MobaXterm native dialog jumps, reliable x86 hook-host startup status, and truthful NTFS fast-index enablement.

**Architecture:** Preserve the hardened session-bound IPC. Make native folder verification distinguish unsupported readback from a confirmed mismatch, reuse one bounded health-probe retry helper after host launches, and make NTFS enablement return success before the view model updates state or requests indexing.

**Tech Stack:** Rust/Win32, .NET 8/WPF, xUnit

---

### Task 1: Compatible Native Dialog Verification

**Files:**
- Modify: `native/ListaryOpen.Hooks/hook_dll/src/lib.rs`

- [ ] **Step 1: Write the failing Rust test**

Add a pure status-selection test asserting that no `CDM_GETFOLDERPATH` result after a successfully sent navigation is `Success`, while a returned mismatching path is `Failed`.

- [ ] **Step 2: Run the test to verify RED**

Run: `cargo test --manifest-path native/ListaryOpen.Hooks/hook_dll/Cargo.toml standard_dialog_ack`

Expected: FAIL because missing readback currently maps to `Failed`.

- [ ] **Step 3: Implement the minimal compatibility rule**

Change the pure verification helper so `None` means the dialog does not support readback and returns `Success`; `Some(path)` still requires `folder_paths_match`.

- [ ] **Step 4: Run the focused Rust tests**

Run: `cargo test --manifest-path native/ListaryOpen.Hooks/hook_dll/Cargo.toml standard_dialog_ack`

Expected: PASS, or an explicit environment blocker if MSVC `link.exe` is unavailable.

### Task 2: Bounded Hook Host Readiness

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs`
- Modify: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookQuickSwitchBridgeTests.cs`

- [ ] **Step 1: Write the failing xUnit test**

Add a health client whose first probe reports unavailable and second probe reports success. Enable a bridge through the existing fake process factory and assert the relevant architecture becomes `HostRunning`.

- [ ] **Step 2: Run the test to verify RED**

Run: `dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~HookQuickSwitchBridgeTests`

Expected: FAIL because launch readiness probes only once.

- [ ] **Step 3: Implement minimal bounded polling**

Replace launch-time calls to the single-probe helper with one helper that retries the same probe until it succeeds or a short fixed startup deadline expires. Delay with `Task.Delay` and the caller cancellation token; do not change IPC.

- [ ] **Step 4: Run focused tests**

Run the Task 2 command again.

Expected: PASS.

### Task 3: Truthful NTFS Enablement

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Modify: `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`
- Modify: `tests/ListaryOpen.Infrastructure.Tests/App/SettingsViewModelTests.cs`
- Modify: `tests/ListaryOpen.Infrastructure.Tests/App/AppStartupWindowTests.cs`

- [ ] **Step 1: Write failing view-model and app tests**

Change the enable callback seam to `Func<bool>`. Assert a false callback leaves `NtfsFastIndexingEnabled` false, exposes an unavailable-helper message, and does not invoke the reindex callback. Keep a success test asserting true enables the feature and requests reindex once.

- [ ] **Step 2: Run the tests to verify RED**

Run: `dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTests|FullyQualifiedName~AppStartupWindowTests"`

Expected: compile/test failure because the callback does not return availability.

- [ ] **Step 3: Implement minimal success propagation**

Make `App.EnableNtfsFastIndexing` return false when the client is null or unavailable after enabling UAC mode; request reindex and return true only when available. Update `SettingsViewModel` to set enabled state only when the callback returns true and otherwise show `NTFS fast indexing: unavailable (elevated helper bundle is missing).`.

- [ ] **Step 4: Run focused tests**

Run the Task 3 command again.

Expected: PASS.

### Task 4: Verification and Commit

**Files:**
- Verify all modified files above.

- [ ] **Step 1: Check the diff**

Run: `git diff --check`

Expected: exit 0.

- [ ] **Step 2: Run the .NET suite serially**

Run: `dotnet test ListaryOpen.sln --no-restore --maxcpucount:1`

Expected: all tests pass without testhost file-lock races.

- [ ] **Step 3: Run Rust tests**

Run: `cargo test --manifest-path native/ListaryOpen.Hooks/Cargo.toml`

Expected: all tests pass when Visual C++ Build Tools are installed; otherwise report the missing `link.exe` blocker.

- [ ] **Step 4: Commit the implementation**

Run: `git add native/ListaryOpen.Hooks/hook_dll/src/lib.rs src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs src/ListaryOpen.App/App.xaml.cs src/ListaryOpen.App/ViewModels/SettingsViewModel.cs tests/ListaryOpen.Infrastructure.Tests/Hooks/HookQuickSwitchBridgeTests.cs tests/ListaryOpen.Infrastructure.Tests/App/SettingsViewModelTests.cs tests/ListaryOpen.Infrastructure.Tests/App/AppStartupWindowTests.cs docs/superpowers/plans/2026-07-11-hook-indexer-regression-fixes.md && git commit -m "fix: restore hook and indexer enablement"`

Expected: commit succeeds and `git status --short` is empty.
