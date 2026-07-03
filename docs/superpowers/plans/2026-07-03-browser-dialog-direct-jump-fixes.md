# Browser Dialog Direct Jump Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the empty app icon, make `Ctrl+G` jump directly to the current Quick Switch folder, and improve Windows file picker support for Firefox and Chrome upload dialogs.

**Architecture:** Keep the existing WPF app and `DialogBridge` boundary. Add a shared application icon resource, route dialog hotkeys through a direct-jump helper before falling back to folder search, and make `WindowsDialogAutomation` locate browser-owned standard file dialogs more robustly by finding the foreground dialog/owned popup and using multiple UI Automation lookup strategies.

**Tech Stack:** .NET 8, WPF, Hardcodet NotifyIcon, Win32 window handles, Windows UI Automation, xUnit.

---

### Task 1: Application And Tray Icon

**Files:**
- Create: `src/ListaryOpen.App/Assets/ListaryOpen.ico`
- Modify: `src/ListaryOpen.App/ListaryOpen.App.csproj`
- Modify: `src/ListaryOpen.App/MainWindow.xaml`
- Modify: `src/ListaryOpen.App/SearchPanel.xaml`
- Modify: `src/ListaryOpen.App/Tray/TrayController.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/AppIconTests.cs`

- [ ] **Step 1: Write failing icon resource tests**

Add tests that assert the icon file exists, the app project declares it as `ApplicationIcon`, and the tray controller references the same resource.

- [ ] **Step 2: Run tests and verify red**

Run: `dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~AppIconTests --no-restore`

Expected: fails because the icon file and project declarations do not exist yet.

- [ ] **Step 3: Add icon resource and wire it into WPF**

Create `Assets/ListaryOpen.ico`, set `<ApplicationIcon>Assets\ListaryOpen.ico</ApplicationIcon>`, set `Icon="Assets/ListaryOpen.ico"` on both windows, and set `TaskbarIcon.IconSource` from the pack URI.

- [ ] **Step 4: Run icon tests**

Run: `dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~AppIconTests --no-restore`

Expected: pass.

### Task 2: Direct Ctrl+G Jump

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/AppDialogHotkeyTests.cs`

- [ ] **Step 1: Write failing route-selection tests**

Add tests for a pure helper that chooses direct jump when Quick Switch candidates exist and chooses folder search only when candidates are empty.

- [ ] **Step 2: Run tests and verify red**

Run: `dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~AppDialogHotkeyTests --no-restore`

Expected: fails because the direct-jump decision helper does not exist.

- [ ] **Step 3: Implement direct-jump route**

`HandleDialogHotkey` should observe candidates, call `JumpDialogToFolderAsync` for the first candidate folder, and only call `ActivateQuickSwitchFolderSearch` when there is no candidate. Any direct-jump failure should fall back to showing folder search with the same candidates so errors remain visible and recoverable.

- [ ] **Step 4: Run route tests**

Run: `dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~AppDialogHotkeyTests --no-restore`

Expected: pass.

### Task 3: Firefox And Chrome Upload File Picker Compatibility

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Dialog/WindowsDialogAutomation.cs`
- Modify: `src/ListaryOpen.Infrastructure/Windows/NativeMethods.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Dialog/WindowsDialogAutomationTests.cs`

- [ ] **Step 1: Write failing automation-helper tests**

Add tests for class-name acceptability and control lookup preference: standard `#32770` dialogs are accepted, browser process foreground windows can resolve an owned `#32770` popup, and file-name edit/commit button lookup has fallback conditions.

- [ ] **Step 2: Run tests and verify red**

Run: `dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~WindowsDialogAutomationTests --no-restore`

Expected: fails because helper seams and fallback lookup behavior do not exist.

- [ ] **Step 3: Implement robust dialog discovery and lookup**

Add Win32 owned-popup enumeration and split the static decision logic into testable helpers. Keep permission-limited handling. Set folders through the best available file-name edit and commit button, using fallback control-name/type conditions when automation IDs differ.

- [ ] **Step 4: Run dialog automation tests**

Run: `dotnet test tests/ListaryOpen.Infrastructure.Tests/ListaryOpen.Infrastructure.Tests.csproj --filter FullyQualifiedName~WindowsDialogAutomationTests --no-restore`

Expected: pass.

### Task 4: Final Verification

**Files:**
- Modify: `docs/manual-test-checklist.md`

- [ ] **Step 1: Run full automated verification**

Run:
`dotnet test ListaryOpen.sln --no-restore`
`dotnet build ListaryOpen.sln --no-restore`

- [ ] **Step 2: Run manual smoke where possible**

Start the app, verify it stays alive, verify the tray/window icon is visible, and check Firefox/Chrome upload picker behavior if the browsers are available in this Windows session.

- [ ] **Step 3: Update manual checklist**

Record automated pass/fail and manual browser picker status without overstating unverified cases.
