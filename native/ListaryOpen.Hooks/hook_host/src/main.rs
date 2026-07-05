#![windows_subsystem = "windows"]

use listary_open_hook_common::{
    JumpCommandHeader, DIALOG_CLASS, HOOK_DLL_EXPORT, JUMP_COPYDATA_MAGIC,
};
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
use windows_sys::Win32::System::DataExchange::COPYDATASTRUCT;
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE,
    PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};
use windows_sys::Win32::System::SystemInformation::{
    IMAGE_FILE_MACHINE_AMD64, IMAGE_FILE_MACHINE_ARM64, IMAGE_FILE_MACHINE_I386,
    IMAGE_FILE_MACHINE_UNKNOWN,
};
use windows_sys::Win32::System::Threading::{
    IsWow64Process2, OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, EnumWindows, GetAncestor, GetClassNameW, GetDlgItem, GetForegroundWindow,
    GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsWindow, IsWindowVisible,
    PeekMessageW, SendMessageTimeoutW, SetWindowsHookExW, TranslateMessage, UnhookWindowsHookEx,
    GA_ROOT, HHOOK, MSG, PM_REMOVE, SMTO_ABORTIFHUNG, WH_CALLWNDPROC, WM_COPYDATA, WM_QUIT,
};

const DEFAULT_PIPE_NAME: &str = "listary-open-hook-x64";
const IPC_VERSION: u32 = 1;
const BUFFER_SIZE: usize = 64 * 1024;
const HOOK_RESCAN_INTERVAL: Duration = Duration::from_millis(500);
const HOOK_LOOP_SLEEP: Duration = Duration::from_millis(50);
const ADDRESS_BAR_EDIT_CONTROL_ID: i32 = 41477;

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
        "GetActiveDialog" => active_dialog_response()
            .unwrap_or_else(|| command_reply("NoActiveDialog", "No active hook dialog.")),
        "JumpDialogToFolder" => {
            let payload = serde_json::from_value::<JumpCommandPayload>(envelope.payload);
            match payload {
                Ok(payload) => jump_dialog_to_folder(payload),
                Err(_) => command_reply("Failed", "Invalid jump command payload."),
            }
        }
        "HealthProbe" => command_reply("Success", "Hook host healthy."),
        _ => command_reply("Failed", "Unknown command."),
    }
}

fn active_dialog_response() -> Option<String> {
    let hwnd = resolve_active_dialog_window()?;
    let mut process_id = 0u32;
    let thread_id = unsafe { GetWindowThreadProcessId(hwnd, &mut process_id) };
    if thread_id == 0 || process_id == 0 {
        return None;
    }

    let architecture = process_architecture(process_id)?;
    if architecture != host_architecture() {
        return None;
    }

    let payload = ActiveDialogPayload {
        dialog_id: dialog_id_for_window(process_id, hwnd),
        window_handle: hwnd as usize,
        process_id,
        thread_id,
        architecture,
        process_name: format!("pid-{process_id}"),
        class_name: class_name(hwnd).unwrap_or_else(|| DIALOG_CLASS.to_string()),
        title: window_text(hwnd),
    };

    let envelope = OutgoingEnvelope {
        version: IPC_VERSION,
        message_type: "ActiveDialog",
        payload,
    };

    Some(
        serde_json::to_string(&envelope)
            .expect("hook IPC active dialog serialization should not fail"),
    )
}

fn jump_dialog_to_folder(payload: JumpCommandPayload) -> String {
    if payload.timeout_ms < 0 || payload.folder_path.trim().is_empty() {
        return command_reply("Failed", "Invalid jump command payload.");
    }

    let Some((expected_process_id, hwnd)) = parse_dialog_id(&payload.dialog_id) else {
        return command_reply("Failed", "Invalid dialog id.");
    };

    if !is_live_window(hwnd) {
        return command_reply("TargetGone", "Dialog window is no longer available.");
    }

    let mut actual_process_id = 0u32;
    let thread_id = unsafe { GetWindowThreadProcessId(hwnd, &mut actual_process_id) };
    if thread_id == 0 || actual_process_id != expected_process_id {
        return command_reply(
            "TargetGone",
            "Dialog window no longer matches the requested target.",
        );
    }

    if !is_dialog_window(hwnd) {
        return command_reply(
            "UnsupportedDialog",
            "Target window is not a standard dialog.",
        );
    }

    let address_edit = unsafe { GetDlgItem(hwnd, ADDRESS_BAR_EDIT_CONTROL_ID) };
    if address_edit.is_null() {
        return command_reply(
            "UnsupportedDialog",
            "Dialog does not expose the standard address edit control.",
        );
    }

    let mut payload_bytes = build_jump_copydata_payload(&payload.folder_path);
    let Ok(cb_data) = u32::try_from(payload_bytes.len()) else {
        return command_reply("Failed", "Jump command payload is too large.");
    };

    let mut copy_data = COPYDATASTRUCT {
        dwData: JUMP_COPYDATA_MAGIC,
        cbData: cb_data,
        lpData: payload_bytes.as_mut_ptr().cast(),
    };
    let mut send_result = 0usize;
    let timeout_ms = payload.timeout_ms as u32;
    let sent = unsafe {
        SendMessageTimeoutW(
            hwnd,
            WM_COPYDATA,
            0,
            (&mut copy_data as *mut COPYDATASTRUCT) as LPARAM,
            SMTO_ABORTIFHUNG,
            timeout_ms,
            &mut send_result,
        )
    };

    if sent == 0 {
        return if is_live_window(hwnd) {
            command_reply("Timeout", "Timed out sending jump command to dialog.")
        } else {
            command_reply("TargetGone", "Dialog closed while sending jump command.")
        };
    }

    if !is_live_window(hwnd) {
        return command_reply("TargetGone", "Dialog closed after jump command was sent.");
    }

    if unsafe { GetDlgItem(hwnd, ADDRESS_BAR_EDIT_CONTROL_ID) }.is_null() {
        return command_reply(
            "UnsupportedDialog",
            "Dialog address edit control disappeared after jump command was sent.",
        );
    }

    command_reply(
        "Success",
        "Jump command sent to hook dialog; dialog remains open.",
    )
}

fn resolve_active_dialog_window() -> Option<HWND> {
    let foreground = unsafe { GetForegroundWindow() };
    if !foreground.is_null() {
        if let Some(hwnd) = resolve_dialog_from_window(foreground) {
            return Some(hwnd);
        }
    }

    first_top_level_dialog()
}

fn resolve_dialog_from_window(hwnd: HWND) -> Option<HWND> {
    if is_dialog_window(hwnd) {
        return Some(hwnd);
    }

    let root = unsafe { GetAncestor(hwnd, GA_ROOT) };
    if !root.is_null() && is_dialog_window(root) {
        return Some(root);
    }

    None
}

fn first_top_level_dialog() -> Option<HWND> {
    let mut found: HWND = null_mut();
    unsafe {
        EnumWindows(
            Some(enum_first_dialog_proc),
            (&mut found as *mut HWND) as LPARAM,
        );
    }

    if found.is_null() {
        None
    } else {
        Some(found)
    }
}

unsafe extern "system" fn enum_first_dialog_proc(hwnd: HWND, l_param: LPARAM) -> BOOL {
    if is_dialog_window(hwnd) && unsafe { IsWindowVisible(hwnd) } != 0 {
        let found = unsafe { &mut *(l_param as *mut HWND) };
        *found = hwnd;
        return 0;
    }

    TRUE
}

fn dialog_id_for_window(process_id: u32, hwnd: HWND) -> String {
    format!("{process_id}:{}", hwnd as usize)
}

fn parse_dialog_id(dialog_id: &str) -> Option<(u32, HWND)> {
    let mut parts = dialog_id.split(':');
    let process_id = parts.next()?.parse::<u32>().ok()?;
    let hwnd = parts.next()?.parse::<usize>().ok()? as HWND;
    if parts.next().is_some() || process_id == 0 || hwnd.is_null() {
        return None;
    }

    Some((process_id, hwnd))
}

fn build_jump_copydata_payload(folder_path: &str) -> Vec<u8> {
    let utf16 = folder_path
        .encode_utf16()
        .chain(Some(0))
        .collect::<Vec<_>>();
    let mut payload =
        Vec::with_capacity(std::mem::size_of::<JumpCommandHeader>() + utf16.len() * 2);
    payload.extend_from_slice(&JUMP_COPYDATA_MAGIC.to_ne_bytes());
    payload.extend_from_slice(&(utf16.len() as u32).to_ne_bytes());
    payload.resize(std::mem::size_of::<JumpCommandHeader>(), 0);
    for unit in utf16 {
        payload.extend_from_slice(&unit.to_ne_bytes());
    }

    payload
}

fn process_architecture(process_id: u32) -> Option<&'static str> {
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, process_id) };
    if process.is_null() {
        return None;
    }

    let mut process_machine = IMAGE_FILE_MACHINE_UNKNOWN;
    let mut native_machine = IMAGE_FILE_MACHINE_UNKNOWN;
    let queried =
        unsafe { IsWow64Process2(process, &mut process_machine, &mut native_machine) } != 0;
    unsafe {
        CloseHandle(process);
    }

    if !queried {
        return None;
    }

    match process_machine {
        IMAGE_FILE_MACHINE_UNKNOWN => architecture_from_machine(native_machine),
        machine => architecture_from_machine(machine),
    }
}

fn architecture_from_machine(machine: u16) -> Option<&'static str> {
    match machine {
        IMAGE_FILE_MACHINE_AMD64 | IMAGE_FILE_MACHINE_ARM64 => Some("x64"),
        IMAGE_FILE_MACHINE_I386 => Some("x86"),
        _ => None,
    }
}

fn host_architecture() -> &'static str {
    #[cfg(target_pointer_width = "64")]
    {
        "x64"
    }

    #[cfg(target_pointer_width = "32")]
    {
        "x86"
    }
}

fn class_name(hwnd: HWND) -> Option<String> {
    let mut class_buffer = [0u16; 256];
    let class_len =
        unsafe { GetClassNameW(hwnd, class_buffer.as_mut_ptr(), class_buffer.len() as i32) };
    if class_len <= 0 {
        return None;
    }

    Some(String::from_utf16_lossy(
        &class_buffer[..class_len as usize],
    ))
}

fn window_text(hwnd: HWND) -> String {
    let text_len = unsafe { GetWindowTextLengthW(hwnd) };
    if text_len <= 0 {
        return String::new();
    }

    let mut buffer = vec![0u16; text_len as usize + 1];
    let copied = unsafe { GetWindowTextW(hwnd, buffer.as_mut_ptr(), buffer.len() as i32) };
    if copied <= 0 {
        return String::new();
    }

    String::from_utf16_lossy(&buffer[..copied as usize])
}

fn is_live_window(hwnd: HWND) -> bool {
    !hwnd.is_null() && unsafe { IsWindow(hwnd) } != 0
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
    payload: serde_json::Value,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct OutgoingEnvelope<'a, T> {
    version: u32,
    message_type: &'a str,
    payload: T,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct JumpCommandPayload {
    dialog_id: String,
    folder_path: String,
    timeout_ms: i32,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct ActiveDialogPayload {
    dialog_id: String,
    window_handle: usize,
    process_id: u32,
    thread_id: u32,
    architecture: &'static str,
    process_name: String,
    class_name: String,
    title: String,
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

    #[test]
    fn parse_dialog_id_requires_process_id_and_hwnd() {
        assert_eq!(
            Some((42, 123456usize as HWND)),
            parse_dialog_id("42:123456")
        );
        assert_eq!(None, parse_dialog_id("42"));
        assert_eq!(None, parse_dialog_id("not-a-pid:123456"));
        assert_eq!(None, parse_dialog_id("42:not-a-window"));
        assert_eq!(None, parse_dialog_id("42:123456:extra"));
    }

    #[test]
    fn build_jump_copydata_payload_encodes_header_and_null_terminated_utf16() {
        let payload = build_jump_copydata_payload("C:\\Temp");
        let header_size = std::mem::size_of::<listary_open_hook_common::JumpCommandHeader>();
        assert!(payload.len() > header_size);

        let magic = usize::from_ne_bytes(
            payload[..std::mem::size_of::<usize>()]
                .try_into()
                .expect("payload contains usize magic"),
        );
        let len_offset = std::mem::size_of::<usize>();
        let utf16_code_units = u32::from_ne_bytes(
            payload[len_offset..len_offset + std::mem::size_of::<u32>()]
                .try_into()
                .expect("payload contains UTF-16 length"),
        );
        let encoded_path = payload[header_size..]
            .chunks_exact(2)
            .map(|chunk| u16::from_ne_bytes([chunk[0], chunk[1]]))
            .collect::<Vec<_>>();

        assert_eq!(listary_open_hook_common::JUMP_COPYDATA_MAGIC, magic);
        assert_eq!(8, utf16_code_units);
        assert_eq!(
            "C:\\Temp".encode_utf16().chain(Some(0)).collect::<Vec<_>>(),
            encoded_path
        );
    }
}
