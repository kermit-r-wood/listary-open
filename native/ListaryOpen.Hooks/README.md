# ListaryOpen Native Hooks

MSVC is the preferred toolchain for Windows native hook binaries. The current shell does not have MSVC `link.exe` on `PATH`, so MSVC targets cannot be verified from this environment.

The current fallback uses the `stable-x86_64-pc-windows-gnullvm` Rust toolchain. Target-specific gnullvm linkers are configured in `.cargo/config.toml`:

- `x86_64-pc-windows-gnullvm`: `C:/Users/paulx/scoop/apps/mingw-mstorsjo-llvm-msvcrt/22.1.8-20260616/bin/x86_64-w64-mingw32-clang.exe`
- `i686-pc-windows-gnullvm`: `C:/Users/paulx/scoop/apps/mingw-mstorsjo-llvm-msvcrt/22.1.8-20260616/bin/i686-w64-mingw32-clang.exe`

These linker paths are machine-specific. Update `.cargo/config.toml` if Scoop upgrades `mingw-mstorsjo-llvm-msvcrt` or the install path changes.

Successful fallback verification commands:

```powershell
cargo build --locked -p listary_open_hook_host --target x86_64-pc-windows-gnullvm
cargo build --locked -p listary_open_hook --target x86_64-pc-windows-gnullvm
cargo build --locked -p listary_open_hook_host --target i686-pc-windows-gnullvm
cargo build --locked -p listary_open_hook --target i686-pc-windows-gnullvm
```

For gnullvm builds, both host EXEs and hook DLLs import `libunwind.dll`. Package the x64 runtime from `C:/Users/paulx/scoop/apps/mingw-mstorsjo-llvm-msvcrt/22.1.8-20260616/x86_64-w64-mingw32/bin/libunwind.dll` with `hooks/x64`, and package the i686 runtime from `C:/Users/paulx/scoop/apps/mingw-mstorsjo-llvm-msvcrt/22.1.8-20260616/i686-w64-mingw32/bin/libunwind.dll` with `hooks/x86`.

Before hook installation or packaging, verify binary imports and exports with `llvm-objdump`.

If MSVC Build Tools become available, later packaging can switch back to MSVC targets and avoid the gnullvm runtime DLL decision.
