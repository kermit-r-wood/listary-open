# Indexing Performance Design

## Goal

Reduce ListaryOpen indexing memory, database size, and first-index latency while preserving an Everything-like local search experience.

## Confirmed Requirements

- Use the full architecture option: optimize memory, database size, first indexing speed, and overall Everything-like behavior.
- Store the final index under the program directory, not under the user profile:
  - `<program-directory>\data\index.db`
- Store helper temporary files under the program directory:
  - `<program-directory>\data\tmp\listary-open-indexer-*.jsonl`
  - `<program-directory>\data\tmp\listary-open-indexer-*.err`
- Do not silently fall back to a user-profile directory if the program directory is not writable. Show a clear blocking error instead.
- Do not show the Settings window automatically on ordinary startup.
- Keep Settings available for user-triggered opening and blocking startup errors.
- Add default exclusions for high-noise development and cache directories.

## Current Problems

- The elevated NTFS helper currently materializes a full-volume map of USN/MFT entries before projecting paths, causing roughly 1 GB memory usage on the user's machine.
- The UAC file path currently writes helper output to the system temp directory and the main process reads the whole JSONL output in one string.
- The SQLite database currently lives under `%LOCALAPPDATA%\ListaryOpen\index.db`.
- The database has reached about 2 GB for about 1.94 million records because broad indexing includes high-noise folders and stores multiple derived text columns and indexes.
- The application currently opens the Settings window on startup, which is too intrusive for a background launcher/search tool.

## Architecture

### Data Paths

Create a small app path service that resolves:

- `DataDirectory`: `AppContext.BaseDirectory\data`
- `IndexDatabasePath`: `DataDirectory\index.db`
- `IndexerTempDirectory`: `DataDirectory\tmp`

Startup creates these directories before opening SQLite or launching helpers. If creation fails, the app starts Settings with a blocking error message instead of continuing silently.

### Default Exclusions

Apply directory-name based default exclusions during both fallback indexing and NTFS indexing:

`.git`, `.svn`, `.hg`, `node_modules`, `bin`, `obj`, `.vs`, `.idea`, `.vscode`, `packages`, `dist`, `build`, `.cache`, `__pycache__`, `.pytest_cache`, `.next`, `.nuxt`, `target`

The first implementation uses fixed defaults with no UI. Settings UI can come later.

### NTFS Helper

Change the helper from "load all entries, then project" to "stream with bounded state":

1. First pass over `FSCTL_ENUM_USN_DATA` builds directory-only state:
   - file reference number
   - parent file reference number
   - directory name
   - resolved path when known
   - excluded subtree state
2. Resolve directory paths and mark excluded directory subtrees.
3. Second pass streams file and directory records:
   - Directly output records whose parent directory is resolved and not excluded.
   - Do not keep all file records in memory.
   - Skip excluded subtrees.

This still performs two MFT passes, but memory is tied to directory count instead of total file count.

### UAC Output Import

Keep the helper JSONL handoff for UAC because stdout redirection is not available through `runas`.

Change the main process to read the JSONL file line by line and parse records incrementally. It should not use `File.ReadAllTextAsync` for large helper output. Batches are flushed to SQLite as they are parsed.

### SQLite Storage

First implementation reduces database growth through exclusions and rebuild behavior:

- Move database to program data directory.
- Add an index schema/rules version.
- Rebuild `files` when the rules/schema version changes.
- Preserve `usage`.

Deeper schema compression is a follow-up after measuring the effect of exclusions and streaming.

### Startup Behavior

Ordinary startup should be quiet:

- Create services.
- Register hotkeys.
- Start background indexing.
- Do not show Settings.

Show Settings only when:

- The user opens it from tray/menu or another explicit command.
- A blocking startup issue requires user action, such as an unwritable program data directory or hotkey registration failure.

## Error Handling

- UAC cancellation returns no NTFS records and does not trigger a full fallback scan.
- NTFS access failures or helper crashes fall back to ordinary enumeration with an error status.
- Malformed helper output marks NTFS failed and falls back.
- Cancellation kills the helper when possible and deletes temporary files.
- Temporary path validation only permits the configured program `data\tmp` directory, rejects existing paths, and rejects reparse-point parents.
- If the program data directory is not writable, show a clear error and do not use `%LOCALAPPDATA%`.

## Verification

- Tests cover program-directory data paths and temp paths.
- Tests cover startup not showing Settings on ordinary startup.
- Tests cover fallback and NTFS exclusions.
- Tests cover UAC temp output path validation for program `data\tmp`.
- Tests cover streaming JSONL import without `ReadAllTextAsync`.
- Tests cover that schema/rules version changes clear `files` while preserving `usage`.
- Manual verification checks memory during indexing, final database location, temp cleanup, and startup behavior.
