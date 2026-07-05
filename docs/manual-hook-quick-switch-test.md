# Manual Hook Quick Switch Acceptance

Use this checklist to validate hook-based quick switch coverage for the Task 11 target applications. UI Automation is not part of this acceptance path; successful jumps should go through the native hook host and DLL for the dialog process architecture.

## Setup

- Build and run `ListaryOpen.App` with native hooks packaged.
- Confirm Settings reports hook quick switch enabled for both `x64` and `x86`.
- Use `C:\Users\paulx` as the target folder for each `Ctrl+G` jump.
- Keep the target file/open/upload dialog in the foreground before pressing `Ctrl+G`.
- If hook quick switch is disabled or a host is unavailable, verify the app reports disabled/unavailable state and any fallback is visibly degraded rather than silent native-hook success.

## Required Targets

- [ ] Antigravity open-folder dialog is detected by the `x64` hook and `Ctrl+G` jumps to `C:\Users\paulx`.
- [ ] Notepad++ open dialog is detected by the `x64` hook and `Ctrl+G` jumps to `C:\Users\paulx`.
- [ ] Chrome upload/open dialog is detected by the `x64` hook and `Ctrl+G` jumps to `C:\Users\paulx`.
- [ ] Firefox upload/open dialog is detected by the `x64` hook and `Ctrl+G` jumps to `C:\Users\paulx`.
- [ ] MobaXterm upload dialog is detected by the `x86` hook and `Ctrl+G` jumps to `C:\Users\paulx`.

## Per-Target Invariants

- [ ] The dialog remains open after the jump.
- [ ] No file is opened or uploaded as a side effect of the jump.
- [ ] The path is not visibly typed character-by-character.
- [ ] Standard browser-owned `#32770` dialogs are accepted by dialog shape, not by process-name whitelist.
- [ ] MobaXterm diagnostics from the `x86` host include architecture, class, and title for the observed upload dialog.

## Diagnostics To Capture

- Hook settings status for `x64` and `x86`.
- Target dialog process name, class, title, and architecture from hook active-dialog diagnostics.
- For MobaXterm, confirm the observed dialog log shows `architecture=x86`, `class=#32770`, and the upload dialog title.
- Any fallback/degradation message if native hook detection or jump acknowledgement fails.
