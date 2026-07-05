#![windows_subsystem = "windows"]

use listary_open_hook_common::{
    JumpAckHeader, JumpCommandHeader, DIALOG_CLASS, HOOK_DLL_EXPORT, JUMP_ACK_COPYDATA_MAGIC,
    JUMP_ACK_STATUS_FAILED, JUMP_ACK_STATUS_SUCCESS, JUMP_ACK_STATUS_UNSUPPORTED_DIALOG,
    JUMP_COPYDATA_MAGIC,
};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::env;
use std::ffi::c_void;
use std::io;
use std::ptr::{null, null_mut};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{mpsc, Mutex, OnceLock};
use std::thread;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{
    CloseHandle, FreeLibrary, GetLastError, LocalFree, BOOL, ERROR_CLASS_ALREADY_EXISTS,
    ERROR_PIPE_CONNECTED, HANDLE, HMODULE, HWND, INVALID_HANDLE_VALUE, LPARAM, LRESULT, TRUE,
    WPARAM,
};
use windows_sys::Win32::Security::Authorization::{
    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
};
use windows_sys::Win32::Security::{
    GetTokenInformation, TokenUser, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES, TOKEN_QUERY,
    TOKEN_USER,
};
use windows_sys::Win32::Storage::FileSystem::{
    FlushFileBuffers, ReadFile, WriteFile, PIPE_ACCESS_DUPLEX,
};
use windows_sys::Win32::System::DataExchange::COPYDATASTRUCT;
use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress, LoadLibraryW};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE, PIPE_TYPE_BYTE,
    PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};
use windows_sys::Win32::System::SystemInformation::{
    IMAGE_FILE_MACHINE_AMD64, IMAGE_FILE_MACHINE_ARM64, IMAGE_FILE_MACHINE_I386,
    IMAGE_FILE_MACHINE_UNKNOWN,
};
use windows_sys::Win32::System::Threading::{
    GetCurrentProcess, IsWow64Process2, OpenProcess, OpenProcessToken, QueryFullProcessImageNameW,
    PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    ChangeWindowMessageFilterEx, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW,
    EnumChildWindows, EnumWindows, GetAncestor, GetClassNameW, GetDlgCtrlID, GetForegroundWindow,
    GetMessageW, GetWindowLongPtrW, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId,
    IsWindow, PeekMessageW, PostMessageW, PostQuitMessage, RegisterClassW, SendMessageTimeoutW,
    SetWindowLongPtrW, SetWindowsHookExW, TranslateMessage, UnhookWindowsHookEx,
    CHANGEFILTERSTRUCT, CREATESTRUCTW, GA_ROOT, GWLP_USERDATA, HHOOK, HWND_MESSAGE, MSG,
    MSGFLT_ALLOW, PM_REMOVE, SMTO_ABORTIFHUNG, WH_CALLWNDPROC, WM_APP, WM_COPYDATA, WM_DESTROY,
    WM_NCCREATE, WM_QUIT, WNDCLASSW,
};

const DEFAULT_PIPE_NAME: &str = pipe_name_for_pointer_width(usize::BITS);
const IPC_VERSION: u32 = 1;
const BUFFER_SIZE: usize = 64 * 1024;
const HOOK_RESCAN_INTERVAL: Duration = Duration::from_millis(500);
const HOOK_LOOP_SLEEP: Duration = Duration::from_millis(50);
const ADDRESS_BAR_EDIT_CONTROL_ID: i32 = 41477;
const ACK_WINDOW_CLASS: &str = "ListaryOpenHookAckWindow";
const ACK_STARTUP_ACCEPT_TIMEOUT: Duration = Duration::from_secs(2);
const ACK_STARTUP_SHUTDOWN_GRACE: Duration = Duration::from_millis(100);
const WM_LISTARY_ACK_CLOSE: u32 = WM_APP + 0x4C4F;

static NEXT_COMMAND_ID: AtomicU64 = AtomicU64::new(1);

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

const fn pipe_name_for_pointer_width(pointer_width: u32) -> &'static str {
    match pointer_width {
        32 => "listary-open-hook-x86",
        _ => "listary-open-hook-x64",
    }
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
    hooked_threads: HashSet<HookThreadKey>,
    logged_mobaxterm_threads: HashSet<u32>,
    hooks: HashMap<HookThreadKey, HHOOK>,
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
            logged_mobaxterm_threads: HashSet::new(),
            hooks: HashMap::new(),
        })
    }

    fn install_new_dialog_hooks(&mut self) {
        let dialogs = match discover_observed_dialogs() {
            Ok(dialogs) => dialogs,
            Err(error) => {
                eprintln!("Hook host dialog discovery failed: {error}");
                return;
            }
        };

        for dialog in &dialogs {
            self.log_dialog_diagnostics(dialog);
        }

        let threads = dialogs
            .iter()
            .filter(|dialog| {
                should_hook_observed_dialog(
                    host_architecture(),
                    dialog.architecture,
                    &dialog.class_name,
                    &dialog.title,
                    has_address_control(dialog.window_handle),
                )
            })
            .map(|dialog| HookThreadKey::new(dialog.process_id, dialog.thread_id))
            .collect::<HashSet<_>>();
        let pruned_hooks = prune_missing_hook_threads(
            &threads,
            &mut self.hooked_threads,
            &mut self.hooks,
            clear_confirmed_hook_thread,
        );
        for hook in pruned_hooks {
            unhook_thread_hook(hook);
        }

        for thread in unhooked_threads(&threads, &self.hooked_threads) {
            match install_thread_hook(self.module, self.hook_proc, thread.thread_id) {
                Ok(hook) => {
                    self.hooked_threads.insert(thread);
                    mark_hook_thread_confirmed(thread);
                    self.hooks.insert(thread, hook);
                    eprintln!(
                        "Hook host installed native dialog hook for thread {}.",
                        thread.thread_id
                    );
                }
                Err(error) => eprintln!(
                    "Hook host failed to hook thread {}: {error}",
                    thread.thread_id
                ),
            }
        }
    }

    fn log_dialog_diagnostics(&mut self, dialog: &ObservedDialog) {
        if !should_log_mobaxterm_dialog(host_architecture(), &dialog.process_name)
            || !self.logged_mobaxterm_threads.insert(dialog.thread_id)
        {
            return;
        }

        eprintln!(
            "Hook host observed MobaXterm dialog: architecture={}, class={}, title='{}'.",
            dialog.architecture, dialog.class_name, dialog.title
        );
    }
}

impl Drop for HookState {
    fn drop(&mut self) {
        for (_, hook) in self.hooks.drain() {
            unhook_thread_hook(hook);
        }

        for thread in self.hooked_threads.drain() {
            clear_confirmed_hook_thread(thread);
        }

        if !self.module.is_null() {
            unsafe {
                FreeLibrary(self.module);
            }
        }
    }
}

type DialogHookProc = unsafe extern "system" fn(i32, WPARAM, LPARAM) -> LRESULT;

fn unhook_thread_hook(hook: HHOOK) {
    let unhooked = unsafe { UnhookWindowsHookEx(hook) };
    if unhooked == 0 {
        eprintln!(
            "Hook host failed to unhook native dialog hook: {}",
            io::Error::last_os_error()
        );
    }
}

#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
struct HookThreadKey {
    process_id: u32,
    thread_id: u32,
}

impl HookThreadKey {
    fn new(process_id: u32, thread_id: u32) -> Self {
        Self {
            process_id,
            thread_id,
        }
    }
}

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

fn discover_observed_dialogs() -> io::Result<Vec<ObservedDialog>> {
    let mut dialogs = Vec::new();
    let enumerated = unsafe {
        EnumWindows(
            Some(enum_windows_proc),
            (&mut dialogs as *mut Vec<ObservedDialog>) as LPARAM,
        )
    };

    if enumerated == 0 {
        return Err(io::Error::last_os_error());
    }

    Ok(dialogs)
}

fn unhooked_threads<T>(discovered: &HashSet<T>, hooked_threads: &HashSet<T>) -> Vec<T>
where
    T: Copy + Eq + std::hash::Hash + Ord,
{
    let mut threads = discovered
        .iter()
        .copied()
        .filter(|thread| !hooked_threads.contains(thread))
        .collect::<Vec<_>>();
    threads.sort_unstable();
    threads
}

fn prune_missing_hook_threads<T, H, C>(
    discovered: &HashSet<T>,
    hooked_threads: &mut HashSet<T>,
    hooks: &mut HashMap<T, H>,
    mut clear_confirmed: C,
) -> Vec<H>
where
    T: Copy + Eq + std::hash::Hash + Ord,
    C: FnMut(T),
{
    let mut missing = hooked_threads
        .iter()
        .copied()
        .filter(|thread| !discovered.contains(thread))
        .collect::<Vec<_>>();
    missing.sort_unstable();

    let mut removed_hooks = Vec::new();
    for thread in &missing {
        hooked_threads.remove(thread);
        if let Some(hook) = hooks.remove(thread) {
            removed_hooks.push(hook);
        }
        clear_confirmed(*thread);
    }

    removed_hooks
}

fn confirmed_hook_threads() -> &'static Mutex<HashSet<HookThreadKey>> {
    static CONFIRMED_HOOK_THREADS: OnceLock<Mutex<HashSet<HookThreadKey>>> = OnceLock::new();
    CONFIRMED_HOOK_THREADS.get_or_init(|| Mutex::new(HashSet::new()))
}

fn mark_hook_thread_confirmed(thread: HookThreadKey) {
    let mut threads = confirmed_hook_threads()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    threads.insert(thread);
}

fn clear_confirmed_hook_thread(thread: HookThreadKey) {
    let mut threads = confirmed_hook_threads()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    threads.remove(&thread);
}

fn is_hook_thread_confirmed(thread: HookThreadKey) -> bool {
    confirmed_hook_threads()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .contains(&thread)
}

unsafe extern "system" fn enum_windows_proc(hwnd: HWND, l_param: LPARAM) -> BOOL {
    let Some(dialog) = observed_dialog(hwnd) else {
        return TRUE;
    };

    let dialogs = unsafe { &mut *(l_param as *mut Vec<ObservedDialog>) };
    dialogs.push(dialog);

    TRUE
}

fn is_supported_dialog_window(hwnd: HWND) -> bool {
    let Some(class_name) = class_name(hwnd) else {
        return false;
    };

    is_supported_dialog_shape(&class_name, &window_text(hwnd), has_address_control(hwnd))
}

fn is_supported_dialog_shape(class_name: &str, title: &str, has_address_control: bool) -> bool {
    class_name == DIALOG_CLASS
        && (has_address_control || title_contains_supported_dialog_keyword(title))
}

fn title_contains_supported_dialog_keyword(title: &str) -> bool {
    let title = title.to_ascii_lowercase();
    ["open", "upload", "choose", "folder"]
        .iter()
        .any(|keyword| title.contains(keyword))
}

fn has_address_control(hwnd: HWND) -> bool {
    find_address_edit_control(hwnd).is_some()
}

#[derive(Clone, Copy)]
struct AddressControlCandidate<'a> {
    hwnd: HWND,
    control_id: i32,
    class_name: &'a str,
}

fn select_address_edit_control<'a, I>(candidates: I) -> Option<HWND>
where
    I: IntoIterator<Item = AddressControlCandidate<'a>>,
{
    candidates
        .into_iter()
        .find(|candidate| {
            candidate.control_id == ADDRESS_BAR_EDIT_CONTROL_ID
                && candidate.class_name.eq_ignore_ascii_case("Edit")
        })
        .map(|candidate| candidate.hwnd)
}

struct AddressControlSearch {
    found_hwnd: HWND,
}

fn find_address_edit_control(hwnd: HWND) -> Option<HWND> {
    let mut search = AddressControlSearch {
        found_hwnd: null_mut(),
    };

    unsafe {
        EnumChildWindows(
            hwnd,
            Some(enum_address_edit_control_proc),
            (&mut search as *mut AddressControlSearch) as LPARAM,
        );
    }

    if search.found_hwnd.is_null() {
        None
    } else {
        Some(search.found_hwnd)
    }
}

unsafe extern "system" fn enum_address_edit_control_proc(hwnd: HWND, l_param: LPARAM) -> BOOL {
    if l_param == 0 {
        return TRUE;
    }

    let search = unsafe { &mut *(l_param as *mut AddressControlSearch) };
    let control_id = unsafe { GetDlgCtrlID(hwnd) };
    if control_id != ADDRESS_BAR_EDIT_CONTROL_ID {
        return TRUE;
    }

    let Some(class_name) = class_name(hwnd) else {
        return TRUE;
    };
    let candidate = AddressControlCandidate {
        hwnd,
        control_id,
        class_name: &class_name,
    };
    if let Some(address_edit) = select_address_edit_control([candidate]) {
        search.found_hwnd = address_edit;
        return 0;
    }

    TRUE
}

fn should_hook_observed_dialog(
    host_architecture: &str,
    dialog_architecture: &str,
    class_name: &str,
    title: &str,
    has_address_control: bool,
) -> bool {
    dialog_architecture.eq_ignore_ascii_case(host_architecture)
        && is_supported_dialog_shape(class_name, title, has_address_control)
}

fn should_report_active_dialog<C>(
    host_architecture: &str,
    dialog_architecture: &str,
    process_id: u32,
    thread_id: u32,
    is_thread_hook_confirmed: C,
) -> bool
where
    C: Fn(HookThreadKey) -> bool,
{
    dialog_architecture.eq_ignore_ascii_case(host_architecture)
        && is_thread_hook_confirmed(HookThreadKey::new(process_id, thread_id))
}

fn observed_dialog(hwnd: HWND) -> Option<ObservedDialog> {
    let mut class_buffer = [0u16; 64];
    let class_len =
        unsafe { GetClassNameW(hwnd, class_buffer.as_mut_ptr(), class_buffer.len() as i32) };

    if class_len <= 0 {
        return None;
    }

    let class_name = String::from_utf16_lossy(&class_buffer[..class_len as usize]);
    if class_name != DIALOG_CLASS {
        return None;
    }

    let mut process_id = 0u32;
    let thread_id = unsafe { GetWindowThreadProcessId(hwnd, &mut process_id) };
    if thread_id == 0 || process_id == 0 {
        return None;
    }

    let architecture = process_architecture(process_id)?;

    Some(ObservedDialog {
        dialog_id: dialog_id_for_window(process_id, hwnd),
        window_handle: hwnd,
        thread_id,
        process_id,
        process_name: process_name(process_id),
        class_name,
        title: window_text(hwnd),
        architecture,
    })
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
    let dialog = observed_dialog(hwnd)?;
    if !should_report_active_dialog(
        host_architecture(),
        dialog.architecture,
        dialog.process_id,
        dialog.thread_id,
        is_hook_thread_confirmed,
    ) {
        return None;
    }

    let payload = ActiveDialogPayload {
        dialog_id: dialog.dialog_id,
        window_handle: dialog.window_handle as usize,
        process_id: dialog.process_id,
        thread_id: dialog.thread_id,
        architecture: dialog.architecture,
        process_name: dialog.process_name,
        class_name: dialog.class_name,
        title: dialog.title,
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

    if !is_supported_dialog_window(hwnd) {
        return command_reply(
            "UnsupportedDialog",
            "Target window is not a supported standard file dialog.",
        );
    }

    if find_address_edit_control(hwnd).is_none() {
        return command_reply(
            "UnsupportedDialog",
            "Dialog does not expose the standard address edit control.",
        );
    }

    if let Err(failure) = jump_target_ready(
        hwnd,
        expected_process_id,
        thread_id,
        resolve_active_dialog_window(),
        is_hook_thread_confirmed,
    ) {
        return command_reply(failure.status, failure.message);
    }

    let command_id = next_command_id();
    let ack_receiver = match AckReceiver::new(command_id) {
        Ok(receiver) => receiver,
        Err(error) => {
            return command_reply(
                "Failed",
                &format!("Could not create hook acknowledgement receiver: {error}"),
            );
        }
    };

    let mut payload_bytes =
        build_jump_copydata_payload(ack_receiver.hwnd(), command_id, &payload.folder_path);
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
    let send_started = Instant::now();
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

    let ack = ack_receiver.wait(remaining_timeout(send_started, timeout_ms));
    let (status, message) = jump_status_for_ack(ack, is_live_window(hwnd));
    if status != "Success" {
        return command_reply(status, message);
    }

    if find_address_edit_control(hwnd).is_none() {
        return command_reply(
            "UnsupportedDialog",
            "Dialog address edit control disappeared after jump command was acknowledged.",
        );
    }

    if let Err(failure) = jump_target_ready(
        hwnd,
        expected_process_id,
        thread_id,
        resolve_active_dialog_window(),
        is_hook_thread_confirmed,
    ) {
        return command_reply(failure.status, failure.message);
    }

    command_reply(status, message)
}

fn resolve_active_dialog_window() -> Option<HWND> {
    let foreground = unsafe { GetForegroundWindow() };
    resolve_active_dialog_candidate(foreground, is_supported_dialog_window, |hwnd| unsafe {
        GetAncestor(hwnd, GA_ROOT)
    })
}

fn resolve_active_dialog_candidate<I, R>(
    foreground: HWND,
    is_dialog: I,
    root_window: R,
) -> Option<HWND>
where
    I: Fn(HWND) -> bool,
    R: Fn(HWND) -> HWND,
{
    if foreground.is_null() {
        return None;
    }

    if is_dialog(foreground) {
        return Some(foreground);
    }

    let root = root_window(foreground);
    if !root.is_null() && is_dialog(root) {
        return Some(root);
    }

    None
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

fn build_jump_copydata_payload(ack_hwnd: HWND, command_id: u64, folder_path: &str) -> Vec<u8> {
    let utf16 = folder_path
        .encode_utf16()
        .chain(Some(0))
        .collect::<Vec<_>>();
    let mut payload =
        Vec::with_capacity(std::mem::size_of::<JumpCommandHeader>() + utf16.len() * 2);
    payload.extend_from_slice(&JUMP_COPYDATA_MAGIC.to_ne_bytes());
    payload.extend_from_slice(&(ack_hwnd as usize).to_ne_bytes());
    payload.resize(align_up(payload.len(), std::mem::align_of::<u64>()), 0);
    payload.extend_from_slice(&command_id.to_ne_bytes());
    payload.extend_from_slice(&(utf16.len() as u32).to_ne_bytes());
    payload.resize(std::mem::size_of::<JumpCommandHeader>(), 0);
    for unit in utf16 {
        payload.extend_from_slice(&unit.to_ne_bytes());
    }

    payload
}

fn next_command_id() -> u64 {
    loop {
        let command_id = NEXT_COMMAND_ID.fetch_add(1, Ordering::Relaxed);
        if command_id != 0 {
            return command_id;
        }
    }
}

fn remaining_timeout(started: Instant, timeout_ms: u32) -> Duration {
    Duration::from_millis(timeout_ms as u64).saturating_sub(started.elapsed())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct CommandFailure {
    status: &'static str,
    message: &'static str,
}

fn jump_target_ready<C>(
    target_hwnd: HWND,
    target_process_id: u32,
    target_thread_id: u32,
    active_hwnd: Option<HWND>,
    is_thread_hook_confirmed: C,
) -> Result<(), CommandFailure>
where
    C: Fn(HookThreadKey) -> bool,
{
    match active_hwnd {
        Some(active_hwnd) if active_hwnd == target_hwnd => {}
        Some(_) => {
            return Err(CommandFailure {
                status: "TargetGone",
                message: "Dialog window is no longer the active foreground dialog.",
            });
        }
        None => {
            return Err(CommandFailure {
                status: "NoActiveDialog",
                message: "No active hook dialog.",
            });
        }
    }

    if !is_thread_hook_confirmed(HookThreadKey::new(target_process_id, target_thread_id)) {
        return Err(CommandFailure {
            status: "NoActiveDialog",
            message: "Target dialog hook has not been installed yet.",
        });
    }

    Ok(())
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum JumpAckStatus {
    Success,
    UnsupportedDialog,
    Failed,
}

fn jump_status_for_ack(
    ack: Option<JumpAckStatus>,
    target_alive: bool,
) -> (&'static str, &'static str) {
    match ack {
        Some(JumpAckStatus::Success) => ("Success", "Hook acknowledged folder jump."),
        Some(JumpAckStatus::UnsupportedDialog) => (
            "UnsupportedDialog",
            "Hook could not navigate this dialog shape.",
        ),
        Some(JumpAckStatus::Failed) => ("Failed", "Hook failed while navigating the dialog."),
        None if target_alive => ("Timeout", "Hook did not acknowledge jump command."),
        None => (
            "TargetGone",
            "Dialog closed before hook acknowledged jump command.",
        ),
    }
}

fn decode_jump_ack_payload(dw_data: usize, bytes: &[u8]) -> Option<(u64, JumpAckStatus)> {
    if dw_data != JUMP_ACK_COPYDATA_MAGIC {
        return None;
    }

    let header_size = std::mem::size_of::<JumpAckHeader>();
    let usize_size = std::mem::size_of::<usize>();
    let command_offset = align_up(usize_size, std::mem::align_of::<u64>());
    let status_offset = command_offset.checked_add(std::mem::size_of::<u64>())?;
    if bytes.len() != header_size || bytes.len() < status_offset + std::mem::size_of::<u32>() {
        return None;
    }

    let magic = usize::from_ne_bytes(bytes[..usize_size].try_into().ok()?);
    if magic != JUMP_ACK_COPYDATA_MAGIC {
        return None;
    }

    let command_id = u64::from_ne_bytes(bytes[command_offset..command_offset + 8].try_into().ok()?);
    let status_code = u32::from_ne_bytes(
        bytes[status_offset..status_offset + std::mem::size_of::<u32>()]
            .try_into()
            .ok()?,
    );
    let status = match status_code {
        JUMP_ACK_STATUS_SUCCESS => JumpAckStatus::Success,
        JUMP_ACK_STATUS_UNSUPPORTED_DIALOG => JumpAckStatus::UnsupportedDialog,
        JUMP_ACK_STATUS_FAILED => JumpAckStatus::Failed,
        _ => return None,
    };

    Some((command_id, status))
}

fn align_up(value: usize, alignment: usize) -> usize {
    debug_assert!(alignment.is_power_of_two());
    (value + alignment - 1) & !(alignment - 1)
}

struct AckReceiver {
    hwnd: HWND,
    receiver: mpsc::Receiver<JumpAckStatus>,
    thread: Option<thread::JoinHandle<()>>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum AckStartupControl {
    Accept,
    Cancel,
}

impl AckReceiver {
    fn new(command_id: u64) -> io::Result<Self> {
        let (ready_sender, ready_receiver) = mpsc::channel();
        let (ack_sender, ack_receiver) = mpsc::channel();
        let (startup_control_sender, startup_control_receiver) = mpsc::channel();
        let (finished_sender, finished_receiver) = mpsc::channel();
        let thread = thread::spawn(move || {
            ack_window_thread(
                command_id,
                ack_sender,
                ready_sender,
                startup_control_receiver,
            );
            let _ = finished_sender.send(());
        });

        let hwnd = wait_for_ack_window_startup(
            &ready_receiver,
            Duration::from_secs(2),
            &startup_control_sender,
            &finished_receiver,
            ACK_STARTUP_SHUTDOWN_GRACE,
        )? as HWND;

        Ok(Self {
            hwnd,
            receiver: ack_receiver,
            thread: Some(thread),
        })
    }

    fn hwnd(&self) -> HWND {
        self.hwnd
    }

    fn wait(&self, timeout: Duration) -> Option<JumpAckStatus> {
        if timeout.is_zero() {
            return self.receiver.try_recv().ok();
        }

        self.receiver.recv_timeout(timeout).ok()
    }
}

impl Drop for AckReceiver {
    fn drop(&mut self) {
        if !self.hwnd.is_null() {
            unsafe {
                PostMessageW(self.hwnd, WM_LISTARY_ACK_CLOSE, 0, 0);
            }
        }

        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

fn wait_for_ack_window_startup(
    ready_receiver: &mpsc::Receiver<Result<usize, String>>,
    startup_timeout: Duration,
    startup_control_sender: &mpsc::Sender<AckStartupControl>,
    finished_receiver: &mpsc::Receiver<()>,
    shutdown_grace: Duration,
) -> io::Result<usize> {
    match ready_receiver.recv_timeout(startup_timeout) {
        Ok(Ok(hwnd)) => {
            if startup_control_sender
                .send(AckStartupControl::Accept)
                .is_err()
            {
                let _ = finished_receiver.recv_timeout(shutdown_grace);
                return Err(io::Error::new(
                    io::ErrorKind::Other,
                    "ack window exited before startup could be accepted",
                ));
            }
            Ok(hwnd)
        }
        Ok(Err(error)) => {
            let _ = startup_control_sender.send(AckStartupControl::Cancel);
            let _ = finished_receiver.recv_timeout(shutdown_grace);
            Err(io::Error::new(io::ErrorKind::Other, error))
        }
        Err(mpsc::RecvTimeoutError::Timeout) => {
            let _ = startup_control_sender.send(AckStartupControl::Cancel);
            let _ = finished_receiver.recv_timeout(shutdown_grace);
            Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "ack window startup timed out",
            ))
        }
        Err(mpsc::RecvTimeoutError::Disconnected) => {
            let _ = startup_control_sender.send(AckStartupControl::Cancel);
            let _ = finished_receiver.recv_timeout(shutdown_grace);
            Err(io::Error::new(
                io::ErrorKind::Other,
                "ack window startup ended before reporting readiness",
            ))
        }
    }
}

struct AckWindowState {
    command_id: u64,
    sender: mpsc::Sender<JumpAckStatus>,
}

fn ack_window_thread(
    command_id: u64,
    ack_sender: mpsc::Sender<JumpAckStatus>,
    ready_sender: mpsc::Sender<Result<usize, String>>,
    startup_control_receiver: mpsc::Receiver<AckStartupControl>,
) {
    if startup_cancelled(&startup_control_receiver) {
        return;
    }

    let class_name = to_wide_null(ACK_WINDOW_CLASS);
    let instance = unsafe { GetModuleHandleW(null()) };
    if let Err(error) = register_ack_window_class(&class_name, instance) {
        let _ = ready_sender.send(Err(error));
        return;
    }

    if startup_cancelled(&startup_control_receiver) {
        return;
    }

    let state = Box::new(AckWindowState {
        command_id,
        sender: ack_sender,
    });
    let state_ptr = Box::into_raw(state);
    let hwnd = unsafe {
        CreateWindowExW(
            0,
            class_name.as_ptr(),
            class_name.as_ptr(),
            0,
            0,
            0,
            0,
            0,
            HWND_MESSAGE,
            null_mut(),
            instance,
            state_ptr.cast::<c_void>(),
        )
    };

    if hwnd.is_null() {
        unsafe {
            drop(Box::from_raw(state_ptr));
        }
        let _ = ready_sender.send(Err(format!(
            "CreateWindowExW failed for ack receiver: {}",
            io::Error::last_os_error()
        )));
        return;
    }

    if startup_cancelled(&startup_control_receiver) {
        unsafe {
            DestroyWindow(hwnd);
        }
        return;
    }

    allow_ack_copydata_message(hwnd);
    if ready_sender.send(Ok(hwnd as usize)).is_err() {
        unsafe {
            DestroyWindow(hwnd);
        }
        return;
    }

    if !wait_for_startup_acceptance(&startup_control_receiver, ACK_STARTUP_ACCEPT_TIMEOUT) {
        unsafe {
            DestroyWindow(hwnd);
        }
        return;
    }

    let mut message = unsafe { std::mem::zeroed::<MSG>() };
    while unsafe { GetMessageW(&mut message, null_mut(), 0, 0) } > 0 {
        unsafe {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}

fn startup_cancelled(receiver: &mpsc::Receiver<AckStartupControl>) -> bool {
    match receiver.try_recv() {
        Ok(AckStartupControl::Cancel) | Err(mpsc::TryRecvError::Disconnected) => true,
        Ok(AckStartupControl::Accept) => false,
        Err(mpsc::TryRecvError::Empty) => false,
    }
}

fn wait_for_startup_acceptance(
    receiver: &mpsc::Receiver<AckStartupControl>,
    timeout: Duration,
) -> bool {
    match receiver.recv_timeout(timeout) {
        Ok(AckStartupControl::Accept) => true,
        Ok(AckStartupControl::Cancel)
        | Err(mpsc::RecvTimeoutError::Timeout)
        | Err(mpsc::RecvTimeoutError::Disconnected) => false,
    }
}

fn register_ack_window_class(class_name: &[u16], instance: HMODULE) -> Result<(), String> {
    let window_class = WNDCLASSW {
        style: 0,
        lpfnWndProc: Some(ack_window_proc),
        cbClsExtra: 0,
        cbWndExtra: 0,
        hInstance: instance,
        hIcon: null_mut(),
        hCursor: null_mut(),
        hbrBackground: null_mut(),
        lpszMenuName: null(),
        lpszClassName: class_name.as_ptr(),
    };

    let registered = unsafe { RegisterClassW(&window_class) };
    if registered != 0 {
        return Ok(());
    }

    let error = unsafe { GetLastError() };
    if error == ERROR_CLASS_ALREADY_EXISTS {
        Ok(())
    } else {
        Err(format!(
            "RegisterClassW failed for ack receiver: {}",
            io::Error::from_raw_os_error(error as i32)
        ))
    }
}

fn allow_ack_copydata_message(hwnd: HWND) {
    let mut filter = CHANGEFILTERSTRUCT {
        cbSize: std::mem::size_of::<CHANGEFILTERSTRUCT>() as u32,
        ExtStatus: 0,
    };
    unsafe {
        ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, &mut filter);
    }
}

unsafe extern "system" fn ack_window_proc(
    hwnd: HWND,
    message: u32,
    w_param: WPARAM,
    l_param: LPARAM,
) -> LRESULT {
    match message {
        WM_NCCREATE => {
            let create = l_param as *const CREATESTRUCTW;
            if create.is_null() {
                return 0;
            }

            let state_ptr = unsafe { (*create).lpCreateParams as *mut AckWindowState };
            if state_ptr.is_null() {
                return 0;
            }

            unsafe {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, (state_ptr as isize) as _);
            }
            TRUE as LRESULT
        }
        WM_COPYDATA => {
            let state_ptr =
                unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut AckWindowState };
            if state_ptr.is_null() || l_param == 0 {
                return 0;
            }

            let copy_data = unsafe { &*(l_param as *const COPYDATASTRUCT) };
            if copy_data.lpData.is_null() || copy_data.cbData == 0 {
                return 0;
            }

            let bytes = unsafe {
                std::slice::from_raw_parts(copy_data.lpData.cast::<u8>(), copy_data.cbData as usize)
            };
            if let Some((command_id, status)) = decode_jump_ack_payload(copy_data.dwData, bytes) {
                let state = unsafe { &*state_ptr };
                if command_id == state.command_id {
                    let _ = state.sender.send(status);
                    return TRUE as LRESULT;
                }
            }

            0
        }
        WM_LISTARY_ACK_CLOSE => {
            unsafe {
                DestroyWindow(hwnd);
            }
            0
        }
        WM_DESTROY => {
            let state_ptr =
                unsafe { GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut AckWindowState };
            if !state_ptr.is_null() {
                unsafe {
                    SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                    drop(Box::from_raw(state_ptr));
                }
            }

            unsafe {
                PostQuitMessage(0);
            }
            0
        }
        _ => unsafe { DefWindowProcW(hwnd, message, w_param, l_param) },
    }
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

fn process_name(process_id: u32) -> String {
    query_process_image_path(process_id)
        .and_then(|path| process_name_from_image_path(&path))
        .unwrap_or_else(|| format!("pid-{process_id}"))
}

fn query_process_image_path(process_id: u32) -> Option<String> {
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, process_id) };
    if process.is_null() {
        return None;
    }

    let mut buffer = vec![0u16; 32768];
    let mut size = buffer.len() as u32;
    let queried = unsafe { QueryFullProcessImageNameW(process, 0, buffer.as_mut_ptr(), &mut size) };
    unsafe {
        CloseHandle(process);
    }

    if queried == 0 || size == 0 {
        return None;
    }

    Some(String::from_utf16_lossy(&buffer[..size as usize]))
}

fn process_name_from_image_path(path: &str) -> Option<String> {
    let trimmed = path.trim();
    if trimmed.is_empty() || trimmed.ends_with(['\\', '/']) {
        return None;
    }

    let name = trimmed.rsplit(['\\', '/']).next()?;
    if name.is_empty() {
        None
    } else {
        Some(name.to_string())
    }
}

fn should_log_mobaxterm_dialog(host_architecture: &str, process_name: &str) -> bool {
    host_architecture.eq_ignore_ascii_case("x86") && is_mobaxterm_process_name(process_name)
}

fn is_mobaxterm_process_name(process_name: &str) -> bool {
    let name = process_name.trim();
    let stem = name
        .strip_suffix(".exe")
        .or_else(|| name.strip_suffix(".EXE"))
        .unwrap_or(name);

    stem.eq_ignore_ascii_case("MobaXterm")
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
        let (mut security_attributes, _security_descriptor) = pipe_security_attributes()?;
        let handle = unsafe {
            CreateNamedPipeW(
                pipe_path.as_ptr(),
                PIPE_ACCESS_DUPLEX,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                BUFFER_SIZE as u32,
                BUFFER_SIZE as u32,
                0,
                &mut security_attributes,
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

struct OwnedSecurityDescriptor(PSECURITY_DESCRIPTOR);

impl Drop for OwnedSecurityDescriptor {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                LocalFree(self.0);
            }
        }
    }
}

struct OwnedHandle(HANDLE);

impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
}

struct OwnedLocalString(windows_sys::core::PWSTR);

impl Drop for OwnedLocalString {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                LocalFree(self.0.cast());
            }
        }
    }
}

fn pipe_security_descriptor_sddl_for_user(user_sid: &str) -> String {
    format!("D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGW;;;{user_sid})")
}

fn current_user_sid_string() -> io::Result<String> {
    let mut token = null_mut();
    let opened = unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) };
    if opened == 0 {
        return Err(io::Error::last_os_error());
    }

    let token = OwnedHandle(token);
    let mut token_length = 0u32;
    unsafe {
        GetTokenInformation(token.0, TokenUser, null_mut(), 0, &mut token_length);
    }
    if token_length == 0 {
        return Err(io::Error::last_os_error());
    }

    let mut token_buffer = vec![0u8; token_length as usize];
    let loaded = unsafe {
        GetTokenInformation(
            token.0,
            TokenUser,
            token_buffer.as_mut_ptr().cast(),
            token_length,
            &mut token_length,
        )
    };
    if loaded == 0 {
        return Err(io::Error::last_os_error());
    }

    let token_user = unsafe { &*(token_buffer.as_ptr().cast::<TOKEN_USER>()) };
    sid_to_string(token_user.User.Sid)
}

fn sid_to_string(sid: windows_sys::Win32::Security::PSID) -> io::Result<String> {
    let mut sid_string = null_mut();
    let converted = unsafe { ConvertSidToStringSidW(sid, &mut sid_string) };
    if converted == 0 {
        return Err(io::Error::last_os_error());
    }

    let sid_string = OwnedLocalString(sid_string);
    Ok(wide_null_ptr_to_string(sid_string.0))
}

fn wide_null_ptr_to_string(value: *const u16) -> String {
    if value.is_null() {
        return String::new();
    }

    let mut len = 0usize;
    unsafe {
        while *value.add(len) != 0 {
            len += 1;
        }

        String::from_utf16_lossy(std::slice::from_raw_parts(value, len))
    }
}

fn pipe_security_attributes() -> io::Result<(SECURITY_ATTRIBUTES, OwnedSecurityDescriptor)> {
    let user_sid = current_user_sid_string()?;
    let sddl = pipe_security_descriptor_sddl_for_user(&user_sid);
    let sddl = to_wide_null(&sddl);
    let mut security_descriptor: PSECURITY_DESCRIPTOR = null_mut();
    let converted = unsafe {
        ConvertStringSecurityDescriptorToSecurityDescriptorW(
            sddl.as_ptr(),
            1,
            &mut security_descriptor,
            null_mut(),
        )
    };
    if converted == 0 {
        return Err(io::Error::last_os_error());
    }

    let owned_security_descriptor = OwnedSecurityDescriptor(security_descriptor);
    let security_attributes = SECURITY_ATTRIBUTES {
        nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
        lpSecurityDescriptor: owned_security_descriptor.0,
        bInheritHandle: 0,
    };

    Ok((security_attributes, owned_security_descriptor))
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

struct ObservedDialog {
    dialog_id: String,
    window_handle: HWND,
    thread_id: u32,
    process_id: u32,
    process_name: String,
    class_name: String,
    title: String,
    architecture: &'static str,
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
    fn prune_missing_hook_threads_clears_confirmed_state_for_disappeared_threads() {
        let discovered = HashSet::from([7, 11]);
        let mut hooked = HashSet::from([42, 7]);
        let mut confirmed = HashSet::from([42, 7]);
        let mut hooks: std::collections::HashMap<i32, usize> = std::collections::HashMap::new();

        assert_eq!(
            Vec::<usize>::new(),
            prune_missing_hook_threads(&discovered, &mut hooked, &mut hooks, |thread_id| {
                confirmed.remove(&thread_id);
            })
        );

        assert_eq!(HashSet::from([7]), hooked);
        assert_eq!(HashSet::from([7]), confirmed);
    }

    #[test]
    fn reused_thread_id_is_not_confirmed_until_hook_is_reinstalled() {
        let previous = HookThreadKey::new(100, 42);
        let reused = HookThreadKey::new(200, 42);
        let mut hooked = HashSet::from([previous]);
        let mut confirmed = HashSet::from([previous]);
        let mut hooks: std::collections::HashMap<HookThreadKey, usize> =
            std::collections::HashMap::new();

        prune_missing_hook_threads(&HashSet::new(), &mut hooked, &mut hooks, |thread| {
            confirmed.remove(&thread);
        });

        let later_discovered = HashSet::from([reused]);
        assert_eq!(vec![reused], unhooked_threads(&later_discovered, &hooked));
        assert!(!should_report_active_dialog(
            "x64",
            "x64",
            200,
            42,
            |thread| { confirmed.contains(&thread) }
        ));

        hooked.insert(reused);
        confirmed.insert(reused);

        assert!(should_report_active_dialog(
            "x64",
            "x64",
            200,
            42,
            |thread| { confirmed.contains(&thread) }
        ));
    }

    #[test]
    fn prune_missing_hook_threads_removes_hook_handles_for_unhooking() {
        let kept = HookThreadKey::new(100, 7);
        let removed = HookThreadKey::new(200, 42);
        let discovered = HashSet::from([kept]);
        let mut hooked = HashSet::from([kept, removed]);
        let mut hooks = std::collections::HashMap::from([(kept, 700usize), (removed, 4200usize)]);
        let mut cleared = Vec::new();

        let removed_hooks =
            prune_missing_hook_threads(&discovered, &mut hooked, &mut hooks, |thread| {
                cleared.push(thread)
            });

        assert_eq!(vec![4200], removed_hooks);
        assert_eq!(HashSet::from([kept]), hooked);
        assert_eq!(std::collections::HashMap::from([(kept, 700usize)]), hooks);
        assert_eq!(vec![removed], cleared);
    }

    #[test]
    fn pipe_security_descriptor_allows_interactive_users() {
        let sddl = pipe_security_descriptor_sddl_for_user("S-1-5-21-1-2-3-1001");

        assert!(sddl.contains("(A;;GRGW;;;S-1-5-21-1-2-3-1001)"));
        assert!(!sddl.contains(";;;IU)"));
    }

    #[test]
    fn pipe_name_for_pointer_width_selects_matching_hook_host_pipe() {
        assert_eq!("listary-open-hook-x64", pipe_name_for_pointer_width(64));
        assert_eq!("listary-open-hook-x86", pipe_name_for_pointer_width(32));
    }

    #[test]
    fn active_dialog_reporting_requires_matching_architecture_and_confirmed_hook() {
        assert!(should_report_active_dialog(
            "x64",
            "x64",
            100,
            42,
            |thread| thread == HookThreadKey::new(100, 42)
        ));
        assert!(!should_report_active_dialog(
            "x64",
            "x86",
            100,
            42,
            |thread| thread == HookThreadKey::new(100, 42)
        ));
        assert!(!should_report_active_dialog("x64", "x64", 100, 42, |_| {
            false
        }));
    }

    #[test]
    fn jump_target_ready_requires_active_target_and_confirmed_hook() {
        let target = 0x1234usize as HWND;
        let other = 0x5678usize as HWND;

        assert_eq!(
            Ok(()),
            jump_target_ready(target, 100, 42, Some(target), |thread| {
                thread == HookThreadKey::new(100, 42)
            })
        );

        let inactive = jump_target_ready(target, 100, 42, Some(other), |_| true).unwrap_err();
        assert_eq!("TargetGone", inactive.status);
        assert_eq!(
            "Dialog window is no longer the active foreground dialog.",
            inactive.message
        );

        let missing_active = jump_target_ready(target, 100, 42, None, |_| true).unwrap_err();
        assert_eq!("NoActiveDialog", missing_active.status);
        assert_eq!("No active hook dialog.", missing_active.message);

        let unhooked = jump_target_ready(target, 100, 42, Some(target), |_| false).unwrap_err();
        assert_eq!("NoActiveDialog", unhooked.status);
        assert_eq!(
            "Target dialog hook has not been installed yet.",
            unhooked.message
        );
    }

    #[test]
    fn supported_dialog_shape_accepts_standard_file_dialog_titles() {
        assert!(is_supported_dialog_shape("#32770", "Open", false));
        assert!(is_supported_dialog_shape("#32770", "Open Folder", false));
        assert!(is_supported_dialog_shape("#32770", "File Upload", false));
        assert!(is_supported_dialog_shape(
            "#32770",
            "Choose which file(s) to upload...",
            false
        ));
    }

    #[test]
    fn supported_dialog_shape_accepts_address_control_without_process_whitelist() {
        assert!(is_supported_dialog_shape("#32770", "Firefox", true));
        assert!(is_supported_dialog_shape("#32770", "Untitled", true));
    }

    #[test]
    fn supported_dialog_shape_rejects_non_dialog_class_and_unknown_shape() {
        assert!(!is_supported_dialog_shape(
            "Chrome_WidgetWin_1",
            "Open",
            true
        ));
        assert!(!is_supported_dialog_shape("#32770", "Properties", false));
    }

    #[test]
    fn address_control_selector_prefers_edit_with_address_control_id() {
        let address_root = 101usize as HWND;
        let combo_ex = 102usize as HWND;
        let combo = 103usize as HWND;
        let edit = 104usize as HWND;

        let selected = select_address_edit_control([
            AddressControlCandidate {
                hwnd: address_root,
                control_id: ADDRESS_BAR_EDIT_CONTROL_ID,
                class_name: "Address Band Root",
            },
            AddressControlCandidate {
                hwnd: combo_ex,
                control_id: ADDRESS_BAR_EDIT_CONTROL_ID,
                class_name: "ComboBoxEx32",
            },
            AddressControlCandidate {
                hwnd: combo,
                control_id: ADDRESS_BAR_EDIT_CONTROL_ID,
                class_name: "ComboBox",
            },
            AddressControlCandidate {
                hwnd: edit,
                control_id: ADDRESS_BAR_EDIT_CONTROL_ID,
                class_name: "Edit",
            },
        ]);

        assert_eq!(Some(edit), selected);
    }

    #[test]
    fn address_control_selector_rejects_non_address_ids_and_classes() {
        assert_eq!(
            None,
            select_address_edit_control([
                AddressControlCandidate {
                    hwnd: 201usize as HWND,
                    control_id: ADDRESS_BAR_EDIT_CONTROL_ID,
                    class_name: "Button",
                },
                AddressControlCandidate {
                    hwnd: 202usize as HWND,
                    control_id: 42,
                    class_name: "Edit",
                },
            ])
        );
    }

    #[test]
    fn observed_dialog_hooking_requires_matching_host_architecture_and_supported_shape() {
        assert!(should_hook_observed_dialog(
            "x64",
            "x64",
            "#32770",
            "File Upload",
            false
        ));
        assert!(should_hook_observed_dialog(
            "x86", "x86", "#32770", "Firefox", true
        ));
        assert!(!should_hook_observed_dialog(
            "x64",
            "x86",
            "#32770",
            "File Upload",
            true
        ));
        assert!(!should_hook_observed_dialog(
            "x86",
            "x86",
            "#32770",
            "Properties",
            false
        ));
    }

    #[test]
    fn mobaxterm_diagnostics_are_emitted_only_by_x86_host() {
        assert!(should_log_mobaxterm_dialog("x86", "MobaXterm"));
        assert!(should_log_mobaxterm_dialog("x86", "MobaXterm.exe"));
        assert!(should_log_mobaxterm_dialog("x86", "mobaxterm.EXE"));
        assert!(!should_log_mobaxterm_dialog("x64", "MobaXterm.exe"));
        assert!(!should_log_mobaxterm_dialog("x86", "firefox.exe"));
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
    fn build_jump_copydata_payload_encodes_ack_command_header_and_null_terminated_utf16() {
        let payload =
            build_jump_copydata_payload(0x1234usize as HWND, 0xABCD_EF01_2345_6789, "C:\\Temp");
        let header_size = std::mem::size_of::<listary_open_hook_common::JumpCommandHeader>();
        assert!(payload.len() > header_size);

        let usize_size = std::mem::size_of::<usize>();
        let magic = usize::from_ne_bytes(payload[..usize_size].try_into().expect("payload magic"));
        let ack_hwnd = usize::from_ne_bytes(
            payload[usize_size..usize_size * 2]
                .try_into()
                .expect("ack hwnd"),
        );
        let command_offset = align_up(usize_size * 2, std::mem::align_of::<u64>());
        let command_id = u64::from_ne_bytes(
            payload[command_offset..command_offset + std::mem::size_of::<u64>()]
                .try_into()
                .expect("command id"),
        );
        let len_offset = command_offset + std::mem::size_of::<u64>();
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
        assert_eq!(0x1234, ack_hwnd);
        assert_eq!(0xABCD_EF01_2345_6789, command_id);
        assert_eq!(8, utf16_code_units);
        assert_eq!(
            "C:\\Temp".encode_utf16().chain(Some(0)).collect::<Vec<_>>(),
            encoded_path
        );
    }

    #[test]
    fn jump_status_for_ack_requires_explicit_ack() {
        assert_eq!(
            ("Timeout", "Hook did not acknowledge jump command."),
            jump_status_for_ack(None, true)
        );
        assert_eq!(
            (
                "TargetGone",
                "Dialog closed before hook acknowledged jump command."
            ),
            jump_status_for_ack(None, false)
        );
        assert_eq!(
            ("Success", "Hook acknowledged folder jump."),
            jump_status_for_ack(Some(JumpAckStatus::Success), true)
        );
        assert_eq!(
            (
                "UnsupportedDialog",
                "Hook could not navigate this dialog shape."
            ),
            jump_status_for_ack(Some(JumpAckStatus::UnsupportedDialog), true)
        );
        assert_eq!(
            ("Failed", "Hook failed while navigating the dialog."),
            jump_status_for_ack(Some(JumpAckStatus::Failed), true)
        );
    }

    #[test]
    fn decode_jump_ack_payload_accepts_magic_command_and_status() {
        let payload = ack_payload_bytes(0xABCD_EF01_2345_6789, JUMP_ACK_STATUS_SUCCESS);

        assert_eq!(
            Some((0xABCD_EF01_2345_6789, JumpAckStatus::Success)),
            decode_jump_ack_payload(JUMP_ACK_COPYDATA_MAGIC, &payload)
        );
        assert_eq!(None, decode_jump_ack_payload(0, &payload));
    }

    #[test]
    fn resolve_active_dialog_candidate_does_not_fallback_to_background_dialog() {
        let dialog = 100usize as HWND;
        let child = 101usize as HWND;
        let unrelated = 200usize as HWND;
        let background_dialog = 300usize as HWND;

        let is_dialog = |hwnd: HWND| hwnd == dialog || hwnd == background_dialog;
        let root = |hwnd: HWND| if hwnd == child { dialog } else { hwnd };

        assert_eq!(
            Some(dialog),
            resolve_active_dialog_candidate(dialog, is_dialog, root)
        );
        assert_eq!(
            Some(dialog),
            resolve_active_dialog_candidate(child, is_dialog, root)
        );
        assert_eq!(
            None,
            resolve_active_dialog_candidate(unrelated, is_dialog, root)
        );
        assert_eq!(
            None,
            resolve_active_dialog_candidate(null_mut(), is_dialog, root)
        );
    }

    #[test]
    fn process_name_from_image_path_returns_base_name() {
        assert_eq!(
            Some("notepad.exe".to_string()),
            process_name_from_image_path(r"C:\Windows\System32\notepad.exe")
        );
        assert_eq!(
            Some("app.exe".to_string()),
            process_name_from_image_path(r"\\server\share\folder\app.exe")
        );
        assert_eq!(None, process_name_from_image_path(""));
        assert_eq!(None, process_name_from_image_path(r"C:\Windows\System32\"));
    }

    #[test]
    fn ack_startup_timeout_signals_cancel_and_does_not_wait_for_thread_finish() {
        let (_ready_sender, ready_receiver) = mpsc::channel::<Result<usize, String>>();
        let (control_sender, control_receiver) = mpsc::channel::<AckStartupControl>();
        let (_finished_sender, finished_receiver) = mpsc::channel::<()>();

        let started = Instant::now();
        let result = wait_for_ack_window_startup(
            &ready_receiver,
            Duration::from_millis(1),
            &control_sender,
            &finished_receiver,
            Duration::from_millis(1),
        );

        let error = result.expect_err("startup wait should time out");
        assert_eq!(io::ErrorKind::TimedOut, error.kind());
        assert!(started.elapsed() < Duration::from_millis(100));
        assert_eq!(
            AckStartupControl::Cancel,
            control_receiver.try_recv().unwrap()
        );
    }

    #[test]
    fn ack_startup_ready_sends_acceptance_before_returning_window() {
        let (ready_sender, ready_receiver) = mpsc::channel::<Result<usize, String>>();
        let (control_sender, control_receiver) = mpsc::channel::<AckStartupControl>();
        let (_finished_sender, finished_receiver) = mpsc::channel::<()>();
        ready_sender.send(Ok(1234)).unwrap();

        let hwnd = wait_for_ack_window_startup(
            &ready_receiver,
            Duration::from_millis(1),
            &control_sender,
            &finished_receiver,
            Duration::from_millis(1),
        )
        .unwrap();

        assert_eq!(1234, hwnd);
        assert_eq!(
            AckStartupControl::Accept,
            control_receiver.try_recv().unwrap()
        );
    }

    fn ack_payload_bytes(command_id: u64, status: u32) -> Vec<u8> {
        let usize_size = std::mem::size_of::<usize>();
        let command_offset = align_up(usize_size, std::mem::align_of::<u64>());
        let mut payload = Vec::new();
        payload.extend_from_slice(&JUMP_ACK_COPYDATA_MAGIC.to_ne_bytes());
        payload.resize(command_offset, 0);
        payload.extend_from_slice(&command_id.to_ne_bytes());
        payload.extend_from_slice(&status.to_ne_bytes());
        payload.resize(std::mem::size_of::<JumpAckHeader>(), 0);
        payload
    }
}
