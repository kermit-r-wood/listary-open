# ListaryOpen Design Spec

Date: 2026-07-03
Status: Approved for implementation planning

## Goal

Build a Windows-only desktop utility similar to Listary, focused on two features:

- Fast file and folder search with an always-available UI.
- Quick Save / Open integration that jumps standard Windows open/save dialogs to a selected folder.

The target user experience follows the intent of Listary's "Search Files" and "Quick Save and Open" feature pages:

- Search should respond as the user types, show files and folders with paths, and rank useful results higher over time.
- Quick Save / Open should recommend recent folders, allow folder search, and change the active file dialog location without forcing the user to browse manually.

## Product Shape

Version 1 is a full Windows desktop utility:

- A single-instance tray process runs in the background.
- A global search panel appears from a hotkey.
- A settings window manages indexed locations, hotkeys, dialog integration, appearance, and diagnostics.
- The app targets standard Windows file dialogs in version 1. Custom dialogs are detected as unsupported and reported clearly.

## Technology Direction

Use a native C#/.NET Windows application.

- UI: WPF, chosen for mature Windows desktop behavior, tray integration, keyboard handling, and UI Automation interoperability.
- Runtime: .NET 8 or newer on Windows.
- Storage: SQLite for the searchable metadata store and usage history.
- Windows integration: global hotkeys, tray icon, Win32 window detection, and Windows UI Automation.
- Indexing: Everything-like NTFS-first indexing strategy, with fallback scanning for unsupported volumes.

Electron or Tauri are not used for version 1 because the core work depends on Windows-native hotkeys, tray behavior, window handles, UI Automation, and low-level indexing.

## Architecture

### AppHost

Owns process lifetime and global integration.

- Enforces single-instance behavior.
- Starts the tray icon.
- Registers global hotkeys.
- Opens the search panel and settings window.
- Starts and stops indexing services.
- Coordinates shutdown.

### SearchIndex

Provides a unified queryable index over files and folders.

- Stores normalized file records in SQLite.
- Exposes insert, update, delete, and query operations.
- Does not know whether records came from NTFS MFT, USN journal, fallback scanning, or manual entries.
- Keeps search UI code independent from indexing implementation details.

### VolumeIndexer

Coordinates per-volume indexing.

- Detects local volumes and their file system type.
- Chooses the best provider for each volume.
- Tracks indexing status per volume.
- Reports unavailable, unsupported, and permission-limited volumes to diagnostics.

### NtfsIndexProvider

Implements the primary Everything-like strategy for NTFS volumes.

- Builds the initial index from NTFS file record metadata instead of recursive directory traversal.
- Uses the USN Change Journal for incremental updates.
- Handles create, delete, rename, move, and metadata update events.
- Uses the elevated indexing helper process for privileged metadata access when elevation is required.

### FallbackIndexProvider

Handles volumes where NTFS metadata indexing is not available.

- Uses recursive scanning for initial discovery.
- Uses `FileSystemWatcher` where possible for incremental updates.
- Supports non-NTFS local volumes, removable drives, network shares, and permission-limited paths.
- Marks results with provider information so diagnostics can explain slower indexing.

### SearchQuery

Ranks search results.

- Matches filenames and folder names.
- Includes path matching.
- Supports fuzzy matching and abbreviation-style matching.
- Supports pinyin and pinyin-initial matching for Chinese file and folder names.
- Boosts recent and frequently used items.
- Boosts pinned folders.

### SearchPanel

Provides the keyboard-first search UI.

- Opens with `Ctrl+Space`.
- Shows a centered overlay above other windows.
- Updates results as the user types.
- Supports `Enter` to open, `Ctrl+Enter` to reveal in Explorer, and `Ctrl+C` to copy path.
- When opened from a file dialog context, uses folder-focused results and returns a target folder to `DialogBridge`.

### DialogBridge

Implements Quick Save / Open.

- Detects whether the active foreground window is a standard Windows open/save dialog.
- Uses Win32 and UI Automation to locate the target controls.
- Changes the dialog's current folder to a selected directory.
- Can use the active Explorer folder as a jump target when the user invokes Quick Save / Open after working in an Explorer window.
- Reports unsupported custom dialogs and automation failures clearly.
- Records successful jumps as recent folder usage.

### Settings

Manages user configuration.

- Indexed roots and excluded paths.
- Hotkeys.
- Quick Save / Open enablement.
- Pinned folders.
- Appearance.
- Diagnostics and logging preferences.

## Core Workflows

### File Search

1. User presses `Ctrl+Space`.
2. Search panel appears.
3. User types part of a file or folder name.
4. Search service queries SQLite-backed metadata and ranks results.
5. User selects an item.
6. `Enter` opens the item, `Ctrl+Enter` reveals it in Explorer, and `Ctrl+C` copies the path.
7. Usage history is recorded and influences later ranking.

### Quick Save / Open

1. A standard Windows open/save dialog is active.
2. User presses `Ctrl+G` or double-taps `Ctrl`.
3. The folder-focused panel appears with recent, pinned, and searchable folders.
4. User selects a folder.
5. `DialogBridge` changes the active dialog to that folder.
6. On success, the folder is recorded as recently used.
7. On failure, the user sees a short unsupported-dialog or automation-failed message, and diagnostics capture details.

### Explorer-to-Dialog Jump

1. User is working in an Explorer window at a target folder.
2. User activates a standard open/save dialog.
3. User invokes Quick Save / Open.
4. The app offers the most recent active Explorer folder as the primary jump target.
5. User confirms the target, and `DialogBridge` changes the dialog to that folder.

## Indexing Strategy

The index must be designed around an Everything-like model.

Primary path:

- Detect NTFS volumes.
- Read file system metadata at the volume level to build the initial index quickly.
- Use the USN Change Journal for incremental maintenance.
- Persist file and folder metadata to SQLite.

Fallback path:

- Use recursive scanning only when NTFS metadata indexing is unavailable.
- Use `FileSystemWatcher` where supported.
- Make fallback status visible in diagnostics because fallback indexing is expected to be slower and less complete.

Security and privilege boundary:

- NTFS metadata access may require elevated privileges.
- Version 1 uses a normal user-mode WPF app plus an optional elevated indexing helper process.
- The main UI never runs elevated by default.
- If the helper is unavailable or the user declines elevation, indexing falls back to the non-privileged provider for that volume.

## Error Handling

User-visible messages should be short and actionable. Diagnostics should keep the detailed reason.

Cases to handle:

- Indexed root does not exist.
- Access is denied.
- Volume is unsupported.
- NTFS metadata access fails.
- USN journal is unavailable or reset.
- Database is missing, locked, or corrupt.
- Hotkey registration conflicts with another app.
- Search result was deleted before activation.
- Active dialog is not a standard Windows open/save dialog.
- UI Automation cannot locate the needed control.
- Dialog folder jump fails.

## Testing Strategy

Core logic tests:

- Search ranking.
- Fuzzy matching.
- Pinyin and pinyin-initial matching.
- Usage-history boosting.
- Settings serialization.
- Index record normalization.
- Provider selection for NTFS and fallback volumes.

Integration tests:

- SQLite persistence.
- Fallback recursive indexing against temporary test folders.
- File change handling through provider abstractions.
- DialogBridge behavior against mocked UI Automation adapters.

Manual Windows acceptance tests:

- Start app and verify tray/process single-instance behavior.
- Build index for a selected folder.
- Search and open a known file.
- Reveal a known file in Explorer.
- Copy a known path.
- Open Notepad Save As dialog, press `Ctrl+G`, select a folder, and verify the dialog changes location.
- Open a standard Open dialog from a common Win32/.NET app and verify folder jump.
- Open Explorer at a folder, invoke Quick Save / Open from a standard dialog, and verify the Explorer folder is offered as the primary target.
- Open a custom or unsupported dialog and verify the app reports it as unsupported.

## Version 1 Acceptance Criteria

- Runs only on Windows.
- Has a visible UI: tray, settings window, and search panel.
- Provides global hotkey search.
- Maintains a local index without depending on Windows Search.
- Uses an Everything-like NTFS-first indexing strategy.
- Falls back gracefully for unsupported volumes.
- Searches files and folders with path display.
- Supports pinyin and pinyin-initial matching for Chinese names.
- Opens, reveals, and copies search results.
- Records usage history for ranking.
- Detects standard Windows open/save dialogs.
- Jumps standard open/save dialogs to a selected folder.
- Offers the most recent active Explorer folder as a Quick Save / Open jump target.
- Shows clear user feedback for unsupported dialogs or failed automation.
- Provides diagnostics for indexing and dialog integration failures.

## Out of Scope for Version 1

- Cloud drive special integrations beyond normal file system visibility.
- Team features.
- Network-share optimized indexing beyond fallback scanning.
- Custom dialog adapters for WPS, WinRAR, AutoCAD, or other app-specific dialogs.
- Full text file content indexing.
- Web search or app launcher commands unrelated to file search.
