# Hook Quick Switch Design

## Goal

Rework ListaryOpen quick switch so file dialogs are controlled through a Listary-like hook bridge instead of relying on UI Automation or visible address-bar keyboard fallback. The implementation must support both 64-bit and 32-bit target applications, and the current open dialogs in Antigravity, Notepad++, Chrome, Firefox, and MobaXterm must be hook-detected and quick-switch correctly.

## Confirmed Requirements

- `Ctrl+G` should automatically quick-switch the active supported file dialog to the best current folder candidate.
- The quick-switch success path must use hook-based dialog control.
- Support 64-bit targets: Antigravity, Notepad++, Chrome, Firefox, and other common 64-bit desktop applications.
- Support 32-bit targets: MobaXterm is the immediate acceptance target.
- Do not reintroduce a UIA strategy.
- Do not use the bottom file/folder name field as a fake path entry for folder switching.
- Keep the dialog open after navigation. Quick switch changes the dialog folder; it must not accidentally accept, upload, open, or close the dialog.
- Keep the app controller, search UI, indexing, and current quick-switch candidate selection in managed code.
- Put hook binaries and runtime data under the program directory, not under the user profile.
- Show clear status when hook support is disabled, unavailable, partially available, or falling back.

## Evidence From Local Investigation

Listary uses a hook architecture on this machine:

- It starts `Listary.Service.exe`, `ListaryHookHost32.exe`, and `ListaryHookHost64.exe`.
- It ships `Hooks\ListaryHook32-6.1.10.1.dll` and `Hooks\ListaryHook64-6.1.10.1.dll`.
- While Listary is running, its hook DLL is loaded into Antigravity, Notepad++, Firefox child processes, Chrome, and Explorer.
- The Listary service and hook hosts run elevated, while the visible app runs at normal integrity.

The target dialogs currently observed:

- Antigravity: `#32770`, title `Open Folder`, 64-bit.
- Notepad++: `#32770`, title `Open`, 64-bit.
- Chrome: `#32770`, title `Open`, 64-bit.
- Firefox: `#32770`, title `Open File`, 64-bit, owned by Firefox.
- MobaXterm: `#32770`, title `Choose which file(s) to upload...`, 32-bit.

This means a single 64-bit hook is not enough. A 32-bit hook host and 32-bit hook DLL are required for the accepted behavior.

## Architecture

### Managed App

`ListaryOpen.App` remains the owner of:

- hotkey registration
- search panel display
- quick-switch folder candidate selection
- settings/status UI
- deciding when to route `Ctrl+G` to quick switch versus search

It gets a new `IHookQuickSwitchBridge` dependency. The app calls this bridge before the existing external dialog automation fallback.

### Hook Bridge

Add a managed bridge in Infrastructure that owns IPC and state:

- Starts or connects to hook hosts.
- Maintains hook availability by architecture: `x64`, `x86`.
- Receives active dialog reports from hook hosts.
- Sends `JumpDialogToFolder` commands to the correct host based on dialog process bitness and UI thread.
- Exposes a simple app-facing API:
  - active hook dialog query
  - jump active dialog to folder
  - status text for Settings
  - health events for host crash, IPC timeout, architecture unavailable

The app should not know how hooks are installed. It only sees dialog capability and command results.

### Hook Hosts

Ship two host executables:

- `hooks\x64\ListaryOpen.HookHost.exe`
- `hooks\x86\ListaryOpen.HookHost.exe`

Each host loads only the matching hook DLL:

- `hooks\x64\ListaryOpen.Hook.dll`
- `hooks\x86\ListaryOpen.Hook.dll`

The host is responsible for:

- setting thread-targeted Win32 hooks for matching desktop GUI threads
- running a message pump
- loading the hook DLL into matching-bitness target processes
- receiving hook events from the injected DLL
- forwarding dialog context to the managed bridge
- executing jump commands in the target dialog thread

Use thread-targeted hooks when a candidate dialog thread is known. If broad discovery is needed, keep the broad hook scope limited to the current interactive desktop and to dialog-related events. The design does not require arbitrary process injection.

### Hook DLL

The hook DLL is intentionally small. It should:

- detect supported file dialogs and child controls
- report dialog metadata to the host
- execute a folder navigation command on a specific dialog
- return precise success/failure results

It must not own search, indexing, ranking, settings, or UI. It is a low-level dialog adapter only.

### IPC

Use named pipes between the managed bridge and hook hosts:

- one control pipe per architecture
- request/response commands for jump operations
- event stream for dialog discovered, dialog destroyed, active dialog changed, and health

Messages should be length-prefixed JSON or a small binary format with explicit versions. The first implementation can use JSON because message volume is low and correctness matters more than serialization performance.

## Quick Switch Flow

1. User presses `Ctrl+G`.
2. App collects the best quick-switch folder candidate from the existing providers.
3. App asks `IHookQuickSwitchBridge` for the active supported dialog.
4. If a hook-controlled dialog is active, the app sends `JumpDialogToFolder(dialogId, folderPath)`.
5. The matching host routes the command to the injected hook DLL in the target process/thread.
6. The hook changes the dialog folder using the dialog's real navigation controls.
7. The hook confirms the dialog is still open and reports success.
8. If hook control is unavailable or fails, the app may use the existing non-UIA fallback and must surface that fallback in status.

Acceptance for this feature requires step 4-7 to succeed for all target applications. Fallback success alone is not sufficient.

## Dialog Handling Rules

- Standard common dialogs are identified primarily by `#32770` plus child control shape.
- Firefox/Mozilla dialogs must be matched by actual dialog/control shape, not only title text.
- MobaXterm is a 32-bit acceptance case and must route through the 32-bit host.
- Do not write the target folder to control `1152` or other bottom file-name fields as a folder switch mechanism.
- Prefer the dialog's internal navigation command/address control when present.
- After navigation, verify the dialog window still exists and remains the foreground dialog or an owned active dialog.
- A jump result must include a reason when it fails: unsupported shape, stale dialog, access denied, host unavailable, hook unavailable, or timeout.

## Security And UAC

Hook support is opt-in from Settings.

High-integrity hook hosts are expected when the user wants ListaryOpen to control elevated applications or reliably install hooks in protected UI paths. Enabling hook support may show a UAC prompt.

The implementation must avoid stealth behavior:

- no hidden persistence outside the program directory
- no injection into non-GUI background processes
- no command execution in target processes beyond the dialog adapter
- no arbitrary file or network access from the hook DLL
- visible Settings status for enabled, disabled, elevated, partial, and error states

Publisher will remain `Unknown` until binaries are code-signed with a trusted certificate. A manifest description can make the UAC prompt clearer, but it cannot remove the `Unknown publisher` label.

## Build And Packaging

Current local toolchain state:

- Rust is available for `x86_64-pc-windows-msvc`.
- The 32-bit Rust target is not currently installed.
- MSVC linker/build tools for 32-bit native DLL output were not found on PATH.

The implementation plan must include a toolchain step for x86 output. The feature is not complete until both hook architectures build and are copied into the app output.

App output layout:

- `ListaryOpen.App.exe`
- `data\`
- `hooks\x64\ListaryOpen.HookHost.exe`
- `hooks\x64\ListaryOpen.Hook.dll`
- `hooks\x86\ListaryOpen.HookHost.exe`
- `hooks\x86\ListaryOpen.Hook.dll`

The app should report a partial hook state if x64 is available but x86 is missing. That is useful for diagnostics, but it does not satisfy the MobaXterm acceptance case.

## Error Handling

- Host not found: show hook unavailable and skip to fallback.
- Host startup denied or UAC canceled: show disabled for this session and skip to fallback.
- x86 host missing: mark 32-bit hook unavailable and fail MobaXterm acceptance.
- IPC timeout: cancel the jump, keep UI responsive, and show a precise status.
- Hook host crash: mark unhealthy, attempt one bounded restart, then disable for the session if it crashes again.
- Unsupported dialog shape: return unsupported without attempting a risky fallback inside the hook.
- Stale dialog handle: refresh active dialog state and return a retryable failure.

## Testing

Unit tests:

- process bitness routing selects x64 or x86 host correctly
- hook availability combines per-architecture health into Settings status
- active dialog routing chooses hook before external fallback
- fallback is used only when hook is unavailable or reports unsupported/failure
- command serialization/deserialization rejects unknown versions and malformed messages
- dialog result mapping preserves exact failure reasons

Native/IPC tests:

- host can start and respond to health probe
- bridge handles host exit and timeout
- command round trip works for both architecture channels where the local toolchain can build them

Manual acceptance tests:

- prove hook DLL is loaded into each target process using module inspection or a purpose-built verifier
- Antigravity open-folder dialog is discovered and jumps to a known Explorer folder
- Notepad++ open dialog is discovered and jumps
- Chrome upload/open dialog is discovered and jumps
- Firefox upload/open dialog is discovered and jumps
- MobaXterm upload dialog is discovered through the 32-bit hook and jumps
- after every jump, the dialog remains open and no file is accepted/uploaded/opened
- with hook disabled, the app reports disabled and uses the existing fallback path

## Non-Goals

- Do not clone all Listary features.
- Do not implement remote-path extraction for MobaXterm sessions.
- Do not replace the search/indexing architecture.
- Do not add regex search as part of this work.
- Do not use UIA as a hidden fallback.

## Acceptance Criteria

- The app builds with hook assets copied to the app output.
- Settings can enable hook support and show clear per-architecture health.
- `Ctrl+G` quick-switches the active dialog through hook control for Antigravity, Notepad++, Chrome, Firefox, and MobaXterm.
- Both x64 and x86 target processes are verified with loaded hook DLLs.
- The quick-switch path is responsive and does not visibly type a path into the dialog address bar.
- Dialogs remain open after quick switch.
- Automated tests cover bridge routing, architecture selection, fallback boundaries, and IPC message handling.
