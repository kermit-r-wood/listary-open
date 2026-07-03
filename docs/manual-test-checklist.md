# Manual Windows Acceptance Checklist

Run date: 2026-07-03

- [x] Run automated tests.
  - PASS: `dotnet test ListaryOpen.sln` passed 206/206 tests.
  - PASS: `dotnet build ListaryOpen.sln --no-restore` completed with 0 warnings and 0 errors.
- [x] Start ListaryOpen.
  - PASS: `src/ListaryOpen.App/bin/Debug/net8.0-windows/ListaryOpen.App.exe` launched and remained alive during a 12 second smoke run.
  - PASS: `%LocalAppData%\ListaryOpen\index.db` existed after startup.
  - PASS: background indexing wrote to the SQLite index; `files` contained 4500 rows after the smoke run.
- [ ] Press Ctrl+Space and verify the global search panel opens.
  - NOT VERIFIED in this final smoke run: global hotkey behavior requires interactive desktop focus. Covered by `HotkeyManager` and `App` integration tests.
- [ ] Search for a known indexed file or folder and activate it.
  - NOT VERIFIED manually in this final smoke run: result activation is covered by search panel tests for open, reveal, and copy actions.
- [ ] Open Notepad Save As.
  - NOT VERIFIED: automated `Ctrl+S` / `Ctrl+Shift+S` SendKeys did not reliably open the modern Notepad Save As dialog in this session.
- [ ] Choose a known folder from folder mode.
  - NOT VERIFIED manually: folder-mode activation and dialog jump are covered by search panel and dialog bridge tests.
- [ ] Verify the Save As dialog changes to that exact folder.
  - NOT VERIFIED manually: standard `#32770` dialog automation is covered by `WindowsDialogAutomation` tests, but this run did not complete an end-to-end Notepad dialog jump.
- [x] Start a non-elevated Win32 or .NET application with an Open/Save dialog.
  - PARTIAL PASS from earlier smoke: a non-elevated .NET `SaveFileDialog` opened a standard `#32770` file dialog.
- [ ] Press Ctrl+G and verify folder jump.
  - NOT VERIFIED manually in this final smoke run: `Ctrl+G` dialog-mode routing and folder jump paths are covered by app/search/dialog tests.
- [ ] Start an administrator-elevated app with an Open dialog.
  - NOT RUN: UAC elevation cannot be completed from this automation session.
- [ ] Press Ctrl+G and verify ListaryOpen reports a permission-limited target.
  - NOT RUN: requires an elevated target dialog.
- [ ] Open a custom unsupported dialog and verify ListaryOpen reports unsupported dialog.
  - NOT VERIFIED manually: unsupported dialog status handling is covered by dialog bridge/search panel tests.
