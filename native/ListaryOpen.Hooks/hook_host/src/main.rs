#![windows_subsystem = "windows"]

use listary_open_hook_common::{DIALOG_CLASS, HOOK_DLL_EXPORT};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::env;
use std::io;
use std::ptr::{null, null_mut};
use std::thread;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{
    CloseHandle, FreeLibrary, GetLastError, BOOL, ERROR_PIPE_CONNECTED, HANDLE, HMODULE, HWND,
    INVALID_HANDLE_VALUE, LPARAM, LRESULT, TRUE, WPARAM,
};
use windows_sys::Win32::Storage::FileSystem::{
    FlushFileBuffers, ReadFile, WriteFile, PIPE_ACCESS_DUPLEX,
};
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE,
    PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, EnumWindows, GetClassNameW, GetWindowThreadProcessId, PeekMessageW,
    SetWindowsHookExW, TranslateMessage, UnhookWindowsHookEx, HHOOK, MSG, PM_REMOVE,
    WH_CALLWNDPROC, WM_QUIT,
};

const DEFAULT_PIPE_NAME: &str = "listary-open-hook-x64";
const IPC_VERSION: u32 = 1;
const BUFFER_SIZE: usize = 64 * 1024;
const HOOK_RESCAN_INTERVAL: Duration = Duration::from_millis(500);
const HOOK_LOOP_SLEEP: Duration = Duration::from_millis(50);

fn main() -> io::Result<()> {
    let pipe_name = pipe_name_from_args();
    let pipe_path = format!(r"\\.\pipe\{}", pipe_name);
    start_hook_thread(arg_value("--dll"));

    println!("ListaryOpen hook host started on pipe '{}'.", pipe_name);

    loop {
        let pipe = NamedPipeHandle::create(&pipe_path)?;
        if let Err(error) = connect_pipe(pipe.raw()) {
            eprintln!("Hook host connect failed: {error}");
            continue;
        }

        thread::spawn(move || {
            if let Err(error) = serve_connection(pipe) {
                eprintln!("Hook host connection failed: {error}");
            }
        });
    }
}

fn pipe_name_from_args() -> String {
    arg_value("--pipe").unwrap_or_else(|| DEFAULT_PIPE_NAME.to_string())
}

fn arg_value(name: &str) -> Option<String> {
    arg_value_from(env::args(), name)
}

fn arg_value_from<I, S>(args: I, name: &str) -> Option<String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut args = args.into_iter();
    while let Some(arg) = args.next() {
        if arg.as_ref() == name {
            let value = args.next()?;
            if value.as_ref().starts_with("--") {
                return None;
            }

            return Some(value.as_ref().to_string());
        }
    }

    None
}

fn start_hook_thread(dll_path: Option<String>) {
    let Some(dll_path) = dll_path else {
        eprintln!("Hook host started without --dll; health IPC will run without native hooks.");
        return;
    };

    thread::spawn(move || run_hook_thread(&dll_path));
}

fn run_hook_thread(dll_path: &str) {
    match HookState::new(dll_path) {
        Ok(mut hook_state) => {
            hook_state.install_new_dialog_hooks();
            hook_loop(hook_state);
        }
        Err(error) => {
            eprintln!("Hook host native hook setup failed: {error}");
        }
    }
}

fn hook_loop(mut hook_state: HookState) {
    let mut next_scan = Instant::now() + HOOK_RESCAN_INTERVAL;
    loop {
        if !pump_pending_messages() {
            break;
        }

        if Instant::now() >= next_scan {
            hook_state.install_new_dialog_hooks();
            next_scan = Instant::now() + HOOK_RESCAN_INTERVAL;
        }

        thread::sleep(HOOK_LOOP_SLEEP);
    }
}

fn pump_pending_messages() -> bool {
    let mut message = unsafe { std::mem::zeroed::<MSG>() };
    while unsafe { PeekMessageW(&mut message, null_mut(), 0, 0, PM_REMOVE) } != 0 {
        if message.message == WM_QUIT {
            return false;
        }

        unsafe {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    true
}

struct HookState {
    module: HMODULE,
    hook_proc: DialogHookProc,
    hooked_threads: HashSet<u32>,
    hooks: Vec<HHOOK>,
}

impl HookState {
    fn new(dll_path: &str) -> Result<Self, String> {
        let module = load_hook_module(dll_path)?;
        let hook_proc = match hook_proc(module) {
            Ok(hook_proc) => hook_proc,
            Err(error) => {
                unsafe {
                    FreeLibrary(module);
                }
                return Err(error);
            }
        };

        Ok(Self {
            module,
            hook_proc,
            hooked_threads: HashSet::new(),
            hooks: Vec::new(),
        })
    }

    fn install_new_dialog_hooks(&mut self) {
        let threads = match discover_dialog_threads() {
            Ok(threads) => threads,
            Err(error) => {
                eprintln!("Hook host dialog discovery failed: {error}");
                return;
            }
        };

        for thread_id in unhooked_threads(&threads, &self.hooked_threads) {
            match install_thread_hook(self.module, self.hook_proc, thread_id) {
                Ok(hook) => {
                    self.hooked_threads.insert(thread_id);
                    self.hooks.push(hook);
                    eprintln!("Hook host installed native dialog hook for thread {thread_id}.");
                }
                Err(error) => eprintln!("Hook host failed to hook thread {thread_id}: {error}"),
            }
        }
    }
}

impl Drop for HookState {
    fn drop(&mut self) {
        for hook in self.hooks.drain(..) {
            let unhooked = unsafe { UnhookWindowsHookEx(hook) };
            if unhooked == 0 {
                eprintln!(
                    "Hook host failed to unhook native dialog hook: {}",
                    io::Error::last_os_error()
                );
            }
        }

        if !self.module.is_null() {
            unsafe {
                FreeLibrary(self.module);
            }
        }
    }
}

type DialogHookProc = unsafe extern "system" fn(i32, WPARAM, LPARAM) -> LRESULT;

fn load_hook_module(dll_path: &str) -> Result<HMODULE, String> {
    let wide_path = to_wide_null(dll_path);
    let module = unsafe { LoadLibraryW(wide_path.as_ptr()) };
    if module.is_null() {
        return Err(format!(
            "LoadLibraryW failed for '{dll_path}': {}",
            io::Error::last_os_error()
        ));
    }

    Ok(module)
}

fn hook_proc(module: HMODULE) -> Result<DialogHookProc, String> {
    let raw_proc = unsafe { GetProcAddress(module, HOOK_DLL_EXPORT.as_ptr()) };
    let Some(raw_proc) = raw_proc else {
        return Err(format!(
            "GetProcAddress failed for ListaryOpenHookProc: {}",
            io::Error::last_os_error()
        ));
    };

    Ok(unsafe {
        std::mem::transmute::<unsafe extern "system" fn() -> isize, DialogHookProc>(raw_proc)
    })
}

fn install_thread_hook(
    module: HMODULE,
    hook_proc: DialogHookProc,
    thread_id: u32,
) -> Result<HHOOK, String> {
    let hook = unsafe { SetWindowsHookExW(WH_CALLWNDPROC, Some(hook_proc), module, thread_id) };
    if hook.is_null() {
        return Err(io::Error::last_os_error().to_string());
    }

    Ok(hook)
}

fn discover_dialog_threads() -> io::Result<HashSet<u32>> {
    let mut threads = HashSet::new();
    let enumerated = unsafe {
        EnumWindows(
            Some(enum_windows_proc),
            (&mut threads as *mut HashSet<u32>) as LPARAM,
        )
    };

    if enumerated == 0 {
        return Err(io::Error::last_os_error());
    }

    Ok(threads)
}

fn unhooked_threads(discovered: &HashSet<u32>, hooked_threads: &HashSet<u32>) -> Vec<u32> {
    let mut thread_ids = discovered
        .iter()
        .copied()
        .filter(|thread_id| !hooked_threads.contains(thread_id))
        .collect::<Vec<_>>();
    thread_ids.sort_unstable();
    thread_ids
}

unsafe extern "system" fn enum_windows_proc(hwnd: HWND, l_param: LPARAM) -> BOOL {
    if is_dialog_window(hwnd) {
        let mut process_id = 0u32;
        let thread_id = unsafe { GetWindowThreadProcessId(hwnd, &mut process_id) };
        if thread_id != 0 {
            let threads = unsafe { &mut *(l_param as *mut HashSet<u32>) };
            threads.insert(thread_id);
        }
    }

    TRUE
}

fn is_dialog_window(hwnd: HWND) -> bool {
    let mut class_buffer = [0u16; 64];
    let class_len =
        unsafe { GetClassNameW(hwnd, class_buffer.as_mut_ptr(), class_buffer.len() as i32) };

    class_len > 0
        && DIALOG_CLASS
            .encode_utf16()
            .eq(class_buffer[..class_len as usize].iter().copied())
}

fn serve_connection(pipe: NamedPipeHandle) -> io::Result<()> {
    let response = match read_line(pipe.raw()) {
        Ok(request) => response_for_request(&request),
        Err(_) => command_reply("Failed", "Unknown command."),
    };

    let result = write_line(pipe.raw(), &response).and_then(|_| flush_pipe(pipe.raw()));
    unsafe {
        DisconnectNamedPipe(pipe.raw());
    }

    result
}

fn connect_pipe(handle: HANDLE) -> io::Result<()> {
    let connected = unsafe { ConnectNamedPipe(handle, null_mut()) };
    if connected == 0 {
        let error = unsafe { GetLastError() };
        if error != ERROR_PIPE_CONNECTED {
            return Err(io::Error::from_raw_os_error(error as i32));
        }
    }

    Ok(())
}

fn flush_pipe(handle: HANDLE) -> io::Result<()> {
    let flushed = unsafe { FlushFileBuffers(handle) };
    if flushed == 0 {
        return Err(io::Error::last_os_error());
    }

    Ok(())
}

fn read_line(handle: HANDLE) -> io::Result<String> {
    let mut request = Vec::new();
    let mut buffer = [0u8; 4096];
    let mut saw_newline = false;

    while request.len() < BUFFER_SIZE {
        let remaining = BUFFER_SIZE - request.len();
        let bytes_to_read = remaining.min(buffer.len()) as u32;
        let mut bytes_read = 0u32;
        let read = unsafe {
            ReadFile(
                handle,
                buffer.as_mut_ptr().cast(),
                bytes_to_read,
                &mut bytes_read,
                null_mut(),
            )
        };

        if read == 0 {
            let error = io::Error::last_os_error();
            if error.kind() == io::ErrorKind::BrokenPipe {
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "hook IPC request ended before newline",
                ));
            }

            return Err(error);
        }

        if bytes_read == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "hook IPC request ended before newline",
            ));
        }

        let chunk = &buffer[..bytes_read as usize];
        if let Some(newline) = chunk.iter().position(|byte| *byte == b'\n') {
            request.extend_from_slice(&chunk[..newline]);
            saw_newline = true;
            break;
        }

        request.extend_from_slice(chunk);
    }

    if !saw_newline {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "hook IPC request exceeded maximum line length",
        ));
    }

    if request.ends_with(b"\r") {
        request.pop();
    }

    String::from_utf8(request).map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))
}

fn write_line(handle: HANDLE, line: &str) -> io::Result<()> {
    let mut response = line.as_bytes().to_vec();
    response.push(b'\n');

    let mut total_written = 0usize;
    while total_written < response.len() {
        let mut bytes_written = 0u32;
        let remaining = response.len() - total_written;
        let bytes_to_write = remaining.min(u32::MAX as usize) as u32;
        let written = unsafe {
            WriteFile(
                handle,
                response[total_written..].as_ptr().cast(),
                bytes_to_write,
                &mut bytes_written,
                null_mut(),
            )
        };

        if written == 0 {
            return Err(io::Error::last_os_error());
        }

        if bytes_written == 0 {
            return Err(io::Error::new(
                io::ErrorKind::WriteZero,
                "failed to write hook IPC response",
            ));
        }

        total_written += bytes_written as usize;
    }

    Ok(())
}

fn response_for_request(request: &str) -> String {
    let envelope = serde_json::from_str::<IncomingEnvelope>(request);
    let Ok(envelope) = envelope else {
        return command_reply("Failed", "Unknown command.");
    };

    if envelope.version != IPC_VERSION {
        return command_reply("Failed", "Unsupported IPC version.");
    }

    match envelope.message_type.as_str() {
        "JumpDialogToFolder" => command_reply("NoActiveDialog", "No active hook dialog."),
        "HealthProbe" => command_reply("Success", "Hook host healthy."),
        _ => command_reply("Failed", "Unknown command."),
    }
}

fn command_reply(status: &str, message: &str) -> String {
    let envelope = OutgoingEnvelope {
        version: IPC_VERSION,
        message_type: "CommandReply",
        payload: CommandReplyPayload { status, message },
    };

    serde_json::to_string(&envelope).expect("hook IPC command reply serialization should not fail")
}

fn to_wide_null(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}

struct NamedPipeHandle(HANDLE);

// SAFETY: NamedPipeHandle owns one pipe HANDLE and ownership is moved to exactly
// one worker thread, which disconnects/closes it before drop.
unsafe impl Send for NamedPipeHandle {}

impl NamedPipeHandle {
    fn create(pipe_path: &str) -> io::Result<Self> {
        let pipe_path = to_wide_null(pipe_path);
        let handle = unsafe {
            CreateNamedPipeW(
                pipe_path.as_ptr(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                BUFFER_SIZE as u32,
                BUFFER_SIZE as u32,
                0,
                null(),
            )
        };

        if handle == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }

        Ok(Self(handle))
    }

    fn raw(&self) -> HANDLE {
        self.0
    }
}

impl Drop for NamedPipeHandle {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct IncomingEnvelope {
    version: u32,
    message_type: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct OutgoingEnvelope<'a> {
    version: u32,
    message_type: &'a str,
    payload: CommandReplyPayload<'a>,
}

#[derive(Serialize)]
struct CommandReplyPayload<'a> {
    status: &'a str,
    message: &'a str,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn arg_value_from_returns_value_after_named_option() {
        let args = [
            "ListaryOpen.HookHost.exe",
            "--pipe",
            "listary-open-hook-x64",
            "--dll",
            "C:\\ListaryOpen\\hooks\\x64\\ListaryOpen.Hook.dll",
        ];

        assert_eq!(
            Some("listary-open-hook-x64".to_string()),
            arg_value_from(args, "--pipe")
        );
        assert_eq!(
            Some("C:\\ListaryOpen\\hooks\\x64\\ListaryOpen.Hook.dll".to_string()),
            arg_value_from(args, "--dll")
        );
    }

    #[test]
    fn arg_value_from_returns_none_for_missing_or_unvalued_option() {
        assert_eq!(None, arg_value_from(["host.exe", "--dll"], "--dll"));
        assert_eq!(
            None,
            arg_value_from(["host.exe", "--dll", "--pipe", "pipe"], "--dll")
        );
        assert_eq!(
            None,
            arg_value_from(["host.exe", "--pipe", "pipe"], "--dll")
        );
    }

    #[test]
    fn unhooked_threads_returns_only_new_dialog_threads() {
        let discovered = HashSet::from([42, 7, 11, 5]);
        let hooked = HashSet::from([7, 11, 99]);

        assert_eq!(vec![5, 42], unhooked_threads(&discovered, &hooked));
    }
}
