# Manual Windows Acceptance Checklist

Run date: 2026-07-17
UI modernization verification date: 2026-07-06
Search responsiveness verification date: 2026-07-12

## Automated acceptance baseline

The release gate is now `tools/Invoke-AutomatedAcceptance.ps1`. It runs the full Core and WPF/infrastructure suites, x64 and x86 native-hook suites, real Win32 desktop integration tests, the requirement-to-evidence matrix in `tests/automation-coverage.json`, 27 screenshot checks (20 production WPF states, 6 native-dialog states, and 1 real Firefox composed state), Release publishing, PE architecture/package-content checks, and ZIP verification. Machine-readable results, TRX files, logs, screenshots, and hashes are written to `artifacts/acceptance-results`.

The desktop suite directly verifies real global-hotkey delivery, Explorer/Task Manager/dialog input routing, foreground switching between multiple Explorer windows, rejection of unsupported hosts, repeated input after a completed dialog search, Ctrl+A, Backspace, Up/Down, Enter and Esc handling, real standard file/folder dialog capture, folder jumps, restored input focus and dialog closing. Unchecked items below are retained as optional visual or machine-environment exploratory checks; they are not substitutes for the automated functional release gate.

- [x] Run automated tests.
  - PASS (2026-07-17): the canonical automated acceptance runner completed all requirement groups and produced a verified distributable ZIP.
  - PASS: `dotnet test ListaryOpen.sln -c Release --no-restore` passed in the latest verification run.
  - PASS: `dotnet build ListaryOpen.sln -c Release --no-restore` completed with 0 warnings and 0 errors.
  - PASS: SQLite performance regressions are covered by large-index, bounded-candidate, usage-window, combined-ranking-window, folder-only, and fuzzy-prefilter tests.
  - PASS: relevance tiers, 80 ms idle debounce, worker-thread search, stale-result rejection, batched UI publication, WAL, separate read/write connections, and FTS5 trigram retrieval are covered by automated tests.
  - PASS: the 1,502,401-row real index returned ordinary substring results in 5-13 ms and exact-name results in 5-16 ms through `SqliteSearchIndex` after warmup.
  - PASS: Quick Switch multi-Explorer candidate ordering, remembered-folder fallback, app routing helper, and folder-mode pinning are covered by unit tests.
  - Add an online-only OneDrive folder and verify placeholder descendants appear in search without downloading file contents.
  - Add a directory junction inside an indexed root and verify indexing neither follows the junction nor loops.
  - Index a non-NTFS, removable, or network location and verify Settings reports compatibility scanning on completion.
  - Make a child directory inaccessible and verify the run reports an incomplete/error state rather than silently claiming a complete index.
  - PASS: Firefox upload picker direct navigation is covered by the real native-hook E2E, which requires pre-Show hook loading, `IFileDialog.SetFolder`, visible target-folder evidence, and rejects fallback/automation paths.
  - PASS: `dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~ListaryOpen.Infrastructure.Tests.App" -p:UseAppHost=false -p:BaseOutputPath=.test-ui-app-final\` passed with 111 tests.
  - PASS: `dotnet test ListaryOpen.sln --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.test-ui-solution-final\` passed with 60 Core tests and 411 Infrastructure tests.
  - PASS: `dotnet build src\ListaryOpen.App\ListaryOpen.App.csproj --no-restore --nologo -p:UseAppHost=false -p:BaseOutputPath=.test-ui-app-build\` completed with 0 warnings and 0 errors.
- [ ] Verify modernized search panel UI.
  - Press Ctrl+Space and confirm the panel opens as a compact command palette.
  - Confirm the top mode label reads Search for normal file/folder search.
  - Type a known query and confirm result rows show name, parent path, file/folder badge, and match reason.
  - Type continuously and confirm no search starts until input has been idle for about 80 ms and the current results do not disappear while typing.
  - Confirm exact filename matches stay above prefixes, prefixes above substrings, and usage history cannot promote a weaker textual match.
  - Confirm long names and parent paths trim without overlapping badges.
  - Press Enter, Esc, Ctrl+Enter, and Ctrl+C and confirm existing behavior is unchanged.
  - AUTOMATED VISUAL PASS: empty/results/preview, light/dark, and Chinese search states are captured and checked; interactive hotkey feel remains an optional manual check.
  - Preview UTF-8, UTF-16, GBK, extensionless text, Markdown, HTML, EML, SVG, ZIP/TAR/GZip, 7z/RAR/CAB/ISO, PDF, DOCX/XLSX/PPTX, ODF, EPUB, WAV/MP3/FLAC/video, SQLite, LNK/URL, STL/OBJ/glTF/DXF, EXE/DLL, and TTF/OTF samples.
  - Preview legacy DOC/DOT and PPT/POT/PPS samples with a registered x64 IFilter; record the filter CLSID and DLL, verify Latin and CJK text, and confirm embedded documents and links are not traversed.
  - Repeat legacy Office preview with the filter absent or bitness-incompatible, and with encrypted, corrupt, locked, and over-32-MiB files; confirm metadata fallback, no hang, and no leftover files under `%TEMP%\ListaryOpen\FilterCopies`.
  - Preview multi-image WIM and solid-compressed ESD samples; verify image metadata and a bounded directory listing, confirm no files are applied or mounted, and confirm `%TEMP%\ListaryOpen\WimPreview` has no per-preview leftovers.
  - Preview representative Unicode PST and OST files; verify the bounded folder tree and stored message/unread counts, confirm that subjects, bodies, recipients, and attachments are not read or saved, and verify locked, corrupt, password-protected, oversized, and active Outlook files degrade to metadata without a hang.
  - Preview representative `.evtx` logs; verify newest-event metadata is bounded, event payloads/message resources are not loaded, and corrupt, locked, oversized, or provider-less logs degrade without a hang.
  - Preview representative CFB-backed `.one`, `.pub`, `.wps`, `.et`, and `.dps` files; verify the bounded storage/stream directory appears without opening stream payloads, and malformed or non-CFB files fall back to metadata.
  - Preview representative `.lha` and `.lzh` archives, including Unicode names, nested level-1 directories, malformed headers, oversized declared payloads, and many-entry archives; verify only bounded headers are listed and no compressed payload is decompressed.
  - Preview representative `.djv` and `.djvu` files, including single- and multi-page `FORM:DJVU`/`FORM:DJVM` documents, dimensions, plain `TXTa` OCR text, compressed `TXTz` text, malformed/truncated chunks, and oversized declarations; verify image/JB2/BZZ payloads are not decoded and limits are enforced.
  - Preview representative `.ps`, `.eps`, and `.ai` files, including DSC `%%Title`, `%%Pages`, `%%BoundingBox`, page markers, literal strings, binary EPS wrappers, malformed headers, and oversized files; verify PostScript code is never interpreted, executed, or used to load external resources.
  - Preview representative `.chm` files, including ITSF v2/v3 headers, PMGL/PMGI directory blocks, UTF-8 virtual paths, compressed and uncompressed section markers, malformed directory lengths, and oversized entry counts; verify only the directory is read and MSCompressed/LZX topic payloads are not decompressed.
  - Preview representative `.eot` fonts, including EOT v1/v2 headers, Unicode style/version/full names, embedding flags, malformed string lengths, and compressed/encrypted font data; verify only bounded metadata is shown and font data is not unpacked or installed.
  - Preview representative `.kfx` files and KFX-ZIP packages, including raw Ion records, ZIP members, encrypted/DRM payloads, malformed signatures, and oversized archives; verify central directories/printable metadata are bounded and no payload is decrypted, executed, or rendered.
  - Preview representative `.mdb`, `.mde`, `.accdb`, and `.accde` files, including Jet/ACE headers, UTF-16 object-name samples, encrypted/compiled databases, malformed markers, and oversized files; verify no database engine is loaded and rows, VBA, macros, OLE, memo, attachment, or linked data are not opened.
  - Preview representative `.torrent`, `.pcap`, and `.pcapng` files, including single/multi-file bencode, announce tiers, little/big-endian PCAP records, PCAPNG enhanced packet blocks, malformed lengths, and oversized captures; verify no tracker/network request occurs and payload bytes are never decoded or displayed.
  - Preview representative `.class`, `.wasm`, `.dmp`, `.mdmp`, `.vdi`, `.vmdk`, `.qcow`, `.qcow2`, and `.dmg` files, including bounded class constant pools, Wasm custom sections, minidump stream directories, sparse/descriptor disk headers, UDIF footers, malformed offsets, and oversized files; verify bytecode is never executed, dump payloads are not symbolized, and disks are never mounted or attached.
  - Rapidly move through mixed results and confirm canceled previews never replace the current selection; corrupt and locked files must degrade without closing or freezing search.
- [ ] Verify dialog/quick-switch search panel mode UI.
  - Confirm the Quick Switch search bar stays attached below a supported dialog without stealing focus.
  - Press Ctrl+G and confirm it jumps directly without expanding the search results.
  - Click the attached search box and confirm the candidate list expands and filters by path.
  - Confirm folder-only results remain readable and keyboard focus stays in the query box.
  - Confirm long dialog jump status messages stay within the status area.
  - AUTOMATED VISUAL PASS: collapsed/expanded Quick Switch and x64/x86 standard file/folder dialog states are captured; native keyboard acceptance is covered end to end.
- [ ] Verify modernized settings UI.
  - Open settings from the tray and confirm General, Appearance, Indexing, Search, Hotkeys, Quick Switch, Actions, Advanced, and About pages are visible.
  - Confirm indexed roots, NTFS fast indexing, hook quick switch, and Quick Save/Open states use readable badges.
  - Resize the settings window to its minimum size and confirm text remains readable without overlap.
  - Check 100%, 125%, and 150% DPI if available on the test machine.
  - AUTOMATED VISUAL PASS: all nine settings pages plus dark, geek, Chinese, and semantic badge states are captured; tray access and physical multi-DPI checks remain optional manual checks.
- [x] Start ListaryOpen.
  - PASS: `dotnet src\ListaryOpen.App\.test-ui-app-build\Debug\net8.0-windows\ListaryOpen.App.dll` launched from the final build output and remained alive during a 12 second non-interactive smoke run.
  - PASS: `src/ListaryOpen.App/bin/Debug/net8.0-windows/ListaryOpen.App.exe` launched and remained alive during a 12 second smoke run.
  - PASS: `src/ListaryOpen.App/bin/Debug/net8.0-windows/data/index.db` existed after startup.
  - PASS: indexer temp files are created under `src/ListaryOpen.App/bin/Debug/net8.0-windows/data/tmp`.
  - PASS: background indexing wrote to the SQLite index; `files` contained 4500 rows after the smoke run.
  - PASS: SQLite performance indexes existed after startup: `ix_files_name`, `ix_files_is_directory_name`, `ix_files_parent_path_nocase_name`, `ix_usage_path_key`. The unused leading-wildcard `ix_files_search_text` index is intentionally removed.
- [ ] Press Ctrl+Space and verify the global search panel opens.
  - NOT VERIFIED in this final smoke run: global hotkey behavior requires interactive desktop focus. Covered at the service/routing layer by `HotkeyService` and app tests, not by an end-to-end desktop hotkey test.
- [ ] In Explorer, type a filename while the file list has focus.
  - Confirm the bottom-right overlay receives characters in order, including fast input such as `open`.
  - Press Up/Down and confirm only the overlay selection moves while Explorer's native selection follows the chosen overlay result rather than processing the key itself.
  - Confirm Esc, clicking outside, or returning focus to Explorer dismisses the overlay.
  - Confirm Ctrl+G subsequently opens centered with an empty query.
  - NOT VERIFIED automatically at the Windows hook boundary; predicate, handled routing, selection movement, and Explorer synchronization are covered by `ExplorerTypeSearchTests` and `WpfSmokeTests`.
- [ ] Search for a known indexed file or folder and activate it.
  - NOT VERIFIED manually in this final smoke run: result activation is covered by search panel tests for open, reveal, and copy actions.
- [ ] Open Notepad Save As.
  - NOT VERIFIED: automated `Ctrl+S` / `Ctrl+Shift+S` SendKeys did not reliably open the modern Notepad Save As dialog in this session.
- [ ] Choose a known folder from folder mode.
  - NOT VERIFIED manually: folder-mode activation and dialog jump are covered by search panel and dialog bridge tests.
- [x] Verify the standard dialog changes to that exact folder.
  - PASS: real x64 and x86 `#32770` file/folder dialogs are captured, jumped, focused, screenshotted, closed with Esc, and exercised with Ctrl+A, typing, Backspace, and Enter; the accepted file path is asserted inside the target folder.
- [x] Start a non-elevated Win32 or .NET application with an Open/Save dialog.
  - PARTIAL PASS from earlier smoke: a non-elevated .NET `SaveFileDialog` opened a standard `#32770` file dialog.
- [ ] Press Ctrl+G and verify folder jump.
  - NOT VERIFIED manually in this final smoke run: direct `Ctrl+G` Quick Switch jump routing and dialog jump paths are covered by app/search/dialog tests.
- [x] Open the Firefox upload file picker and complete a Quick Switch folder jump and file selection.
  - PASS: `FirefoxFilePickerAttachesQuickSwitchJumpsAndAcceptsAFile` launches an isolated Firefox profile on a local upload fixture, verifies `firefox/#32770/File Upload`, attaches the production Quick Switch bar, captures the composed desktop, jumps to a temporary folder, selects a sentinel file, and confirms Firefox receives it.
  - Chrome remains a separate optional manual compatibility check; Firefox is the required automated browser picker path.
- [ ] Open multiple Explorer windows and press Ctrl+G over a standard dialog.
  - NOT VERIFIED manually in this final smoke run: multiple Explorer candidates are covered through `ExplorerTracker`, app routing, and `SearchPanelViewModel` tests, but this run did not complete an end-to-end desktop multi-window Quick Switch session.
- [ ] Start an administrator-elevated app with an Open dialog.
  - NOT RUN: UAC elevation cannot be completed from this automation session.
- [ ] Press Ctrl+G and verify ListaryOpen reports a permission-limited target.
  - NOT RUN: requires an elevated target dialog.
- [ ] Open a custom unsupported dialog and verify ListaryOpen reports unsupported dialog.
  - NOT VERIFIED manually: unsupported dialog status handling is covered by dialog bridge/search panel tests.
