# ListaryOpen Full Experience Repair Design Spec

Date: 2026-07-03
Status: Approved for implementation planning after written-spec review

## Goal

Close the end-to-end gaps found by final review so ListaryOpen behaves as a usable Windows file-search and Quick Save/Open MVP, not only an architectural skeleton.

This repair keeps the existing architecture and finishes the workflows that must work together:

- Indexed files and folders are actually written into SQLite.
- Search results can be activated from the UI.
- Folder-focused search can jump a standard file dialog through `DialogBridge`.
- Explorer folder context is observed at runtime and offered in dialog mode.
- Dialog and indexing failures are visible to users.
- The tray app enforces a single running instance.

## Scope

### In Scope

- Background startup indexing for configured/default indexed roots.
- NTFS-helper output parsing in the normal-process elevated indexer client.
- Fallback indexing when NTFS helper indexing is unavailable or returns no records because of access/runtime failure.
- A minimal indexing status model surfaced through settings and tray interactions.
- SearchPanel keyboard and mouse activation.
- Folder-mode result activation through `DialogBridge`.
- Visible dialog-jump status feedback.
- Runtime Explorer current-folder tracking.
- Single-instance process guard.
- Manual checklist updates after verification.

### Out of Scope

- Full settings editor for adding/removing roots.
- Fully optimized incremental NTFS USN maintenance.
- Custom per-application dialog adapters.
- Running the main UI elevated by default.
- Rich notification center, advanced progress UI, or visual redesign.
- Full text content indexing.

## Architecture

### IndexingCoordinator

Add an application-level coordinator that owns indexing startup work.

Responsibilities:

- Read indexed roots from `AppSettings.Defaults()` for this repair.
- For each root, determine the containing volume and select a provider with `VolumeIndexer`.
- Try the selected provider first.
- If the selected provider is NTFS and scanning fails or yields no records, fall back to `FallbackIndexProvider`.
- Batch upsert records into `SqliteSearchIndex`.
- Report `Idle`, `Indexing`, `Completed`, and `Failed` state through a small status object.
- Run indexing in the background without blocking WPF startup.
- Support cancellation on app shutdown.

The coordinator should not own UI controls. App startup wires it to settings/status view models.

### ElevatedIndexerClient Output Parsing

`ElevatedIndexerClient.ScanNtfsAsync` must consume helper stdout instead of discarding it.

Behavior:

- Start helper with `scan <root>`.
- Read newline-delimited JSON records from stdout.
- Convert each JSON record into `FileRecord`.
- Continue to capture stderr for diagnostics.
- If helper exits non-zero, trace diagnostics and yield no remaining records.
- Preserve cancellation cleanup behavior added earlier.
- Treat malformed helper output as a helper/data failure, not as successful empty indexing.

This keeps the helper boundary useful while preserving the fallback path when native indexing is unavailable.

### Search Result Activation

SearchPanel becomes an actionable tool surface.

Normal file-search mode:

- `Enter` opens the selected result using shell execution.
- Double-click opens the selected result.
- `Ctrl+Enter` reveals the selected file or folder in Explorer.
- `Ctrl+C` copies the selected full path to the clipboard.
- Activation failures show a short status message in the panel and are traced.

Folder-focused dialog mode:

- Results are folders only.
- `Enter` or double-click calls a folder activation callback supplied by App startup.
- The callback calls `DialogBridge.JumpToFolderAsync(selectedFolder)`.
- The panel displays result status: success, permission limited, unsupported dialog, or failed.
- On success, the panel may hide after the jump.

The SearchPanel should keep keyboard focus and predictable selection as results update.

### Dialog Status Feedback

Add visible status text to SearchPanel for dialog mode and activation failures.

Examples:

- `Dialog folder changed.`
- `The active dialog is permission-limited.`
- `The active window is not a supported file dialog.`
- `The selected path no longer exists.`

Trace logging remains for detailed diagnostics, but user-visible status is required.

### ExplorerTracker Runtime Observation

Upgrade `ExplorerTracker` from a test-only setter to runtime observation.

Behavior:

- Observe the foreground window when dialog mode is invoked and periodically while the app is running.
- If the foreground window belongs to Explorer, query open Explorer windows through Shell COM and capture the active window's folder path.
- Store the last existing folder path.
- Preserve `ObserveFolderForTests` for deterministic tests.
- Fail quietly if Shell COM is unavailable or the Explorer window cannot be mapped.

`Ctrl+G` should still work without Explorer context; it opens folder-focused search. When a last Explorer folder exists, it appears first.

### Single Instance

Add a process-level single-instance guard.

Behavior:

- Create a named mutex during startup.
- If another instance already owns the mutex, show a short message or exit cleanly.
- The first instance remains responsible for tray, hotkeys, indexing, and UI.
- Release the mutex on shutdown.

Second-instance handoff to show the existing window is not required for this repair.

## Data Flow

### Startup

1. App acquires the single-instance mutex.
2. App opens SQLite index.
3. App creates index providers, `VolumeIndexer`, `DialogBridge`, `ExplorerTracker`, `SearchPanelViewModel`, windows, tray, and hotkeys on the WPF dispatcher.
4. App starts `IndexingCoordinator` in the background.
5. Settings displays current hotkey/index status.

### Indexing

1. Coordinator reads default indexed roots.
2. Coordinator selects NTFS provider for NTFS volumes.
3. NTFS provider calls `ElevatedIndexerClient`.
4. Client parses helper NDJSON into `FileRecord`s.
5. If NTFS path is unavailable or empty after failure, coordinator uses fallback scan.
6. Coordinator upserts records into SQLite.
7. SearchPanel queries SQLite and returns real results.

### Quick Save/Open

1. User opens a standard file dialog.
2. User presses `Ctrl+G`.
3. App observes Explorer folder context if available.
4. SearchPanel opens in folder-focused mode.
5. User selects a folder.
6. App calls `DialogBridge.JumpToFolderAsync`.
7. SearchPanel shows success or a clear failure message.

## Error Handling

- Index root missing: show failed indexing status for that root; do not crash startup.
- NTFS helper missing or failed: trace diagnostic and use fallback provider.
- Helper output malformed: trace diagnostic, mark NTFS scan failed, and use fallback provider.
- SQLite upsert failure: mark indexing failed and keep app running.
- Search result deleted before activation: show status message and remove/ignore activation.
- Open/reveal shell failure: show status message and trace details.
- Dialog unsupported: show unsupported status in SearchPanel.
- Dialog permission-limited: show permission-limited status in SearchPanel.
- Hotkey conflict: retain existing warning/error behavior.
- Second instance: exit cleanly without stealing hotkeys.

## Testing Strategy

Automated tests:

- `ElevatedIndexerClient` parses valid helper NDJSON into `FileRecord`s.
- `ElevatedIndexerClient` handles malformed JSON and non-zero helper exits without reporting success.
- `IndexingCoordinator` falls back when NTFS provider fails or yields no records after a helper failure.
- `IndexingCoordinator` upserts fallback records into SQLite.
- SearchPanel/ViewModel activation commands call open/reveal/copy/dialog callbacks as expected.
- Folder-focused activation calls `DialogBridge` callback and surfaces status.
- ExplorerTracker accepts Shell/foreground-window test seams and stores only existing folders.
- Single-instance guard reports first/second instance behavior through an injectable mutex seam.

Manual verification:

- `dotnet test ListaryOpen.sln` passes.
- App starts and shows settings/tray.
- Indexing default root produces searchable real files/folders.
- `Ctrl+Space` search opens a selected file.
- `Ctrl+Enter` reveals a selected result in Explorer.
- `Ctrl+C` copies selected path.
- Standard Save/Open dialog plus `Ctrl+G` lets the user select a folder and changes the dialog location.
- Elevated file dialog reports permission-limited.
- Unsupported dialog reports unsupported.
- Starting a second app instance exits cleanly without competing for hotkeys.

## Acceptance Criteria

- App startup no longer produces only an empty SQLite DB; it starts background indexing and writes searchable records.
- SearchPanel results are actionable through keyboard and mouse.
- Folder-focused search can complete a standard dialog folder jump from a selected folder.
- Dialog failures are user-visible, not trace-only.
- NTFS helper output is consumed by the client.
- Fallback indexing is used when NTFS indexing cannot produce records.
- Explorer current folder is offered first when it is available.
- Single-instance enforcement prevents competing tray/hotkey processes.
- Manual checklist records pass for core non-elevated search and dialog-jump flows.
