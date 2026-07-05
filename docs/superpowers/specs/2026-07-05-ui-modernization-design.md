# ListaryOpen UI Modernization Design Spec

Date: 2026-07-05
Status: Approved for written-spec review

## Goal

Modernize the ListaryOpen WPF user interface so it feels like a contemporary Windows utility while preserving the tray-first, keyboard-first product shape. The redesign should make the search panel faster to scan, make settings easier to understand, and avoid a risky framework rewrite.

The implementation should keep ListaryOpen as a quiet operational tool:

- Search and dialog quick-switch remain the primary user experience.
- The app stays lightweight and starts quickly.
- Existing keyboard behavior, topmost search behavior, tray lifecycle, indexing, and hook quick-switch flows remain intact.
- Visual changes should improve clarity without adding marketing-style screens or decorative chrome.

## Confirmed Direction

Use a native WPF modernization pass:

- Stay on the current WPF application architecture.
- Do not rewrite the UI in WinUI 3 for this work.
- Do not introduce a global Fluent/WPF UI library for the first pass.
- Add local app-level WPF resource dictionaries and templates.
- Modernize both `SearchPanel.xaml` and `MainWindow.xaml`.
- Prioritize the search panel because it is the primary interactive surface.

## Context From Local Review

The current WPF UI is intentionally small:

- `App.xaml` only defines shutdown mode and the shared icon resource.
- `MainWindow.xaml` is a stock WPF settings window with default controls.
- `SearchPanel.xaml` is a fixed `720x420` topmost window with a query box, a list, and a status line.
- Search results currently show only `Record.FullPath`.
- Existing models already expose richer result data: `Record.Name`, `ParentPath`, `IsDirectory`, `LastWriteTime`, `Score`, and `MatchReason`.
- The app is tray-first and windows hide instead of closing.
- Keyboard behavior is covered by tests and should remain stable.

There is also unrelated active work in the tree around UAC, hooks, and NTFS indexing. This UI design must not revert or depend on those unrelated edits.

## Subagent Review Summary

Two read-only review agents inspected the current UI and feasibility.

Their shared conclusions:

- The highest-impact change is the search panel.
- A native WPF refresh is lower risk than adopting a global UI library.
- A WinUI 3 rewrite is not justified for the current small tray utility.
- The search result row should use existing record fields instead of showing only the full path.
- Settings should be grouped around user tasks and statuses rather than presented as a diagnostic dump.
- Custom window chrome, transparency, acrylic, or Mica could affect focus and topmost behavior, so they are not part of the first pass.
- Add lightweight WPF smoke tests because existing tests protect behavior more than visual/resource loading.

## External Research Summary

The design follows patterns from current Windows utility and search tools:

- Microsoft WPF .NET 9 Fluent theme notes emphasize Windows 11 styling, light/dark support, and accent-color alignment.
- Microsoft Fluent 2 guidance emphasizes neutral surfaces, readable text, and restrained status color.
- PowerToys Command Palette and PowerToys Run emphasize keyboard-first invocation, immediate results, and low-friction command/search workflows.
- Everything and Fluent Search emphasize fast scanning, simple UI, quick indexing/search response, and minimal distraction.

These references support a dense, useful, keyboard-first design rather than a decorative landing-page style.

## Scope

### In Scope

- Add WPF resource dictionaries under `src/ListaryOpen.App/Styles/`.
- Restyle shared typography, colors, buttons, text boxes, list rows, status badges, and focus visuals.
- Redesign `SearchPanel.xaml` as a command-palette style search surface.
- Redesign `MainWindow.xaml` as a grouped settings surface.
- Add small ViewModel-derived properties only when needed for mode labels, placeholders, status badges, or concise detail strings.
- Keep behavior semantics of existing search, activation, indexing status, NTFS status, and hook quick-switch status.
- Add WPF smoke tests for window/resource loading.
- Update the manual acceptance checklist with UI-specific checks.

### Out of Scope

- WinUI 3 migration.
- Adding WPF UI, ModernWpf, or another global Fluent control library.
- Full settings editor for adding/removing indexed roots.
- Hotkey editor.
- Theme switcher.
- Rich notification center.
- Custom window chrome, acrylic, Mica, or borderless-window behavior.
- Changing search ranking, indexing behavior, hook IPC, or dialog jump business logic.

## Visual System

Add these resource dictionaries:

- `Styles/Colors.xaml`
- `Styles/Typography.xaml`
- `Styles/Controls.xaml`
- `Styles/SearchPanel.xaml`
- `Styles/Settings.xaml`

`App.xaml` should merge these dictionaries and keep `ListaryOpenIcon`.

The palette should be mostly neutral:

- light app background
- distinct surface background
- readable primary and secondary text
- single restrained accent color
- separate status colors for success, warning, and error

Avoid a one-hue theme, large gradients, decorative orbs, large hero sections, and heavy illustration. This is a tool UI, so the visual system should support scanning, comparison, and repeated use.

Use stable dimensions for repeated controls:

- result row height
- icon column width
- badge widths or minimum widths
- action button widths
- search/status rows

Text must not overflow buttons or incoherently overlap neighboring content. Long paths and errors should be trimmed or wrapped by design.

## Search Panel Design

`SearchPanel` becomes a compact command-palette surface. It keeps:

- `Topmost=True`
- quick show/focus behavior
- `Esc` hide
- `Enter` activate
- `Ctrl+Enter` reveal
- `Ctrl+C` copy selected path when focus is not in text input
- double-click activate
- hide-on-close lifecycle

The panel is organized into four areas.

### Mode Bar

The top row shows the active mode:

- `Search`
- `Dialog Jump`
- `Quick Switch`

It also shows compact state on the right, such as:

- `Index ready`
- `Searching`
- `3 results`
- failure/degraded status when relevant

The mode indicator should help users understand why folder-only behavior is active without reading long explanatory text.

### Search Input

The query box gets a clear, modern input style:

- larger but not hero-sized text
- visible focus state
- stable height
- concise placeholder text

Placeholder examples:

- `Search files and folders`
- `Jump dialog to folder`
- `Quick switch to folder`

If native WPF placeholder support requires helper logic, keep it small and local. Do not add a large behavior framework just for placeholder text.

### Result Rows

Each result row should show:

- file or folder icon
- primary title from `Record.Name`
- secondary path from `Record.ParentPath`
- type badge: `File` or `Folder`
- source badge based on `MatchReason`, such as `Pinned`, `Usage`, `Name`, `Path`, or `Pinyin`

Long title/path text should trim predictably. The selected row should have clear contrast and remain readable. Hover state should be subtle and not resize the row.

The design can use text-based glyphs or a small local icon style in the first pass. It should not add a new icon package unless implementation proves native glyphs are insufficient.

### Status Bar

The bottom status area continues to bind to `StatusText`.

Rules:

- Normal status is low contrast and single-line when possible.
- Long paths and expected operation failures may wrap to at most two lines.
- The status area must not resize the whole panel unpredictably.
- Search failures, activation failures, copy failures, reveal failures, and dialog jump failures keep their existing wording semantics.

### Empty And Loading States

Represent common states clearly:

- empty query: `Type to search.`
- searching: `Searching...`
- no result: `No results.`
- failure: existing `StatusText` failure message

The result list should not steal focus from the query box during these states.

## Settings Window Design

`MainWindow` becomes a grouped settings surface. It remains a simple WPF window, not a full navigation app.

Suggested size:

- width around `720`
- height around `520`
- sensible min width/height

Use four full-width groups.

### Indexing

Show:

- indexing status as a concise state plus detail text
- indexed roots list
- NTFS fast indexing state

NTFS status should use a badge:

- `Enabled`
- `Disabled`
- `Needs admin`
- `Failed`

The action button should reflect state where available:

- `Enable`
- `Enabled`
- `Retry`

Long root paths should trim or wrap within a stable row layout.

### Hotkeys

Show current configured hotkeys:

- `Search: Ctrl+Space`
- `Dialog jump: Ctrl+G`

This pass does not add hotkey editing.

### Quick Switch

Show hook quick-switch capability as:

- short status badge: `Ready`, `Disabled`, `Degraded`, or `Failed`
- detail row with the existing status text
- stable action button

The Enable button should visually prevent repeated clicks while enable is in progress. Existing command enablement is the behavioral source of truth.

### General

Show `Quick Save / Open` as a read-only settings row instead of a disabled checkbox.

Each row should include:

- feature name
- current state badge
- short detail text when needed

Do not add promotional copy or a large hero header.

## Data Flow

Prefer binding to existing models:

- `SearchPanelViewModel.Results`
- `SearchPanelViewModel.SelectedResult`
- `SearchPanelViewModel.StatusText`
- `SearchPanelViewModel.QueryText`
- `SettingsViewModel.IndexingStatusText`
- `SettingsViewModel.NtfsFastIndexingStatusText`
- `SettingsViewModel.HookQuickSwitchStatusText`
- `SettingsViewModel.Settings`

If the UI needs concise labels or badges, add read-only derived properties:

- search mode display name
- search placeholder text
- result count/status display
- NTFS status kind
- hook status kind
- concise settings detail strings

Do not move business logic into XAML converters if it would become hard to test. Use small ViewModel properties for state classification when tests should cover the mapping.

## Error Handling

The UI layer should not swallow or reinterpret business failures.

Keep these existing flows:

- search exceptions update `StatusText`
- open/reveal/copy failures update `StatusText`
- dialog jump failures update `StatusText`
- hook quick-switch status remains surfaced through `SettingsViewModel`
- indexing status remains surfaced through `SettingsViewModel`

The redesign only changes presentation:

- trim or wrap long messages
- color warning/error statuses appropriately
- keep focus in the expected control
- keep the layout stable

## Accessibility

The first implementation should include:

- clear focus visuals for keyboard navigation
- predictable tab order in settings
- readable contrast in normal and selected states
- labels or automation names for search input, results list, status text, and key settings rows
- DPI-safe sizing
- no text overlap at small supported window sizes

High-contrast-specific theme work is not part of this pass, but the resource design should not block it later.

## Testing And Verification

Run existing tests relevant to behavior:

- `SearchPanelKeyboardTests`
- `SearchPanelViewModelTests`
- `SettingsViewModelTests`
- `AppStartupWindowTests`
- `AppDispatcherStartupTests`
- `AppIconTests`

Add lightweight WPF smoke tests that:

- instantiate `MainWindow` on an STA thread
- instantiate `SearchPanel` on an STA thread
- verify merged style resources load
- verify key named controls exist
- verify windows still use `ListaryOpenIcon`

Manual acceptance should cover:

- `Ctrl+Space` opens search
- `Ctrl+G` opens folder/dialog mode
- typing searches without losing focus
- arrow selection remains readable
- `Enter`, `Esc`, `Ctrl+Enter`, and `Ctrl+C` still work
- long file names and long parent paths do not overlap
- long status/error messages do not resize the panel unpredictably
- settings window scrolls when needed
- indexed roots, NTFS status, hook status, and hotkeys are readable
- 100%, 125%, and 150% DPI remain usable

## Implementation Notes

Likely touched files:

- `src/ListaryOpen.App/App.xaml`
- `src/ListaryOpen.App/MainWindow.xaml`
- `src/ListaryOpen.App/SearchPanel.xaml`
- `src/ListaryOpen.App/ViewModels/SearchPanelViewModel.cs`
- `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`
- `src/ListaryOpen.App/Styles/*.xaml`
- `tests/ListaryOpen.Infrastructure.Tests/App/*`
- `docs/manual-test-checklist.md`

Keep the implementation incremental:

1. Add resource dictionaries and smoke tests.
2. Modernize `SearchPanel.xaml`.
3. Add only the ViewModel properties required by the search UI.
4. Modernize `MainWindow.xaml`.
5. Add only the ViewModel properties required by the settings UI.
6. Run behavior tests and the manual visual checklist.

## Risks

- Overly broad implicit styles could unexpectedly alter tray context menus or non-target controls. Keep styles scoped or named where risk is high.
- Custom window chrome could disturb search-panel focus and topmost behavior. Avoid it in this pass.
- Long status strings may still overflow if the status container is not constrained. Test long path and long error cases.
- Adding visual state derived properties can accidentally change asserted status strings. Preserve existing strings and add new properties rather than replacing business status text.
- Existing unrelated UAC/hook/NTFS edits in the worktree should not be reverted or bundled into this design commit.

## Success Criteria

The work is successful when:

- Search panel visually reads as a modern keyboard-first command palette.
- Result rows are scannable by name, path, type, and match source.
- Settings is grouped into clear operational sections.
- Existing search, activation, dialog mode, tray lifecycle, and settings behavior tests still pass.
- New WPF smoke tests prove windows and resources load.
- Manual checks confirm no obvious text overlap, path overflow, or focus regressions.
