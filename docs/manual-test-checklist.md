# Manual Windows Acceptance Checklist

Run date: 2026-07-03

- [x] Run automated tests.
  - PASS: `dotnet test ListaryOpen.sln` passed 142/142 tests.
- [x] Start ListaryOpen.
  - PASS: app launched after fixing startup dispatcher/XAML binding issues.
  - PASS: `%LocalAppData%\ListaryOpen\index.db` was created.
  - PASS with degraded hotkeys: startup warned that `Ctrl+Space` was already owned; `Ctrl+G` registered and the app continued.
  - PASS: `ListaryOpen Settings` window appeared after dismissing the hotkey warning.
- [x] Press Ctrl+G.
  - PASS: with ListaryOpen running, `Ctrl+G` opened `ListaryOpen Search`.
- [x] Open Notepad.
  - PASS: Notepad opened.
- [ ] Open Notepad Save As.
  - NOT VERIFIED: automated `Ctrl+S` / `Ctrl+Shift+S` SendKeys did not open the modern Notepad Save As dialog in this session.
- [ ] Choose a known folder.
  - FAIL: current app has no completed folder selection/activation path from the search panel.
- [ ] Verify the Save As dialog changes to that exact folder, not just that the file-name field submitted successfully.
  - FAIL: standard Save dialog smoke showed `Ctrl+G` opens ListaryOpen Search, but no dialog folder jump occurred because no tracked/selected folder was available.
- [x] Start a non-elevated Win32 or .NET application with an Open dialog.
  - PARTIAL PASS: a non-elevated .NET `SaveFileDialog` smoke opened a standard `#32770` file dialog.
- [ ] Press Ctrl+G and verify folder jump.
  - FAIL: `Ctrl+G` opened ListaryOpen Search over the standard dialog, but did not jump the dialog to a folder.
- [ ] Start an administrator-elevated app with an Open dialog.
  - NOT RUN: UAC elevation cannot be completed from this automation session.
- [ ] Press Ctrl+G and verify ListaryOpen reports a permission-limited target.
  - NOT RUN: requires an elevated target dialog.
- [ ] Open a custom unsupported dialog and verify ListaryOpen reports unsupported dialog.
  - NOT VERIFIED: no visible unsupported-dialog reporting path was available during this session.
