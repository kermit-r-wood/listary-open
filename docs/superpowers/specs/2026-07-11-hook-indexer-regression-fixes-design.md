# Hook and Indexer Regression Fixes

## Goal

Fix three regressions introduced by the consensus-hardening change without removing its session-bound IPC security:

- MobaXterm upload dialogs must complete folder jumps through the native hook instead of reporting failure and falling back.
- Quick Switch must not remain degraded merely because the x86 child host needs longer than one connection timeout to become ready.
- NTFS fast indexing must not report enabled when the elevated helper bundle is unavailable.

## Design

### Dialog jump verification

Keep post-jump `CDM_GETFOLDERPATH` verification when the dialog supports it. Treat an unavailable/unsupported folder-path response differently from a confirmed path mismatch:

- matching returned path: success;
- different returned path: failure;
- no returned path after the address edit accepted the path and Enter was sent: success in compatibility mode.

This restores support for standard-shaped dialogs such as MobaXterm that accept address-bar navigation but do not implement `CDM_GETFOLDERPATH`. Unsupported window shapes and failed `WM_SETTEXT` calls remain failures.

### Hook host readiness

After launching a hook host, poll the existing health probe for a short bounded startup window instead of probing once. Use the current client and cancellation token; add no new protocol or background service. Both directly launched x64 and x64-launched x86 hosts use the same readiness helper.

If readiness is still unconfirmed when the window expires, retain the current degraded status and diagnostic message.

### NTFS fast indexing enablement

Make the enable action report whether the elevated helper bundle is available after UAC mode is enabled. The settings view model updates its enabled state only on success. On failure it remains disabled and exposes a concise unavailable-helper status rather than starting a misleading reindex.

Startup behavior remains unchanged: fast indexing is enabled automatically only when the helper bundle is usable.

## Error handling

- Cancellation during host readiness polling propagates through the existing cancellation path.
- Host probe failures remain transient until the bounded startup window expires.
- Missing NTFS helper files are reported as unavailable; no UAC prompt or indexing run starts.
- Existing hook fallback remains available for genuine native-hook failures.

## Tests

Add minimal regression tests before production changes:

1. Native hook status selection succeeds when navigation was sent but folder-path readback is unavailable, and still fails for a confirmed mismatch.
2. Hook enablement succeeds when a host health probe becomes ready after an initial failure.
3. Settings do not mark NTFS fast indexing enabled and do not request reindex when helper availability is false.

Run focused .NET tests, Rust tests where the MSVC linker is available, then the full .NET solution suite. Perform manual MobaXterm verification when the application is installed locally.

## Scope

No MobaXterm-specific adapter, IPC redesign, persistent settings change, or unrelated refactoring.
