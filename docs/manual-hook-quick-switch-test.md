# Manual Hook Quick Switch Acceptance

Use this checklist to validate hook-based quick switch coverage across accessible local software. UI Automation is not part of this acceptance path; successful jumps should go through the native hook host and DLL for the dialog process architecture.

## Setup

- Build and run `ListaryOpen.App` with native hooks packaged.
- Open Settings and click **Enable Hook Quick Switch**.
- Approve the single expected UAC prompt at ListaryOpen startup.
- If Windows reports an unknown publisher, treat that as expected for unsigned local binaries; signed builds should show the configured publisher instead.
- Confirm Settings reports hook quick switch enabled for both `x64` and `x86`.
- Use `%USERPROFILE%` as the target folder for each `Ctrl+G` jump.
- Keep the target file/open/upload dialog in the foreground before pressing `Ctrl+G`.
- If hook quick switch is disabled or a host is unavailable, verify the app reports disabled/unavailable state and any fallback is visibly degraded rather than silent native-hook success.

## Required Targets

- [x] Antigravity open-folder dialog is detected by the `x64` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] Notepad++ open dialog is detected by the `x64` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] Chrome open dialog and real upload picker are detected by the `x64` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] Edge open dialog and real upload picker are detected by the `x64` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] Firefox open dialog and real upload picker are detected by the `x64` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] VS Code open dialog is detected by the `x64` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] MobaXterm private-key open dialog is detected by the `x86` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] MobaXterm upload dialog is detected by the `x86` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] Paint open/save-as dialogs are detected by the `x64` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] WordPad open dialog is detected by the `x64` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] Windows PowerShell ISE x86 open dialog is detected by the `x86` hook and `Ctrl+G` jumps to `%USERPROFILE%`.
- [x] VLC open dialog is detected by the `x64` hook and `Ctrl+G` jumps to `%USERPROFILE%`.

## Latest Local Verification

Run date: 2026-07-09.

- PASS: Directory Opus source provider read active lister paths through `dopusrt /info`, returning `C:\`.
- PASS: Total Commander source provider read `TTOTAL_CMD` child text through `WM_GETTEXT`, including `c:\>` and `c:\*.*`.
- PASS: Notepad `Ctrl+O` dialog was detected by the `x64` hook as `notepad.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Paint `Ctrl+O` dialog was detected by the `x64` hook as `mspaint.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Paint `Ctrl+S` Save As dialog was detected by the `x64` hook as `mspaint.exe/#32770/Save As`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: WordPad `Ctrl+O` dialog was detected by the `x64` hook as `wordpad.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Windows PowerShell ISE x86 `Ctrl+O` dialog was detected by the `x86` hook as `powershell_ise.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: VLC `Ctrl+O` dialog was detected by the `x64` hook as `vlc.exe/#32770/Select one or more files to open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Notepad++ `Ctrl+O` dialog was detected by the `x64` hook as `notepad++.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: 7-Zip File Manager browse-folder dialog was detected by the `x64` hook as `7zFM.exe/#32770/Browse For Folder`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Chrome `Ctrl+O` dialog was detected by the `x64` hook as `chrome.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Chrome real `<input type=file>` upload picker from a temporary local HTML page was detected by the `x64` hook as `chrome.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open and no file was selected.
- PASS: Firefox `Ctrl+O` dialog was detected by the `x64` hook as `firefox.exe/#32770/Open File`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Firefox real upload picker was detected by the `x64` hook as `firefox.exe/#32770/File Upload`; `JumpDialogToFolder` returned `Success`; dialog remained open and no file was selected.
- PASS: Edge `Ctrl+O` dialog was detected by the `x64` hook as `msedge.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Edge real `<input type=file>` upload picker from a temporary local HTML page was detected by the `x64` hook as `msedge.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open and no file was selected.
- PASS: VS Code `Alt+F`, `o` dialog was detected by the `x64` hook as `Code.exe/#32770/Open File`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Antigravity `Ctrl+K Ctrl+O` dialog was detected by the `x64` hook as `Antigravity IDE.exe/#32770/Open Folder`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Audacity open dialog was detected by the `x64` hook as `Audacity.exe/#32770/Select one or more files`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: OpenSCAD open dialog was detected by the `x64` hook as `openscad.exe/#32770/Open File`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: OBS Studio import-profile dialog was detected by the `x64` hook as `obs64.exe/#32770/Import Profile`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Calibre file-dialog helper was detected by the `x64` hook as `calibre-file-dialog.exe/#32770/Choose a location for your new calibre e-book library`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Bambu Studio 3MF open dialog was detected by the `x64` hook as `bambu-studio.exe/#32770/选择一个文件（3mf）：`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: xTool Studio open dialog was detected by the `x64` hook as `xTool Studio.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Word browse-open dialog was detected by the `x64` hook as `WINWORD.EXE/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: Excel browse-open dialog was detected by the `x64` hook as `EXCEL.EXE/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open.
- PASS: MobaXterm private-key file picker was detected by the `x86` hook as `MobaXterm.exe/#32770/Open`; `JumpDialogToFolder` returned `Success`; dialog remained open at `%USERPROFILE%`.
- PASS: MobaXterm upload dialog was detected by the `x86` hook as `MobaXterm.exe/#32770/Choose which file(s) to upload...`; `JumpDialogToFolder` returned `Success`; dialog remained open at `%USERPROFILE%`; no file was selected or uploaded.
- NOTE: The first MobaXterm SSH attempt failed before SFTP could load because `%LOCALAPPDATA%\Temp\Mxt250\bin\mottynew.exe` was missing. Closing and restarting MobaXterm rebuilt `%LOCALAPPDATA%\Temp\Mxt250` and restored `MoTTYnew.exe`, after which the saved `10.0.0.1 (root)` session opened its SFTP browser and the upload button produced the verified dialog.
- NOTE: 7-Zip File Manager also exposed an unrelated `#32770/Copy` dialog during verification; diagnostics should prefer supported file/folder dialog titles when multiple `#32770` windows are visible.
- NOTE: Blender file browser exposed `blender.exe/GHOST_WindowClass/Blender File View`, not a supported foreground `#32770` dialog; host/script diagnostics now classify it as a custom file browser instead of a missing standard dialog.
- NOTE: Calibre Editor/Viewer/LRF Viewer remain unverified even though the main Calibre file-dialog helper is covered.
- NOTE: The first Edge real-upload attempts clicked behind a profile sync prompt or below the file input. Closing the prompt and keyboard-focusing the visible file input produced the verified upload picker.
- NOTE: Notepad Save As automation did not produce an active supported dialog in this run. Paint Save As is the standard Windows Save As representative.
- NOTE: Word browse-open is covered when it exposes the foreground `#32770/Open` dialog. Other Office/backstage entries remain classified separately from standard Win32 file-dialog coverage.

## Local App Inventory Classification

- Tested native/Win32 standard dialogs: 7-Zip, Audacity, Bambu Studio, Calibre, Notepad, OBS Studio, OpenSCAD, Paint, WordPad, Notepad++, Windows PowerShell ISE x86, VLC, xTool Studio.
- Tested browser/Electron standard dialogs: Chrome, Edge, Firefox, VS Code, Antigravity.
- Tested browser upload path: Chrome, Edge, and Firefox real upload pickers.
- Tested x86 dialogs: MobaXterm and Windows PowerShell ISE x86.
- Tested source providers: Explorer, Directory Opus, Total Commander.
- Equivalent Electron/Chromium candidates not separately required after VS Code/Antigravity/Chrome coverage: Cursor, Kiro, Postman, Cherry Studio, LM Studio, eufyMake Studio, Ollama.
- Custom-dialog or nonstandard-picker candidates to treat as unsupported until proven otherwise: Blender, Bambu Suite, Corel apps, Autodesk Fusion, RPCS3, Raspberry Pi Imager, Rufus, ScreenToGif.
- Office/backstage candidates not counted as standard hook coverage unless they expose a foreground `#32770` file dialog: Access, OneNote, Outlook, PowerPoint, Publisher, Sticky Notes, Send to OneNote.
- Excluded for safety or side effects: QPST/Qualcomm tools, Wilcom machine/recovery tools, firmware/download tools, disk/admin/system tools, uninstallers, Steam, Listary, tray/service helpers.

## Start Menu Inventory Snapshot

Inventory source: Start Menu `.lnk` targets under `%ProgramData%` and `%APPDATA%`, filtered to existing `.exe` targets on 2026-07-09.

- Directly tested target-dialog apps or represented by direct target-dialog tests: 7-Zip File Manager, Antigravity IDE, Audacity, Bambu Studio, Calibre, Excel, Firefox, Firefox Private Browsing, Firefox Profile Manager, Google Chrome, Microsoft Edge, MobaXterm Personal, Notepad, Notepad++, OBS Studio, OpenSCAD, Paint, VLC media player, Visual Studio Code, Windows PowerShell ISE (x86), Word, Wordpad, xTool Studio.
- Source-provider-only file managers: Directory Opus, Total Commander.
- Command-shell shortcuts without a reproducible foreground `#32770` file-dialog path in this run: Anaconda PowerShell Prompt, Anaconda Prompt, Command Prompt, Git Bash, Git GUI, Windows PowerShell, Windows PowerShell (x86), Windows PowerShell ISE.
- Equivalent Chromium/Electron candidates represented by Chrome/Edge/VS Code/Antigravity real file dialogs: Cherry Studio, Cursor, eufyMake Studio, Kiro, LM Studio, Ollama, Postman.
- Office/backstage candidates not counted as covered unless they expose a foreground `#32770` file dialog: Access, Office Language Preferences, OneNote, Outlook (classic), PowerPoint, Publisher, Send to OneNote, Sticky Notes (new).
- Custom media/design/developer apps not counted as covered unless they expose a standard foreground `#32770` dialog in a future run: Autodesk Fusion, Bambu Suite, Blender, Calibre E-Book Editor, Calibre E-Book Viewer, Calibre LRF Viewer, Corel CAPTURE 2018 (64-Bit), Corel CONNECT 2018 (64-Bit), Corel Font Manager 2018 (64-Bit), Corel PHOTO-PAINT 2018 (64-Bit), CorelDRAW 2018 (64-Bit), Internet Download Manager, Nsight Compute 2024.1.1, Nsight Monitor, Nsight Systems 2023.4.4, RPCS3, Raspberry Pi Imager, Rufus, ScreenToGif, Windows Fax and Scan, Windows Media Player.
- Admin/system/diagnostic/search apps excluded from hook quick switch acceptance: Administrative Tools, Character Map, CPU-Z, CrystalDiskInfo, CrystalDiskMark, dfrgui, Disk Cleanup, DS4Windows, Eraser, Everything, Hyper-V Manager, Immersive Control Panel, Internet Explorer, iSCSI Initiator, Listary, Magnify, Math Input Panel, Memory Diagnostics Tool, Narrator, ODBC Data Sources (32-bit), ODBC Data Sources (64-bit), On-Screen Keyboard, OneDrive, Quick Assist, RecoveryDrive, Registry Editor, Resource Monitor, Speech Recognition, Steps Recorder, System Configuration, System Information, Task Manager, VMCreate.
- Device/firmware/vendor tools excluded for side-effect risk: Duplexing Wizard (64-Bit), EFS Explorer, eMMC Software Download, Machine Manager e4.2, MemoryDebugApp, PDC, Purge Recovery, QCNView, QFIL, QPST Configuration, Report an Issue, Revert, RL Editor, Service Programming, Software Download, Wilcom EmbroideryStudio e4.2.
- Uninstallers and non-file-dialog launchers excluded: Steam, Uninstall, Uninstall IDM, Uninstall NVIDIA Nsight Compute 2024.1.1, Uninstall NVIDIA Nsight Systems 2023.4.4.

## Per-Target Invariants

- [x] The dialog remains open after the jump.
- [x] No file is opened or uploaded as a side effect of the jump.
- [x] The path is not visibly typed character-by-character.
- [x] Standard browser-owned `#32770` dialogs are accepted by dialog shape, not by process-name whitelist.
- [x] MobaXterm diagnostics from the `x86` host include architecture, class, and title for the observed upload dialog.

## Diagnostics To Capture

- Hook settings status for `x64` and `x86`.
- Target dialog process name, class, title, and architecture from hook active-dialog diagnostics.
- For Edge and VS Code, capture process name, architecture, dialog class, and title so nonstandard dialogs can be distinguished from normal `#32770` coverage.
- For MobaXterm, confirm the observed dialog log shows `architecture=x86`, `class=#32770`, and the upload dialog title.
- Any fallback/degradation message if native hook detection or jump acknowledgement fails.
