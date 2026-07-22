#![windows_subsystem = "windows"]

use listary_open_hook_common::{
    CleanupCommandHeader, JumpAckHeader, JumpCommandHeader, PreloadAcknowledgement,
    PreloadAuthorization, SpawnProof, CLEANUP_COPYDATA_MAGIC, DIALOG_CLASS, HOOK_DLL_EXPORT,
    JUMP_ACK_COPYDATA_MAGIC, JUMP_ACK_STATUS_FAILED, JUMP_ACK_STATUS_SUCCESS,
    JUMP_ACK_STATUS_UNSUPPORTED_DIALOG, JUMP_COPYDATA_MAGIC, PRELOAD_ACK_PENDING,
    PRELOAD_ACK_SUCCESS, SPAWN_PROOF_PENDING, SPAWN_PROOF_SUCCESS,
};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::env;
use std::ffi::c_void;
use std::io;
use std::process::{Child, Command};
use std::ptr::{null, null_mut};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicU8, Ordering};
use std::sync::{mpsc, Arc, Condvar, Mutex, OnceLock};
use std::thread;
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{
    CloseHandle, FreeLibrary, GetLastError, LocalFree, BOOL, ERROR_CLASS_ALREADY_EXISTS,
    ERROR_PIPE_CONNECTED, FALSE, HANDLE, HMODULE, HWND, INVALID_HANDLE_VALUE, LPARAM, LRESULT,
    TRUE, WAIT_FAILED, WAIT_OBJECT_0, WAIT_TIMEOUT, WPARAM,
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
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, Thread32First, Thread32Next,
    PROCESSENTRY32W, TH32CS_SNAPPROCESS, TH32CS_SNAPTHREAD, THREADENTRY32,
};
use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress, LoadLibraryW};
use windows_sys::Win32::System::Pipes::{
    ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, GetNamedPipeClientProcessId,
    PIPE_READMODE_BYTE, PIPE_TYPE_BYTE, PIPE_UNLIMITED_INSTANCES, PIPE_WAIT,
};
use windows_sys::Win32::System::SystemInformation::{
    IMAGE_FILE_MACHINE_AMD64, IMAGE_FILE_MACHINE_ARM64, IMAGE_FILE_MACHINE_I386,
    IMAGE_FILE_MACHINE_UNKNOWN,
};
use windows_sys::Win32::System::Threading::{
    GetCurrentProcess, IsWow64Process2, OpenProcess, OpenProcessToken, QueryFullProcessImageNameW,
    WaitForSingleObject, INFINITE, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
};
use windows_sys::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent, HWINEVENTHOOK};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    ChangeWindowMessageFilterEx, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW,
    EnumWindows, GetAncestor, GetClassNameW, GetForegroundWindow, GetMessageW, GetWindowLongPtrW,
    GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsWindow, IsWindowVisible,
    MsgWaitForMultipleObjectsEx, PeekMessageW, PostMessageW, PostQuitMessage, PostThreadMessageW,
    RegisterClassW, SendMessageTimeoutW, SetWindowLongPtrW, SetWindowsHookExW, TranslateMessage,
    UnhookWindowsHookEx, CHANGEFILTERSTRUCT, CREATESTRUCTW, EVENT_SYSTEM_DIALOGSTART,
    EVENT_SYSTEM_FOREGROUND, GA_ROOT, GWLP_USERDATA, HHOOK, HWND_MESSAGE, MSG, MSGFLT_ALLOW,
    MWMO_INPUTAVAILABLE, PM_REMOVE, QS_ALLINPUT, SMTO_ABORTIFHUNG, SMTO_BLOCK, WH_CALLWNDPROC,
    WH_GETMESSAGE, WINEVENT_OUTOFCONTEXT, WM_APP, WM_COPYDATA, WM_DESTROY, WM_NCCREATE, WM_NULL,
    WM_QUIT, WNDCLASSW,
};

const DEFAULT_PIPE_NAME: &str = pipe_name_for_pointer_width(usize::BITS);
const IPC_VERSION: u32 = 1;
const BUFFER_SIZE: usize = 64 * 1024;
const HOOK_RESCAN_INTERVAL: Duration = Duration::from_millis(500);
const HOOK_EVENT_RECOVERY_RESCAN_INTERVAL: Duration = Duration::from_secs(5);
const HOOK_PRELOAD_RESCAN_INTERVAL: Duration = Duration::from_millis(25);
const HOOK_TARGET_SESSION_RESCAN_INTERVAL: Duration = Duration::from_millis(100);
const HOOK_PRELOAD_BURST_DURATION: Duration = Duration::from_secs(3);
const HOOK_SHUTDOWN_POLL_INTERVAL: Duration = Duration::from_millis(50);
const HOOK_UNLOAD_QUEUE_TURN_GRACE: Duration = Duration::from_millis(50);
const ACK_WINDOW_CLASS: &str = "ListaryOpenHookAckWindow";
const ACK_STARTUP_ACCEPT_TIMEOUT: Duration = Duration::from_secs(2);
const ACK_STARTUP_SHUTDOWN_GRACE: Duration = Duration::from_millis(100);
const WM_LISTARY_ACK_CLOSE: u32 = WM_APP + 0x4C4F;
const PIPE_REJECT_REMOTE_CLIENTS: u32 = 0x0000_0008;
const PRELOAD_HOOK_DLL_EXPORT: &[u8] = b"ListaryOpenPreloadHookProc\0";

#[repr(C)]
struct FileTime {
    low: u32,
    high: u32,
}

#[link(name = "kernel32")]
extern "system" {
    fn CreateFileMappingW(
        file: HANDLE,
        attributes: *const c_void,
        protection: u32,
        maximum_size_high: u32,
        maximum_size_low: u32,
        name: *const u16,
    ) -> HANDLE;
    fn MapViewOfFile(
        mapping: HANDLE,
        desired_access: u32,
        file_offset_high: u32,
        file_offset_low: u32,
        bytes_to_map: usize,
    ) -> *mut c_void;
    fn OpenFileMappingW(access: u32, inherit_handle: i32, name: *const u16) -> HANDLE;
    fn GetProcessTimes(
        process: HANDLE,
        creation: *mut FileTime,
        exit: *mut FileTime,
        kernel: *mut FileTime,
        user: *mut FileTime,
    ) -> i32;
    fn UnmapViewOfFile(address: *const c_void) -> i32;
}

static NEXT_COMMAND_ID: AtomicU64 = AtomicU64::new(1);
static HOOK_RUNTIME_STATUS: AtomicU8 = AtomicU8::new(HookRuntimeStatus::Starting as u8);
static DIALOG_SCAN_REQUESTED: AtomicBool = AtomicBool::new(false);

fn confirmed_spawn_proofs() -> &'static Mutex<HashMap<u32, SpawnProof>> {
    static PROOFS: OnceLock<Mutex<HashMap<u32, SpawnProof>>> = OnceLock::new();
    PROOFS.get_or_init(|| Mutex::new(HashMap::new()))
}

fn spawn_proof_diagnostics() -> &'static Mutex<HashMap<u32, String>> {
    static DIAGNOSTICS: OnceLock<Mutex<HashMap<u32, String>>> = OnceLock::new();
    DIAGNOSTICS.get_or_init(|| Mutex::new(HashMap::new()))
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum HookRuntimeStatus {
    Starting = 0,
    Ready = 1,
    Failed = 2,
}

fn main() -> io::Result<()> {
    let session = HostSession::from_args()?;
    let pipe_name = pipe_name_from_args();
    let pipe_path = format!(r"\\.\pipe\{}", pipe_name);
    let _child_hosts = start_child_hosts(child_host_launches_from_args(env::args()), &session);
    let hook_shutdown = Arc::new(HookShutdown::new());
    start_hook_thread(
        arg_value("--dll"),
        arg_value("--preload-pid").and_then(|value| value.parse::<u32>().ok()),
        cleanup_authorization_token(&session.secret),
        hook_shutdown.clone(),
    );
    start_parent_monitor(session.parent_process_id, hook_shutdown.clone());

    println!("ListaryOpen hook host started on pipe '{}'.", pipe_name);

    loop {
        let pipe = NamedPipeHandle::create(&pipe_path)?;
        if let Err(error) = connect_pipe(pipe.raw()) {
            eprintln!("Hook host connect failed: {error}");
            continue;
        }

        let session = session.clone();
        let hook_shutdown = hook_shutdown.clone();
        thread::spawn(move || {
            if let Err(error) = serve_connection(pipe, &session, &hook_shutdown) {
                eprintln!("Hook host connection failed: {error}");
            }
        });
    }
}

#[derive(Clone, Debug)]
struct HostSession {
    parent_process_id: u32,
    secret: String,
}

impl HostSession {
    fn from_args() -> io::Result<Self> {
        let parent_process_id = arg_value("--parent-pid")
            .and_then(|value| value.parse::<u32>().ok())
            .filter(|value| *value != 0)
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "missing --parent-pid"))?;
        let secret = arg_value("--secret")
            .filter(|value| !value.trim().is_empty())
            .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "missing --secret"))?;

        Ok(Self {
            parent_process_id,
            secret,
        })
    }

    fn accepts_client(
        &self,
        actual_client_process_id: u32,
        claimed_client_process_id: u32,
        secret: &str,
    ) -> bool {
        actual_client_process_id == self.parent_process_id
            && claimed_client_process_id == self.parent_process_id
            && secret == self.secret
    }
}

fn cleanup_authorization_token(secret: &str) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for byte in secret.as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }
    if hash == 0 {
        1
    } else {
        hash
    }
}

fn start_parent_monitor(parent_process_id: u32, hook_shutdown: Arc<HookShutdown>) {
    thread::spawn(move || {
        let parent_exited = match open_process_for_exit_wait(parent_process_id) {
            Some(parent_process) => {
                let wait_result = unsafe { WaitForSingleObject(parent_process.0, INFINITE) };
                wait_result == WAIT_OBJECT_0 || wait_result == WAIT_FAILED
            }
            None => true,
        };

        if parent_exited {
            // The process-wide exit is intentionally delayed until HookState has
            // uninstalled every Windows hook and released the hook DLL.
            hook_shutdown.request_and_wait();
            std::process::exit(0);
        }
    });
}

struct HookShutdown {
    requested: AtomicBool,
    complete: Mutex<bool>,
    complete_signal: Condvar,
}

impl HookShutdown {
    fn new() -> Self {
        Self {
            requested: AtomicBool::new(false),
            complete: Mutex::new(false),
            complete_signal: Condvar::new(),
        }
    }

    fn is_requested(&self) -> bool {
        self.requested.load(Ordering::Acquire)
    }

    fn request(&self) {
        self.requested.store(true, Ordering::Release);
    }

    fn mark_complete(&self) {
        let mut complete = self
            .complete
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        *complete = true;
        self.complete_signal.notify_all();
    }

    fn request_and_wait(&self) {
        self.request();
        let mut complete = self
            .complete
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        while !*complete {
            complete = self
                .complete_signal
                .wait(complete)
                .unwrap_or_else(|poisoned| poisoned.into_inner());
        }
    }
}

fn open_process_for_exit_wait(process_id: u32) -> Option<OwnedHandle> {
    let process = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, 0, process_id) };
    (!process.is_null()).then_some(OwnedHandle(process))
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

#[derive(Clone, Debug, PartialEq, Eq)]
struct ChildHostLaunch {
    host_exe_path: String,
    pipe_name: String,
    dll_path: String,
}

fn child_host_launches_from_args<I, S>(args: I) -> Vec<ChildHostLaunch>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let args = args
        .into_iter()
        .map(|arg| arg.as_ref().to_string())
        .collect::<Vec<_>>();
    let mut launches = Vec::new();
    let mut index = 0;
    while index + 5 < args.len() {
        if args[index] == "--launch-host"
            && args[index + 2] == "--launch-pipe"
            && args[index + 4] == "--launch-dll"
            && !args[index + 1].starts_with("--")
            && !args[index + 3].starts_with("--")
            && !args[index + 5].starts_with("--")
        {
            launches.push(ChildHostLaunch {
                host_exe_path: args[index + 1].clone(),
                pipe_name: args[index + 3].clone(),
                dll_path: args[index + 5].clone(),
            });
            index += 6;
            continue;
        }

        index += 1;
    }

    launches
}

fn start_child_hosts(launches: Vec<ChildHostLaunch>, session: &HostSession) -> Vec<Child> {
    let mut children = Vec::new();
    for launch in launches {
        match Command::new(&launch.host_exe_path)
            .arg("--pipe")
            .arg(&launch.pipe_name)
            .arg("--dll")
            .arg(&launch.dll_path)
            .arg("--parent-pid")
            .arg(session.parent_process_id.to_string())
            .arg("--secret")
            .arg(&session.secret)
            .spawn()
        {
            Ok(child) => children.push(child),
            Err(error) => eprintln!(
                "Hook host failed to launch child host '{}': {error}",
                launch.host_exe_path
            ),
        }
    }

    children
}

fn start_hook_thread(
    dll_path: Option<String>,
    preload_process_id: Option<u32>,
    cleanup_authorization_token: u64,
    hook_shutdown: Arc<HookShutdown>,
) {
    set_hook_runtime_status(HookRuntimeStatus::Starting);
    let Some(dll_path) = dll_path else {
        set_hook_runtime_status(HookRuntimeStatus::Failed);
        eprintln!("Hook host started without --dll; health IPC will run without native hooks.");
        hook_shutdown.mark_complete();
        return;
    };

    thread::spawn(move || {
        run_hook_thread(
            &dll_path,
            preload_process_id,
            cleanup_authorization_token,
            &hook_shutdown,
        );
        // run_hook_thread returns only after hook_loop has dropped HookState.
        hook_shutdown.mark_complete();
    });
}

fn run_hook_thread(
    dll_path: &str,
    preload_process_id: Option<u32>,
    cleanup_authorization_token: u64,
    hook_shutdown: &HookShutdown,
) {
    match HookState::new(dll_path, preload_process_id, cleanup_authorization_token) {
        Ok(mut hook_state) => {
            hook_state.install_new_dialog_hooks();
            let scan_events = DialogScanEvents::install();
            set_hook_runtime_status(hook_state.runtime_status());
            hook_loop(hook_state, scan_events.is_active(), hook_shutdown);
            set_hook_runtime_status(HookRuntimeStatus::Failed);
        }
        Err(error) => {
            set_hook_runtime_status(HookRuntimeStatus::Failed);
            eprintln!("Hook host native hook setup failed: {error}");
        }
    }
}

fn set_hook_runtime_status(status: HookRuntimeStatus) {
    HOOK_RUNTIME_STATUS.store(status as u8, Ordering::Release);
}

fn hook_runtime_status() -> HookRuntimeStatus {
    match HOOK_RUNTIME_STATUS.load(Ordering::Acquire) {
        value if value == HookRuntimeStatus::Ready as u8 => HookRuntimeStatus::Ready,
        value if value == HookRuntimeStatus::Failed as u8 => HookRuntimeStatus::Failed,
        _ => HookRuntimeStatus::Starting,
    }
}

fn hook_loop(mut hook_state: HookState, event_scan_active: bool, hook_shutdown: &HookShutdown) {
    let rescan_interval = rescan_interval(event_scan_active);
    let mut preload_burst_until = Instant::now() + HOOK_PRELOAD_BURST_DURATION;
    let mut next_scan = Instant::now() + HOOK_PRELOAD_RESCAN_INTERVAL;
    loop {
        if hook_shutdown.is_requested() {
            break;
        }

        if !pump_pending_messages() {
            break;
        }

        let scan_requested = DIALOG_SCAN_REQUESTED.swap(false, Ordering::AcqRel);
        if scan_requested {
            preload_burst_until = Instant::now() + HOOK_PRELOAD_BURST_DURATION;
        }
        if scan_requested || Instant::now() >= next_scan {
            hook_state.install_new_dialog_hooks();
            let runtime_status = hook_state.runtime_status();
            set_hook_runtime_status(runtime_status);
            next_scan = Instant::now()
                + hook_scan_interval(
                    hook_state.preload_process_id.is_some(),
                    runtime_status == HookRuntimeStatus::Ready,
                    Instant::now() < preload_burst_until,
                    !hook_state.preload_authorizations.is_empty(),
                    rescan_interval,
                );
        }

        let timeout = wait_timeout_millis(
            next_scan
                .saturating_duration_since(Instant::now())
                .min(HOOK_SHUTDOWN_POLL_INTERVAL),
        );
        let wait_result = unsafe {
            MsgWaitForMultipleObjectsEx(0, null(), timeout, QS_ALLINPUT, MWMO_INPUTAVAILABLE)
        };
        if wait_result == WAIT_FAILED {
            // Preserve the original bounded retry behavior if message waiting is unavailable.
            thread::sleep(Duration::from_millis(50));
        } else {
            debug_assert!(wait_result == WAIT_OBJECT_0 || wait_result == WAIT_TIMEOUT);
        }
    }
}

fn rescan_interval(event_scan_active: bool) -> Duration {
    if event_scan_active {
        HOOK_EVENT_RECOVERY_RESCAN_INTERVAL
    } else {
        HOOK_RESCAN_INTERVAL
    }
}

fn hook_scan_interval(
    explicit_preload: bool,
    preload_ready: bool,
    preload_burst_active: bool,
    target_session_armed: bool,
    recovery_interval: Duration,
) -> Duration {
    if explicit_preload {
        return if preload_ready {
            HOOK_TARGET_SESSION_RESCAN_INTERVAL
        } else {
            HOOK_PRELOAD_RESCAN_INTERVAL
        };
    }
    if preload_burst_active {
        HOOK_PRELOAD_RESCAN_INTERVAL
    } else if target_session_armed {
        HOOK_TARGET_SESSION_RESCAN_INTERVAL
    } else {
        recovery_interval
    }
}

fn wait_timeout_millis(duration: Duration) -> u32 {
    if duration.is_zero() {
        return 0;
    }

    let millis = duration.as_millis().saturating_add(1);
    millis.min(u128::from(u32::MAX - 1)) as u32
}

struct DialogScanEvents {
    hooks: Vec<HWINEVENTHOOK>,
}

impl DialogScanEvents {
    fn install() -> Self {
        let mut hooks = Vec::with_capacity(2);
        for event in [EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_DIALOGSTART] {
            let hook = unsafe {
                SetWinEventHook(
                    event,
                    event,
                    null_mut(),
                    Some(dialog_scan_event),
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT,
                )
            };
            if !hook.is_null() {
                hooks.push(hook);
            }
        }

        Self { hooks }
    }

    fn is_active(&self) -> bool {
        !self.hooks.is_empty()
    }
}

impl Drop for DialogScanEvents {
    fn drop(&mut self) {
        for hook in self.hooks.drain(..) {
            unsafe {
                UnhookWinEvent(hook);
            }
        }
    }
}

unsafe extern "system" fn dialog_scan_event(
    _hook: HWINEVENTHOOK,
    _event: u32,
    _window: HWND,
    _object_id: i32,
    _child_id: i32,
    _event_thread: u32,
    _event_time: u32,
) {
    DIALOG_SCAN_REQUESTED.store(true, Ordering::Release);
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
    preload_hook_proc: DialogHookProc,
    hooked_threads: HashSet<HookThreadKey>,
    preloaded_threads: HashSet<HookThreadKey>,
    logged_mobaxterm_threads: HashSet<u32>,
    hooks: HashMap<HookThreadKey, HHOOK>,
    preload_hooks: HashMap<HookThreadKey, HHOOK>,
    preload_acknowledgements: HashMap<HookThreadKey, HANDLE>,
    preload_confirmed_threads: HashSet<HookThreadKey>,
    preload_authorizations: HashMap<u32, HANDLE>,
    preload_process_id: Option<u32>,
    cleanup_authorization_token: u64,
    lifecycle_threads: HashSet<HookThreadKey>,
}

impl HookState {
    fn new(
        dll_path: &str,
        preload_process_id: Option<u32>,
        cleanup_authorization_token: u64,
    ) -> Result<Self, String> {
        let module = load_hook_module(dll_path)?;
        let hook_proc = match load_hook_proc(module, HOOK_DLL_EXPORT) {
            Ok(hook_proc) => hook_proc,
            Err(error) => {
                unsafe {
                    FreeLibrary(module);
                }
                return Err(error);
            }
        };
        let preload_hook_proc = match load_hook_proc(module, PRELOAD_HOOK_DLL_EXPORT) {
            Ok(hook_proc) => hook_proc,
            Err(error) => {
                unsafe { FreeLibrary(module) };
                return Err(error);
            }
        };
        let mut state = Self {
            module,
            hook_proc,
            preload_hook_proc,
            hooked_threads: HashSet::new(),
            preloaded_threads: HashSet::new(),
            logged_mobaxterm_threads: HashSet::new(),
            hooks: HashMap::new(),
            preload_hooks: HashMap::new(),
            preload_acknowledgements: HashMap::new(),
            preload_confirmed_threads: HashSet::new(),
            preload_authorizations: HashMap::new(),
            preload_process_id,
            cleanup_authorization_token,
            lifecycle_threads: HashSet::new(),
        };
        if let Some(process_id) = preload_process_id {
            state.allow_preload_process_session(process_id);
        }
        Ok(state)
    }

    fn install_new_dialog_hooks(&mut self) {
        if self.preload_process_id.is_none() {
            if let Some(process_id) = foreground_root_process_id() {
                self.allow_preload_process_session(process_id);
            }
        }
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

        let explicit_session_threads = self
            .preload_process_id
            .map(|process_id| discover_process_session_threads(process_id, host_architecture()))
            .unwrap_or_default();
        let explicit_session_processes = explicit_session_threads
            .iter()
            .map(|thread| thread.process_id)
            .collect::<HashSet<_>>();
        let explicit_preload_threads = if self
            .preload_process_id
            .is_some_and(|process_id| process_name(process_id).eq_ignore_ascii_case("firefox"))
        {
            explicit_session_threads
                .iter()
                .copied()
                .filter(|thread| Some(thread.process_id) == self.preload_process_id)
                .collect::<HashSet<_>>()
        } else {
            explicit_session_threads.clone()
        };
        let observed_dialog_threads = dialogs
            .iter()
            .filter(|dialog| {
                should_hook_observed_dialog(
                    host_architecture(),
                    dialog.architecture,
                    &dialog.class_name,
                ) && (self.preload_process_id.is_none()
                    || explicit_session_processes.contains(&dialog.process_id))
            })
            .map(|dialog| HookThreadKey::new(dialog.process_id, dialog.thread_id))
            .collect::<HashSet<_>>();
        let mut threads = observed_dialog_threads.clone();
        if self.preload_process_id.is_none() {
            match discover_foreground_process_threads() {
                Ok(preload_threads) => threads.extend(preload_threads),
                Err(error) => eprintln!("Hook host foreground preload discovery failed: {error}"),
            }
        }
        threads.extend(explicit_preload_threads);
        let previously_hooked = self
            .hooked_threads
            .union(&self.preloaded_threads)
            .copied()
            .collect::<HashSet<_>>();
        threads.extend(live_hook_thread_keys(&previously_hooked));
        self.lifecycle_threads.extend(threads.iter().copied());
        for process_id in threads
            .iter()
            .map(|thread| thread.process_id)
            .collect::<HashSet<_>>()
        {
            self.allow_preload_process_session(process_id);
        }
        for child_process_id in threads
            .iter()
            .map(|thread| thread.process_id)
            .collect::<HashSet<_>>()
        {
            for parent_process_id in self.preload_authorizations.keys().copied() {
                match read_spawn_proof(
                    parent_process_id,
                    child_process_id,
                    self.cleanup_authorization_token,
                ) {
                    Ok(proof) => {
                        spawn_proof_diagnostics()
                            .lock()
                            .unwrap_or_else(|poisoned| poisoned.into_inner())
                            .remove(&child_process_id);
                        let mut proofs = confirmed_spawn_proofs()
                            .lock()
                            .unwrap_or_else(|poisoned| poisoned.into_inner());
                        if proofs.insert(child_process_id, proof).is_none() {
                            eprintln!(
                                "Hook host confirmed Firefox precapture proof parent={} child={} generation={} hook_before_resume=true captured_show=true.",
                                parent_process_id, child_process_id, proof.generation
                            );
                        }
                    }
                    Err(reason) => {
                        let diagnostic = format!(
                            "parent={parent_process_id} child={child_process_id}: {reason}"
                        );
                        let mut diagnostics = spawn_proof_diagnostics()
                            .lock()
                            .unwrap_or_else(|poisoned| poisoned.into_inner());
                        if diagnostics.get(&child_process_id) != Some(&diagnostic) {
                            eprintln!("Hook host Firefox spawn proof rejected: {diagnostic}");
                            diagnostics.insert(child_process_id, diagnostic);
                        }
                    }
                }
            }
        }
        let pruned_preload_hooks = prune_missing_hook_threads(
            &threads,
            &mut self.preloaded_threads,
            &mut self.preload_hooks,
            |_| {},
        );
        for hook in pruned_preload_hooks {
            unhook_thread_hook(hook);
        }
        self.preload_confirmed_threads
            .retain(|thread| threads.contains(thread));
        self.preload_acknowledgements.retain(|thread, mapping| {
            if threads.contains(thread) {
                true
            } else {
                unsafe { CloseHandle(*mapping) };
                false
            }
        });
        self.retire_confirmed_preload_hooks();
        for thread in unhooked_threads(&threads, &self.preloaded_threads)
            .into_iter()
            .filter(|thread| {
                !process_has_confirmed_precapture(
                    thread.process_id,
                    &self.preload_confirmed_threads,
                ) && !has_confirmed_spawn_proof(thread.process_id)
            })
        {
            match install_thread_hook(
                self.module,
                self.preload_hook_proc,
                thread.thread_id,
                WH_GETMESSAGE,
            ) {
                Ok(hook) => {
                    self.preloaded_threads.insert(thread);
                    self.preload_hooks.insert(thread, hook);
                }
                Err(error) => eprintln!(
                    "Hook host failed to install preload hook for thread {}: {error}",
                    thread.thread_id
                ),
            }
        }
        let pending_preload_threads = self
            .preloaded_threads
            .difference(&self.preload_confirmed_threads)
            .copied()
            .collect::<Vec<_>>();
        let mut posted_preload_threads = Vec::new();
        for thread in &pending_preload_threads {
            if !self.preload_acknowledgements.contains_key(thread) {
                if let Some(mapping) =
                    create_preload_acknowledgement(*thread, self.cleanup_authorization_token)
                {
                    self.preload_acknowledgements.insert(*thread, mapping);
                }
            }
            if let Some(mapping) = self.preload_acknowledgements.get(thread) {
                reset_preload_acknowledgement(*mapping, self.cleanup_authorization_token);
                if unsafe { PostThreadMessageW(thread.thread_id, WM_NULL, 0, 0) } != 0 {
                    posted_preload_threads.push(*thread);
                }
            }
        }
        let observed_dialog_processes = dialogs
            .iter()
            .map(|dialog| dialog.process_id)
            .collect::<HashSet<_>>();
        let acknowledgement_deadline = Instant::now() + Duration::from_millis(100);
        while !posted_preload_threads.is_empty() && Instant::now() < acknowledgement_deadline {
            for thread in &posted_preload_threads {
                if self.preload_confirmed_threads.contains(thread) {
                    continue;
                }
                let acknowledgement_succeeded = self
                    .preload_acknowledgements
                    .get(thread)
                    .is_some_and(|mapping| {
                        preload_acknowledgement_succeeded(
                            *mapping,
                            self.cleanup_authorization_token,
                        )
                    });
                let process_was_preloaded = has_confirmed_spawn_proof(thread.process_id)
                    || self
                        .preload_confirmed_threads
                        .iter()
                        .any(|confirmed| confirmed.process_id == thread.process_id);
                if acknowledgement_succeeded
                    && preload_confirmation_is_early(
                        process_was_preloaded,
                        observed_dialog_processes.contains(&thread.process_id),
                    )
                {
                    self.preload_confirmed_threads.insert(*thread);
                }
            }
            if posted_preload_threads
                .iter()
                .all(|thread| self.preload_confirmed_threads.contains(thread))
            {
                break;
            }
            thread::sleep(Duration::from_millis(5));
        }
        self.retire_confirmed_preload_hooks();
        let pruned_hooks = prune_missing_hook_threads(
            &observed_dialog_threads,
            &mut self.hooked_threads,
            &mut self.hooks,
            clear_confirmed_hook_thread,
        );
        for hook in pruned_hooks {
            unhook_thread_hook(hook);
        }

        for thread in unhooked_threads(&observed_dialog_threads, &self.hooked_threads) {
            match install_thread_hook(
                self.module,
                self.hook_proc,
                thread.thread_id,
                WH_CALLWNDPROC,
            ) {
                Ok(hook) => {
                    self.hooked_threads.insert(thread);
                    self.hooks.insert(thread, hook);
                    eprintln!(
                        "Hook host installed native hook for process {} thread {}.",
                        thread.process_id, thread.thread_id
                    );
                }
                Err(error) => eprintln!(
                    "Hook host failed to hook thread {}: {error}",
                    thread.thread_id
                ),
            }
        }

        for thread in self.hooked_threads.iter().copied().collect::<Vec<_>>() {
            if !is_hook_thread_confirmed(thread) {
                let has_precapture_proof = process_has_confirmed_precapture(
                    thread.process_id,
                    &self.preload_confirmed_threads,
                ) || has_confirmed_spawn_proof(thread.process_id);
                let callwnd_delivered = confirm_hook_thread_delivery(thread.thread_id);
                // Precapture proof is required for the direct COM path, but a
                // late-installed hook is still valid for the safe address-bar
                // fallback. Confirm the target hook from delivery itself.
                let ready = if has_precapture_proof {
                    target_hook_ready_after_preload(true, callwnd_delivered)
                } else {
                    callwnd_delivered
                };
                if ready {
                    mark_hook_thread_confirmed(thread);
                }
            }
        }
    }

    fn runtime_status(&self) -> HookRuntimeStatus {
        if explicit_preload_runtime_ready(self.preload_process_id, &self.preload_confirmed_threads)
        {
            HookRuntimeStatus::Ready
        } else {
            HookRuntimeStatus::Starting
        }
    }

    fn retire_confirmed_preload_hooks(&mut self) {
        let confirmed_processes = self
            .preload_confirmed_threads
            .iter()
            .map(|thread| thread.process_id)
            .collect::<HashSet<_>>();
        let retired_threads = self
            .preloaded_threads
            .iter()
            .copied()
            .filter(|thread| confirmed_processes.contains(&thread.process_id))
            .collect::<Vec<_>>();
        for thread in retired_threads {
            self.preloaded_threads.remove(&thread);
            if let Some(hook) = self.preload_hooks.remove(&thread) {
                unhook_thread_hook(hook);
            }
            if let Some(mapping) = self.preload_acknowledgements.remove(&thread) {
                unsafe { CloseHandle(mapping) };
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

    fn allow_preload_process_session(&mut self, process_id: u32) {
        let Some(root_process_id) = process_session_root_id(process_id) else {
            return;
        };
        self.allow_preload_process(root_process_id);
        if self.preload_process_id == Some(process_id) && process_id != root_process_id {
            // Firefox can hand the browser window from a short-lived launcher
            // to another firefox.exe process.  Bind explicit preload to that
            // actual window owner as well as the transient same-name root.
            self.allow_preload_process(process_id);
        }
    }

    fn allow_preload_process(&mut self, process_id: u32) {
        if self.preload_authorizations.contains_key(&process_id) {
            return;
        }
        const PAGE_READWRITE: u32 = 0x04;
        const FILE_MAP_WRITE: u32 = 0x0002;
        let name = to_wide_null(&preload_authorization_name(process_id));
        let mapping = unsafe {
            CreateFileMappingW(
                INVALID_HANDLE_VALUE,
                null(),
                PAGE_READWRITE,
                0,
                std::mem::size_of::<PreloadAuthorization>() as u32,
                name.as_ptr(),
            )
        };
        if mapping.is_null() {
            eprintln!(
                "Hook host could not authorize preload session root {}: {}",
                process_id,
                io::Error::last_os_error()
            );
        } else {
            let view = unsafe {
                MapViewOfFile(
                    mapping,
                    FILE_MAP_WRITE,
                    0,
                    0,
                    std::mem::size_of::<PreloadAuthorization>(),
                )
            };
            if view.is_null() {
                eprintln!(
                    "Hook host could not write preload authorization for root {}: {}",
                    process_id,
                    io::Error::last_os_error()
                );
                unsafe { CloseHandle(mapping) };
                return;
            }
            unsafe {
                *(view as *mut PreloadAuthorization) = PreloadAuthorization {
                    token: self.cleanup_authorization_token,
                    host_process_id: std::process::id(),
                    generation: self.cleanup_authorization_token as u32,
                };
                UnmapViewOfFile(view);
            }
            self.preload_authorizations.insert(process_id, mapping);
        }
    }
}

fn discover_process_session_threads(
    seed_process_id: u32,
    expected_architecture: &str,
) -> HashSet<HookThreadKey> {
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS | TH32CS_SNAPTHREAD, 0) };
    if snapshot == INVALID_HANDLE_VALUE {
        return HashSet::new();
    }
    let mut processes = Vec::new();
    let mut process = unsafe { std::mem::zeroed::<PROCESSENTRY32W>() };
    process.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
    let mut has_process = unsafe { Process32FirstW(snapshot, &mut process) } != 0;
    while has_process {
        let name_len = process
            .szExeFile
            .iter()
            .position(|unit| *unit == 0)
            .unwrap_or(process.szExeFile.len());
        processes.push(ProcessFamilyEntry {
            process_id: process.th32ProcessID,
            parent_process_id: process.th32ParentProcessID,
            executable_name: String::from_utf16_lossy(&process.szExeFile[..name_len]),
        });
        has_process = unsafe { Process32NextW(snapshot, &mut process) } != 0;
    }
    let Some((session_root, executable_name)) = session_process_root(seed_process_id, &processes)
    else {
        unsafe { CloseHandle(snapshot) };
        return HashSet::new();
    };
    let parent_pairs = processes
        .iter()
        .map(|process| (process.process_id, process.parent_process_id))
        .collect::<Vec<_>>();
    let mut process_ids = descendant_process_ids(session_root, &parent_pairs);
    process_ids.retain(|process_id| {
        processes.iter().any(|process| {
            process.process_id == *process_id
                && process
                    .executable_name
                    .eq_ignore_ascii_case(&executable_name)
        })
    });
    process_ids.retain(|process_id| {
        process_architecture(*process_id)
            .is_some_and(|architecture| architecture.eq_ignore_ascii_case(expected_architecture))
    });
    let threads = discover_threads_from_snapshot(snapshot, &process_ids);
    unsafe { CloseHandle(snapshot) };
    threads
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct ProcessFamilyEntry {
    process_id: u32,
    parent_process_id: u32,
    executable_name: String,
}

fn session_process_root(
    seed_process_id: u32,
    processes: &[ProcessFamilyEntry],
) -> Option<(u32, String)> {
    let seed = processes
        .iter()
        .find(|process| process.process_id == seed_process_id)?;
    let executable_name = seed.executable_name.clone();
    let mut root = seed;
    for _ in 0..processes.len() {
        let Some(parent) = processes.iter().find(|process| {
            process.process_id == root.parent_process_id
                && process
                    .executable_name
                    .eq_ignore_ascii_case(&executable_name)
        }) else {
            break;
        };
        if parent.process_id == root.process_id {
            break;
        }
        root = parent;
    }
    Some((root.process_id, executable_name))
}

fn process_session_root_id(seed_process_id: u32) -> Option<u32> {
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
    if snapshot == INVALID_HANDLE_VALUE {
        return None;
    }
    let mut processes = Vec::new();
    let mut process = unsafe { std::mem::zeroed::<PROCESSENTRY32W>() };
    process.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
    let mut has_process = unsafe { Process32FirstW(snapshot, &mut process) } != 0;
    while has_process {
        let name_len = process
            .szExeFile
            .iter()
            .position(|unit| *unit == 0)
            .unwrap_or(process.szExeFile.len());
        processes.push(ProcessFamilyEntry {
            process_id: process.th32ProcessID,
            parent_process_id: process.th32ParentProcessID,
            executable_name: String::from_utf16_lossy(&process.szExeFile[..name_len]),
        });
        has_process = unsafe { Process32NextW(snapshot, &mut process) } != 0;
    }
    unsafe { CloseHandle(snapshot) };
    session_process_root(seed_process_id, &processes).map(|(root, _)| root)
}

fn preload_authorization_name(root_process_id: u32) -> String {
    format!(
        "Local\\ListaryOpen.NativePreload.{}.{}",
        host_architecture(),
        root_process_id
    )
}

fn preload_acknowledgement_name(thread: HookThreadKey, generation: u32) -> String {
    format!(
        "Local\\ListaryOpen.PreloadAck.{}.{}.{}.{}",
        host_architecture(),
        thread.process_id,
        thread.thread_id,
        generation
    )
}

fn spawn_proof_name(parent_process_id: u32, child_process_id: u32, generation: u32) -> String {
    format!(
        "Local\\ListaryOpen.SpawnProof.{}.{}.{}.{}",
        host_architecture(),
        parent_process_id,
        child_process_id,
        generation
    )
}

fn read_spawn_proof(
    parent_process_id: u32,
    child_process_id: u32,
    authorization_token: u64,
) -> Result<SpawnProof, String> {
    const FILE_MAP_READ: u32 = 0x0004;
    let generation = authorization_token as u32;
    let name = to_wide_null(&spawn_proof_name(
        parent_process_id,
        child_process_id,
        generation,
    ));
    let mapping = unsafe { OpenFileMappingW(FILE_MAP_READ, 0, name.as_ptr()) };
    if mapping.is_null() {
        return Err(format!("mapping missing ({})", io::Error::last_os_error()));
    }
    let view = unsafe {
        MapViewOfFile(
            mapping,
            FILE_MAP_READ,
            0,
            0,
            std::mem::size_of::<SpawnProof>(),
        )
    };
    if view.is_null() {
        unsafe { CloseHandle(mapping) };
        return Err(format!(
            "mapping view failed ({})",
            io::Error::last_os_error()
        ));
    }
    let proof = unsafe { std::ptr::read_volatile(view as *const SpawnProof) };
    unsafe { UnmapViewOfFile(view) };
    let current_creation_time = process_creation_time(child_process_id);
    let rejection = spawn_proof_rejection(
        proof,
        authorization_token,
        parent_process_id,
        child_process_id,
        current_creation_time,
    );
    unsafe { CloseHandle(mapping) };
    rejection.map_or(Ok(proof), Err)
}

fn spawn_proof_rejection(
    proof: SpawnProof,
    authorization_token: u64,
    parent_process_id: u32,
    child_process_id: u32,
    current_creation_time: Option<u64>,
) -> Option<String> {
    if proof.status != SPAWN_PROOF_SUCCESS {
        return Some(format!(
            "status {} (pending={} means child factory hook ACK is not complete; other values failed)",
            proof.status,
            SPAWN_PROOF_PENDING,
        ));
    }
    if proof.token != authorization_token {
        return Some("token mismatch".to_string());
    }
    if proof.generation != authorization_token as u32 {
        return Some("generation mismatch".to_string());
    }
    if proof.parent_process_id != parent_process_id {
        return Some("parent mismatch".to_string());
    }
    if proof.child_process_id != child_process_id {
        return Some("child mismatch".to_string());
    }
    if proof.child_thread_id == 0 {
        return Some("primary child thread id is zero".to_string());
    }
    if proof.installed_before_resume == 0 {
        return Some("hookInstalledBeforeResume false".to_string());
    }
    if proof.captured_show_observed == 0 {
        return Some("captured Show has not been observed".to_string());
    }
    if current_creation_time != Some(proof.child_creation_time) {
        return Some(format!(
            "creationTime mismatch (proof={}, live={current_creation_time:?})",
            proof.child_creation_time
        ));
    }
    None
}

#[cfg(test)]
fn spawn_proof_matches(
    proof: SpawnProof,
    authorization_token: u64,
    parent_process_id: u32,
    child_process_id: u32,
    current_creation_time: Option<u64>,
) -> bool {
    spawn_proof_rejection(
        proof,
        authorization_token,
        parent_process_id,
        child_process_id,
        current_creation_time,
    )
    .is_none()
}

fn process_creation_time(process_id: u32) -> Option<u64> {
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, process_id) };
    if process.is_null() {
        return None;
    }
    let mut creation = FileTime { low: 0, high: 0 };
    let mut exit = FileTime { low: 0, high: 0 };
    let mut kernel = FileTime { low: 0, high: 0 };
    let mut user = FileTime { low: 0, high: 0 };
    let succeeded =
        unsafe { GetProcessTimes(process, &mut creation, &mut exit, &mut kernel, &mut user) } != 0;
    unsafe { CloseHandle(process) };
    succeeded.then_some((u64::from(creation.high) << 32) | u64::from(creation.low))
}

fn has_confirmed_spawn_proof(process_id: u32) -> bool {
    let mut proofs = confirmed_spawn_proofs()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let valid = proofs
        .get(&process_id)
        .is_some_and(|proof| process_creation_time(process_id) == Some(proof.child_creation_time));
    if !valid {
        proofs.remove(&process_id);
    }
    valid
}

fn create_preload_acknowledgement(
    thread: HookThreadKey,
    authorization_token: u64,
) -> Option<HANDLE> {
    const PAGE_READWRITE: u32 = 0x04;
    let generation = authorization_token as u32;
    let name = to_wide_null(&preload_acknowledgement_name(thread, generation));
    let mapping = unsafe {
        CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            null(),
            PAGE_READWRITE,
            0,
            std::mem::size_of::<PreloadAcknowledgement>() as u32,
            name.as_ptr(),
        )
    };
    if mapping.is_null() {
        return None;
    }
    reset_preload_acknowledgement(mapping, authorization_token);
    Some(mapping)
}

fn reset_preload_acknowledgement(mapping: HANDLE, authorization_token: u64) {
    const FILE_MAP_WRITE: u32 = 0x0002;
    let view = unsafe {
        MapViewOfFile(
            mapping,
            FILE_MAP_WRITE,
            0,
            0,
            std::mem::size_of::<PreloadAcknowledgement>(),
        )
    };
    if view.is_null() {
        return;
    }
    unsafe {
        *(view as *mut PreloadAcknowledgement) = PreloadAcknowledgement {
            token: authorization_token,
            status: PRELOAD_ACK_PENDING,
            host_handle: 0,
        };
        UnmapViewOfFile(view);
    }
}

fn preload_acknowledgement_succeeded(mapping: HANDLE, authorization_token: u64) -> bool {
    const FILE_MAP_READ: u32 = 0x0004;
    let view = unsafe {
        MapViewOfFile(
            mapping,
            FILE_MAP_READ,
            0,
            0,
            std::mem::size_of::<PreloadAcknowledgement>(),
        )
    };
    if view.is_null() {
        return false;
    }
    let acknowledgement = unsafe { *(view as *const PreloadAcknowledgement) };
    unsafe { UnmapViewOfFile(view) };
    preload_acknowledgement_matches(acknowledgement, authorization_token)
}

fn preload_acknowledgement_matches(
    acknowledgement: PreloadAcknowledgement,
    authorization_token: u64,
) -> bool {
    acknowledgement.token == authorization_token && acknowledgement.status == PRELOAD_ACK_SUCCESS
}

fn target_hook_ready_after_preload(
    preload_capture_confirmed: bool,
    callwnd_delivery_confirmed: bool,
) -> bool {
    preload_capture_confirmed && callwnd_delivery_confirmed
}

fn preload_confirmation_is_early(
    process_was_preloaded_before_dialog: bool,
    dialog_is_already_observed: bool,
) -> bool {
    process_was_preloaded_before_dialog || !dialog_is_already_observed
}

fn explicit_preload_runtime_ready(
    preload_process_id: Option<u32>,
    preload_confirmed_threads: &HashSet<HookThreadKey>,
) -> bool {
    preload_process_id.is_none_or(|process_id| {
        process_has_confirmed_precapture(process_id, preload_confirmed_threads)
    })
}

fn process_has_confirmed_precapture(
    process_id: u32,
    preload_confirmed_threads: &HashSet<HookThreadKey>,
) -> bool {
    preload_confirmed_threads
        .iter()
        .any(|thread| thread.process_id == process_id)
}

fn descendant_process_ids(root_process_id: u32, parents: &[(u32, u32)]) -> HashSet<u32> {
    let mut result = HashSet::from([root_process_id]);
    loop {
        let previous_len = result.len();
        for (process_id, parent_process_id) in parents {
            if result.contains(parent_process_id) {
                result.insert(*process_id);
            }
        }
        if result.len() == previous_len {
            return result;
        }
    }
}

fn discover_threads_from_snapshot(
    snapshot: HANDLE,
    process_ids: &HashSet<u32>,
) -> HashSet<HookThreadKey> {
    let mut result = HashSet::new();
    let mut thread = unsafe { std::mem::zeroed::<THREADENTRY32>() };
    thread.dwSize = std::mem::size_of::<THREADENTRY32>() as u32;
    let mut has_thread = unsafe { Thread32First(snapshot, &mut thread) } != 0;
    while has_thread {
        if process_ids.contains(&thread.th32OwnerProcessID) {
            result.insert(HookThreadKey::new(
                thread.th32OwnerProcessID,
                thread.th32ThreadID,
            ));
        }
        has_thread = unsafe { Thread32Next(snapshot, &mut thread) } != 0;
    }
    result
}

fn live_hook_thread_keys(candidates: &HashSet<HookThreadKey>) -> HashSet<HookThreadKey> {
    if candidates.is_empty() {
        return HashSet::new();
    }
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0) };
    if snapshot == INVALID_HANDLE_VALUE {
        return candidates.clone();
    }
    let process_ids = candidates
        .iter()
        .map(|thread| thread.process_id)
        .collect::<HashSet<_>>();
    let live = discover_threads_from_snapshot(snapshot, &process_ids);
    unsafe { CloseHandle(snapshot) };
    live.intersection(candidates).copied().collect()
}

fn discover_foreground_process_threads() -> io::Result<HashSet<HookThreadKey>> {
    let Some(foreground_process_id) = foreground_root_process_id() else {
        return Ok(HashSet::new());
    };
    Ok(discover_process_session_threads(
        foreground_process_id,
        host_architecture(),
    ))
}

fn foreground_root_process_id() -> Option<u32> {
    let foreground = unsafe { GetForegroundWindow() };
    if foreground.is_null() {
        return None;
    }
    let root = unsafe { GetAncestor(foreground, GA_ROOT) };
    let root = if root.is_null() { foreground } else { root };
    let mut process_id = 0u32;
    (unsafe { GetWindowThreadProcessId(root, &mut process_id) } != 0 && process_id != 0)
        .then_some(process_id)
}

fn confirm_hook_thread_delivery(thread_id: u32) -> bool {
    let Some(hwnd) = top_level_window_for_thread(thread_id) else {
        return false;
    };
    let mut result = 0usize;
    (unsafe {
        SendMessageTimeoutW(
            hwnd,
            WM_NULL,
            0,
            0,
            SMTO_ABORTIFHUNG | SMTO_BLOCK,
            1_000,
            &mut result,
        )
    }) != 0
}

impl Drop for HookState {
    fn drop(&mut self) {
        let dialog_hooks = self.hooks.drain().collect();
        let preload_hooks = self.preload_hooks.drain().collect();
        let confirmed_threads = self.hooked_threads.drain().collect();
        let preloaded_threads = self.preloaded_threads.drain().collect();
        let preload_authorizations = self.preload_authorizations.drain().collect::<Vec<_>>();
        let preload_acknowledgements = self
            .preload_acknowledgements
            .drain()
            .map(|(_, mapping)| mapping)
            .collect::<Vec<_>>();
        self.preload_confirmed_threads.clear();
        let lifecycle_threads = self.lifecycle_threads.drain().collect::<HashSet<_>>();
        let cleanup_authorization_token = self.cleanup_authorization_token;

        shutdown_hook_resources(
            dialog_hooks,
            preload_hooks,
            confirmed_threads,
            preloaded_threads,
            || {
                let cleanup_threads = live_hook_thread_keys(&lifecycle_threads)
                    .into_iter()
                    .collect::<Vec<_>>();
                if !cleanup_remote_dialog_captures(&cleanup_threads, cleanup_authorization_token) {
                    eprintln!("Hook host could not confirm remote dialog capture cleanup.");
                }
            },
            unhook_thread_hook,
            || {
                for mapping in preload_acknowledgements {
                    unsafe { CloseHandle(mapping) };
                }
                for (_, mapping) in preload_authorizations {
                    unsafe { CloseHandle(mapping) };
                }
            },
            clear_confirmed_hook_thread,
            drive_hook_threads_after_unhook,
        );
        confirmed_spawn_proofs()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clear();
        spawn_proof_diagnostics()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clear();
        if !self.module.is_null() {
            unsafe {
                FreeLibrary(self.module);
            }
        }
    }
}

fn cleanup_remote_dialog_captures(threads: &[HookThreadKey], authorization_token: u64) -> bool {
    let mut targets = threads.to_vec();
    targets.sort_unstable();
    let mut all_cleaned = true;
    let process_ids = targets
        .iter()
        .map(|target| target.process_id)
        .collect::<HashSet<_>>();
    for process_id in process_ids {
        let hwnd = targets
            .iter()
            .filter(|target| target.process_id == process_id)
            .find_map(|target| top_level_window_for_thread(target.thread_id));
        all_cleaned &= hwnd
            .map(|hwnd| send_capture_cleanup_command(hwnd, authorization_token))
            .unwrap_or(false);
    }
    all_cleaned
}

fn top_level_window_for_thread(thread_id: u32) -> Option<HWND> {
    struct Search {
        thread_id: u32,
        hwnd: HWND,
    }
    unsafe extern "system" fn callback(hwnd: HWND, l_param: LPARAM) -> BOOL {
        let search = unsafe { &mut *(l_param as *mut Search) };
        if unsafe { GetWindowThreadProcessId(hwnd, null_mut()) } == search.thread_id {
            search.hwnd = hwnd;
            return FALSE;
        }
        TRUE
    }

    let mut search = Search {
        thread_id,
        hwnd: null_mut(),
    };
    unsafe {
        EnumWindows(
            Some(callback),
            (&mut search as *mut Search).cast::<c_void>() as LPARAM,
        );
    }
    (!search.hwnd.is_null()).then_some(search.hwnd)
}

fn send_capture_cleanup_command(hwnd: HWND, authorization_token: u64) -> bool {
    let command_id = next_command_id();
    let mut target_process_id = 0u32;
    if unsafe { GetWindowThreadProcessId(hwnd, &mut target_process_id) } == 0 {
        return false;
    }
    let Ok(ack_receiver) = AckReceiver::new(command_id, target_process_id) else {
        return false;
    };
    let mut payload =
        build_cleanup_copydata_payload(ack_receiver.hwnd(), command_id, authorization_token);
    let mut copy_data = COPYDATASTRUCT {
        dwData: CLEANUP_COPYDATA_MAGIC,
        cbData: payload.len() as u32,
        lpData: payload.as_mut_ptr().cast(),
    };
    let mut result = 0usize;
    let sent = unsafe {
        SendMessageTimeoutW(
            hwnd,
            WM_COPYDATA,
            ack_receiver.hwnd() as WPARAM,
            (&mut copy_data as *mut COPYDATASTRUCT) as LPARAM,
            SMTO_ABORTIFHUNG | SMTO_BLOCK,
            2_000,
            &mut result,
        )
    };
    sent != 0 && ack_receiver.wait(Duration::from_secs(2)) == Some(JumpAckStatus::Success)
}

fn build_cleanup_copydata_payload(
    ack_hwnd: HWND,
    command_id: u64,
    authorization_token: u64,
) -> Vec<u8> {
    let mut payload = Vec::with_capacity(std::mem::size_of::<CleanupCommandHeader>());
    payload.extend_from_slice(&CLEANUP_COPYDATA_MAGIC.to_ne_bytes());
    payload.extend_from_slice(&(ack_hwnd as usize).to_ne_bytes());
    payload.resize(align_up(payload.len(), std::mem::align_of::<u64>()), 0);
    payload.extend_from_slice(&command_id.to_ne_bytes());
    payload.extend_from_slice(&authorization_token.to_ne_bytes());
    payload.resize(std::mem::size_of::<CleanupCommandHeader>(), 0);
    payload
}

fn shutdown_hook_resources<L, U, R, C, D>(
    mut dialog_hooks: Vec<(HookThreadKey, HHOOK)>,
    mut preload_hooks: Vec<(HookThreadKey, HHOOK)>,
    mut confirmed_threads: Vec<HookThreadKey>,
    preloaded_threads: Vec<HookThreadKey>,
    cleanup_captures: L,
    mut unhook: U,
    revoke_preload_authorizations: R,
    mut clear_confirmed: C,
    drive_threads: D,
) where
    L: FnOnce(),
    U: FnMut(HHOOK),
    R: FnOnce(),
    C: FnMut(HookThreadKey),
    D: FnOnce(&[u32]),
{
    let mut affected_thread_ids = dialog_hooks
        .iter()
        .chain(preload_hooks.iter())
        .map(|(thread, _)| thread.thread_id)
        .chain(confirmed_threads.iter().map(|thread| thread.thread_id))
        .chain(preloaded_threads.iter().map(|thread| thread.thread_id))
        .collect::<Vec<_>>();
    affected_thread_ids.sort_unstable();
    affected_thread_ids.dedup();

    // Restore every patched COM vtable while thread-specific hooks and the
    // authenticated acknowledgement channel are still alive.
    cleanup_captures();

    // Keep the authorization mapping through cleanup, then revoke it before the
    // remaining hooks are removed.
    revoke_preload_authorizations();

    dialog_hooks.sort_unstable_by_key(|(thread, _)| *thread);
    for (_, hook) in dialog_hooks {
        unhook(hook);
    }
    preload_hooks.sort_unstable_by_key(|(thread, _)| *thread);
    for (_, hook) in preload_hooks {
        unhook(hook);
    }
    confirmed_threads.sort_unstable();
    for thread in confirmed_threads {
        clear_confirmed(thread);
    }

    // UnhookWindowsHookEx can return while a remote thread is still inside the
    // hook procedure. Drive every affected queue after all hooks are gone so
    // USER32 can finish the callback and promptly release the injected DLL.
    drive_threads(&affected_thread_ids);
}

fn drive_hook_threads_after_unhook(thread_ids: &[u32]) {
    drive_hook_threads_after_unhook_with(
        thread_ids,
        |thread_id| unsafe {
            PostThreadMessageW(thread_id, WM_NULL, 0, 0);
        },
        || thread::sleep(HOOK_UNLOAD_QUEUE_TURN_GRACE),
        synchronize_hook_thread,
    );
}

fn drive_hook_threads_after_unhook_with<P, W, S>(
    thread_ids: &[u32],
    mut post_queue_message: P,
    wait_for_queue_turn: W,
    mut synchronize_thread: S,
) where
    P: FnMut(u32),
    W: FnOnce(),
    S: FnMut(u32),
{
    for thread_id in thread_ids {
        post_queue_message(*thread_id);
    }
    if !thread_ids.is_empty() {
        wait_for_queue_turn();
    }
    for thread_id in thread_ids {
        synchronize_thread(*thread_id);
    }
}

fn synchronize_hook_thread(thread_id: u32) {
    struct Delivery {
        thread_id: u32,
    }

    unsafe extern "system" fn callback(hwnd: HWND, l_param: LPARAM) -> BOOL {
        let delivery = unsafe { &mut *(l_param as *mut Delivery) };
        if unsafe { GetWindowThreadProcessId(hwnd, null_mut()) } != delivery.thread_id {
            return TRUE;
        }

        let mut message_result = 0usize;
        let delivered = unsafe {
            SendMessageTimeoutW(
                hwnd,
                WM_NULL,
                0,
                0,
                SMTO_ABORTIFHUNG | SMTO_BLOCK,
                1_000,
                &mut message_result,
            )
        };
        if delivered != 0 {
            return FALSE;
        }
        TRUE
    }

    let mut delivery = Delivery { thread_id };
    unsafe {
        EnumWindows(
            Some(callback),
            (&mut delivery as *mut Delivery).cast::<c_void>() as LPARAM,
        );
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

fn load_hook_proc(module: HMODULE, export: &[u8]) -> Result<DialogHookProc, String> {
    let raw_proc = unsafe { GetProcAddress(module, export.as_ptr()) };
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
    hook_type: i32,
) -> Result<HHOOK, String> {
    validate_target_thread_id(thread_id)?;
    let hook = unsafe { SetWindowsHookExW(hook_type, Some(hook_proc), module, thread_id) };
    if hook.is_null() {
        return Err(io::Error::last_os_error().to_string());
    }

    Ok(hook)
}

fn validate_target_thread_id(thread_id: u32) -> Result<(), String> {
    if thread_id == 0 {
        Err("Process-wide/global Windows hooks are prohibited; a concrete target thread is required."
            .to_string())
    } else {
        Ok(())
    }
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

fn firefox_child_dialog_requires_spawn_proof(dialog: &ObservedDialog) -> bool {
    dialog
        .process_name
        .trim()
        .trim_end_matches(".exe")
        .eq_ignore_ascii_case("firefox")
        && process_session_root_id(dialog.process_id)
            .is_some_and(|root_process_id| root_process_id != dialog.process_id)
}

fn firefox_dialog_precapture_ready(requires_spawn_proof: bool, has_spawn_proof: bool) -> bool {
    !requires_spawn_proof || has_spawn_proof
}

fn is_supported_dialog_window(hwnd: HWND) -> bool {
    let Some(class_name) = class_name(hwnd) else {
        return false;
    };

    is_supported_dialog_shape(&class_name)
}

fn is_supported_dialog_shape(class_name: &str) -> bool {
    class_name == DIALOG_CLASS
}

fn unsupported_foreground_window_message() -> Option<String> {
    let foreground = unsafe { GetForegroundWindow() };
    if foreground.is_null() {
        return None;
    }

    let root = unsafe { GetAncestor(foreground, GA_ROOT) };
    let hwnd = if root.is_null() { foreground } else { root };
    let class_name = class_name(hwnd)?;
    let title = window_text(hwnd);
    let mut process_id = 0u32;
    let thread_id = unsafe { GetWindowThreadProcessId(hwnd, &mut process_id) };
    if thread_id == 0 || process_id == 0 {
        return None;
    }

    unsupported_custom_file_browser_message(&process_name(process_id), &class_name, &title)
}

fn unsupported_custom_file_browser_message(
    process_name: &str,
    class_name: &str,
    title: &str,
) -> Option<String> {
    if !is_blender_custom_file_browser(process_name, class_name, title) {
        return None;
    }

    Some(format!(
        "Foreground window is Blender custom file browser ({}/{}/{}), not a standard Win32 file dialog.",
        process_name.trim(),
        class_name.trim(),
        title.trim()
    ))
}

fn is_blender_custom_file_browser(process_name: &str, class_name: &str, title: &str) -> bool {
    let process_name = process_name
        .trim()
        .strip_suffix(".exe")
        .or_else(|| process_name.trim().strip_suffix(".EXE"))
        .unwrap_or_else(|| process_name.trim());

    process_name.eq_ignore_ascii_case("blender")
        && class_name.trim() == "GHOST_WindowClass"
        && title.trim().eq_ignore_ascii_case("Blender File View")
}

fn should_hook_observed_dialog(
    host_architecture: &str,
    dialog_architecture: &str,
    class_name: &str,
) -> bool {
    dialog_architecture.eq_ignore_ascii_case(host_architecture)
        && is_supported_dialog_shape(class_name)
}

#[cfg(test)]
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
    if unsafe { IsWindowVisible(hwnd) } == 0 {
        return None;
    }
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
        dialog_id: dialog_id_for_window(process_id, thread_id, hwnd),
        window_handle: hwnd,
        thread_id,
        process_id,
        process_name: process_name(process_id),
        class_name,
        title: window_text(hwnd),
        architecture,
    })
}

fn serve_connection(
    pipe: NamedPipeHandle,
    session: &HostSession,
    hook_shutdown: &HookShutdown,
) -> io::Result<()> {
    let client_process_id = named_pipe_client_process_id(pipe.raw())?;
    let outcome = match read_line(pipe.raw()) {
        Ok(request) => request_outcome(&request, session, client_process_id),
        Err(_) => RequestOutcome::reply(command_reply("Failed", "Unknown command.")),
    };

    let result = write_line(pipe.raw(), &outcome.response).and_then(|_| flush_pipe(pipe.raw()));
    unsafe {
        DisconnectNamedPipe(pipe.raw());
    }

    if outcome.shutdown_requested {
        hook_shutdown.request_and_wait();
        std::process::exit(0);
    }

    result
}

fn named_pipe_client_process_id(handle: HANDLE) -> io::Result<u32> {
    let mut process_id = 0u32;
    let result = unsafe { GetNamedPipeClientProcessId(handle, &mut process_id) };
    if result == 0 || process_id == 0 {
        return Err(io::Error::last_os_error());
    }

    Ok(process_id)
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

#[cfg(test)]
fn response_for_request(request: &str, session: &HostSession, client_process_id: u32) -> String {
    request_outcome(request, session, client_process_id).response
}

struct RequestOutcome {
    response: String,
    shutdown_requested: bool,
}

impl RequestOutcome {
    fn reply(response: String) -> Self {
        Self {
            response,
            shutdown_requested: false,
        }
    }

    fn shutdown(response: String) -> Self {
        Self {
            response,
            shutdown_requested: true,
        }
    }
}

fn request_outcome(request: &str, session: &HostSession, client_process_id: u32) -> RequestOutcome {
    let envelope = serde_json::from_str::<IncomingEnvelope>(request);
    let Ok(envelope) = envelope else {
        return RequestOutcome::reply(command_reply("Failed", "Unknown command."));
    };

    if envelope.version != IPC_VERSION {
        return RequestOutcome::reply(command_reply("Failed", "Unsupported IPC version."));
    }

    if !session.accepts_client(
        client_process_id,
        envelope.client_process_id,
        &envelope.secret,
    ) {
        return RequestOutcome::reply(command_reply("Failed", "Unauthorized hook IPC client."));
    }

    let response = match envelope.message_type.as_str() {
        "GetActiveDialog" => active_dialog_response(),
        "JumpDialogToFolder" => {
            let payload = serde_json::from_value::<JumpCommandPayload>(envelope.payload);
            match payload {
                Ok(payload) => jump_dialog_to_folder(payload),
                Err(_) => command_reply("Failed", "Invalid jump command payload."),
            }
        }
        "HealthProbe" => health_probe_reply(hook_runtime_status()),
        "Shutdown" => {
            return RequestOutcome::shutdown(command_reply(
                "Success",
                "Hook host graceful shutdown accepted.",
            ));
        }
        _ => command_reply("Failed", "Unknown command."),
    };

    RequestOutcome::reply(response)
}

fn health_probe_reply(status: HookRuntimeStatus) -> String {
    match status {
        HookRuntimeStatus::Starting => {
            command_reply("HostUnavailable", "Native hook is still starting.")
        }
        HookRuntimeStatus::Ready => command_reply("Success", "Hook host healthy."),
        HookRuntimeStatus::Failed => command_reply("Failed", "Native hook initialization failed."),
    }
}

fn active_dialog_response() -> String {
    let Some(hwnd) = resolve_active_dialog_window() else {
        if let Some(message) = unsupported_foreground_window_message() {
            return command_reply("NoActiveDialog", &message);
        }

        return command_reply("NoActiveDialog", "No supported foreground hook dialog.");
    };

    let Some(dialog) = observed_dialog(hwnd) else {
        return command_reply(
            "NoActiveDialog",
            "Active foreground dialog could not be observed.",
        );
    };

    if let Some(message) =
        active_dialog_report_failure(host_architecture(), &dialog, is_hook_thread_confirmed)
    {
        return command_reply("NoActiveDialog", &message);
    }

    let firefox_file_dialog_utility = firefox_child_dialog_requires_spawn_proof(&dialog);
    let payload = ActiveDialogPayload {
        dialog_id: dialog.dialog_id,
        window_handle: dialog.window_handle as usize,
        process_id: dialog.process_id,
        thread_id: dialog.thread_id,
        architecture: dialog.architecture,
        process_name: dialog.process_name,
        class_name: dialog.class_name,
        title: dialog.title,
        preload_confirmed_before_dialog: has_confirmed_spawn_proof(dialog.process_id),
        firefox_file_dialog_utility,
    };

    let envelope = OutgoingEnvelope {
        version: IPC_VERSION,
        message_type: "ActiveDialog",
        payload,
    };

    serde_json::to_string(&envelope).expect("hook IPC active dialog serialization should not fail")
}

fn active_dialog_report_failure<C>(
    host_architecture: &str,
    dialog: &ObservedDialog,
    is_thread_hook_confirmed: C,
) -> Option<String>
where
    C: Fn(HookThreadKey) -> bool,
{
    if !dialog.architecture.eq_ignore_ascii_case(host_architecture) {
        return Some(format!(
            "Active dialog architecture {} does not match {} hook host.",
            dialog.architecture, host_architecture
        ));
    }

    let thread = HookThreadKey::new(dialog.process_id, dialog.thread_id);
    if !is_thread_hook_confirmed(thread) {
        return Some(format!(
            "Target dialog hook has not been installed yet for process {} thread {}.",
            dialog.process_id, dialog.thread_id
        ));
    }

    None
}

fn jump_dialog_to_folder(payload: JumpCommandPayload) -> String {
    if payload.timeout_ms < 0 || payload.folder_path.trim().is_empty() {
        return command_reply("Failed", "Invalid jump command payload.");
    }

    let Some((expected_process_id, expected_thread_id, hwnd)) = parse_dialog_id(&payload.dialog_id)
    else {
        return command_reply("Failed", "Invalid dialog id.");
    };

    if !is_live_window(hwnd) {
        return command_reply("TargetGone", "Dialog window is no longer available.");
    }

    let mut actual_process_id = 0u32;
    let thread_id = unsafe { GetWindowThreadProcessId(hwnd, &mut actual_process_id) };
    if thread_id == 0
        || !target_window_identity_matches(
            expected_process_id,
            expected_thread_id,
            actual_process_id,
            thread_id,
        )
    {
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

    if let Err(failure) = target_hook_ready(
        expected_process_id,
        expected_thread_id,
        is_hook_thread_confirmed,
    ) {
        return command_reply(failure.status, failure.message);
    }

    let command_id = next_command_id();
    let ack_receiver = match AckReceiver::new(command_id, expected_process_id) {
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

    if let Err(failure) = target_hook_ready(
        expected_process_id,
        expected_thread_id,
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

fn dialog_id_for_window(process_id: u32, thread_id: u32, hwnd: HWND) -> String {
    format!("{process_id}:{thread_id}:{}", hwnd as usize)
}

fn parse_dialog_id(dialog_id: &str) -> Option<(u32, u32, HWND)> {
    let mut parts = dialog_id.split(':');
    let process_id = parts.next()?.parse::<u32>().ok()?;
    let thread_id = parts.next()?.parse::<u32>().ok()?;
    let hwnd = parts.next()?.parse::<usize>().ok()? as HWND;
    if parts.next().is_some() || process_id == 0 || thread_id == 0 || hwnd.is_null() {
        return None;
    }

    Some((process_id, thread_id, hwnd))
}

fn target_window_identity_matches(
    expected_process_id: u32,
    expected_thread_id: u32,
    actual_process_id: u32,
    actual_thread_id: u32,
) -> bool {
    expected_process_id == actual_process_id && expected_thread_id == actual_thread_id
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

fn target_hook_ready<C>(
    target_process_id: u32,
    target_thread_id: u32,
    is_thread_hook_confirmed: C,
) -> Result<(), CommandFailure>
where
    C: Fn(HookThreadKey) -> bool,
{
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
    fn new(command_id: u64, expected_sender_process_id: u32) -> io::Result<Self> {
        let (ready_sender, ready_receiver) = mpsc::channel();
        let (ack_sender, ack_receiver) = mpsc::channel();
        let (startup_control_sender, startup_control_receiver) = mpsc::channel();
        let (finished_sender, finished_receiver) = mpsc::channel();
        let thread = thread::spawn(move || {
            ack_window_thread(
                command_id,
                expected_sender_process_id,
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
    expected_sender_process_id: u32,
    sender: mpsc::Sender<JumpAckStatus>,
}

fn ack_window_thread(
    command_id: u64,
    expected_sender_process_id: u32,
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
        expected_sender_process_id,
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
                let sender_hwnd = w_param as HWND;
                let mut sender_process_id = 0u32;
                let sender_thread_id = if sender_hwnd.is_null() {
                    0
                } else {
                    unsafe { GetWindowThreadProcessId(sender_hwnd, &mut sender_process_id) }
                };
                if command_id == state.command_id
                    && sender_thread_id != 0
                    && sender_process_id == state.expected_sender_process_id
                {
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
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
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
    client_process_id: u32,
    secret: String,
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
    preload_confirmed_before_dialog: bool,
    firefox_file_dialog_utility: bool,
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
    fn cleanup_authorization_is_session_bound_and_never_zero() {
        assert_ne!(0, cleanup_authorization_token(""));
        assert_ne!(
            cleanup_authorization_token("session-a"),
            cleanup_authorization_token("session-b")
        );
        assert_eq!(
            cleanup_authorization_token("session-a"),
            cleanup_authorization_token("session-a")
        );
    }

    #[test]
    fn preload_acknowledgements_are_unique_per_architecture_process_thread_and_generation() {
        let first = preload_acknowledgement_name(HookThreadKey::new(100, 7), 11);
        let other_thread = preload_acknowledgement_name(HookThreadKey::new(100, 8), 11);
        let other_process = preload_acknowledgement_name(HookThreadKey::new(101, 7), 11);
        let other_generation = preload_acknowledgement_name(HookThreadKey::new(100, 7), 12);

        assert!(first.contains(host_architecture()));
        assert_ne!(first, other_thread);
        assert_ne!(first, other_process);
        assert_ne!(first, other_generation);
    }

    #[test]
    fn spawn_proof_rejects_a_reused_child_process_id() {
        let proof = SpawnProof {
            token: 0x1234,
            child_creation_time: 77,
            generation: 0x1234,
            parent_process_id: 100,
            child_process_id: 200,
            child_thread_id: 300,
            status: SPAWN_PROOF_SUCCESS,
            installed_before_resume: 1,
            captured_show_observed: 1,
        };
        assert!(spawn_proof_matches(proof, 0x1234, 100, 200, Some(77)));
        assert!(!spawn_proof_matches(proof, 0x1234, 100, 200, Some(78)));
        assert!(!spawn_proof_matches(proof, 0x1234, 100, 201, Some(77)));
    }

    #[test]
    fn spawn_proof_diagnostic_identifies_each_fail_closed_gate() {
        let proof = SpawnProof {
            token: 0x1234,
            child_creation_time: 77,
            generation: 0x1234,
            parent_process_id: 100,
            child_process_id: 200,
            child_thread_id: 7,
            status: SPAWN_PROOF_SUCCESS,
            installed_before_resume: 1,
            captured_show_observed: 1,
        };
        let rejection = |proof| spawn_proof_rejection(proof, 0x1234, 100, 200, Some(77));
        assert_eq!(None, rejection(proof));
        assert!(rejection(SpawnProof { status: 2, ..proof })
            .unwrap()
            .contains("other values failed"));
        assert!(rejection(SpawnProof {
            status: SPAWN_PROOF_PENDING,
            ..proof
        })
        .unwrap()
        .contains("not complete"));
        assert_eq!(
            Some("token mismatch".to_string()),
            rejection(SpawnProof { token: 9, ..proof })
        );
        assert_eq!(
            Some("hookInstalledBeforeResume false".to_string()),
            rejection(SpawnProof {
                installed_before_resume: 0,
                ..proof
            })
        );
        assert_eq!(
            Some("captured Show has not been observed".to_string()),
            rejection(SpawnProof {
                captured_show_observed: 0,
                ..proof
            })
        );
        assert!(spawn_proof_rejection(proof, 0x1234, 100, 200, Some(78))
            .unwrap()
            .contains("creationTime mismatch"));
    }

    #[test]
    fn firefox_child_dialog_is_fail_closed_without_spawn_precapture_proof() {
        assert!(firefox_dialog_precapture_ready(false, false));
        assert!(firefox_dialog_precapture_ready(false, true));
        assert!(firefox_dialog_precapture_ready(true, true));
        assert!(!firefox_dialog_precapture_ready(true, false));
    }

    #[test]
    fn target_hook_readiness_requires_matching_preload_success_and_callwnd_delivery() {
        let token = 0x1234u64;
        assert!(preload_acknowledgement_matches(
            PreloadAcknowledgement {
                token,
                status: PRELOAD_ACK_SUCCESS,
                host_handle: 0,
            },
            token
        ));
        assert!(!preload_acknowledgement_matches(
            PreloadAcknowledgement {
                token,
                status: PRELOAD_ACK_PENDING,
                host_handle: 0,
            },
            token
        ));
        assert!(!preload_acknowledgement_matches(
            PreloadAcknowledgement {
                token: token + 1,
                status: PRELOAD_ACK_SUCCESS,
                host_handle: 0,
            },
            token
        ));
        assert!(target_hook_ready_after_preload(true, true));
        assert!(!target_hook_ready_after_preload(false, true));
        assert!(!target_hook_ready_after_preload(true, false));
        assert!(preload_confirmation_is_early(false, false));
        assert!(preload_confirmation_is_early(true, true));
        assert!(!preload_confirmation_is_early(false, true));
    }

    #[test]
    fn explicit_preload_health_waits_for_target_process_precapture_acknowledgement() {
        let target = HookThreadKey::new(100, 7);
        let other_process = HookThreadKey::new(200, 8);

        assert!(explicit_preload_runtime_ready(None, &HashSet::new()));
        assert!(!explicit_preload_runtime_ready(Some(100), &HashSet::new()));
        assert!(!explicit_preload_runtime_ready(
            Some(100),
            &HashSet::from([other_process])
        ));
        assert!(explicit_preload_runtime_ready(
            Some(100),
            &HashSet::from([target])
        ));
    }

    #[test]
    fn process_precapture_allows_a_new_dialog_thread_to_confirm_callwnd_delivery() {
        let preload_thread = HookThreadKey::new(100, 7);
        let new_dialog_thread = HookThreadKey::new(100, 99);
        let other_process_dialog_thread = HookThreadKey::new(200, 99);
        let confirmed = HashSet::from([preload_thread]);

        assert!(process_has_confirmed_precapture(
            new_dialog_thread.process_id,
            &confirmed
        ));
        assert!(!process_has_confirmed_precapture(
            other_process_dialog_thread.process_id,
            &confirmed
        ));
        assert!(target_hook_ready_after_preload(
            process_has_confirmed_precapture(new_dialog_thread.process_id, &confirmed),
            true
        ));
        assert!(!target_hook_ready_after_preload(
            process_has_confirmed_precapture(other_process_dialog_thread.process_id, &confirmed),
            true
        ));
    }

    #[test]
    fn cleanup_payload_binds_ack_command_and_authorization_token() {
        let payload = build_cleanup_copydata_payload(42usize as HWND, 7, 11);
        let pointer_size = std::mem::size_of::<usize>();
        let command_offset = align_up(pointer_size * 2, std::mem::align_of::<u64>());

        assert_eq!(std::mem::size_of::<CleanupCommandHeader>(), payload.len());
        assert_eq!(
            CLEANUP_COPYDATA_MAGIC,
            usize::from_ne_bytes(payload[..pointer_size].try_into().unwrap())
        );
        assert_eq!(
            42,
            usize::from_ne_bytes(payload[pointer_size..pointer_size * 2].try_into().unwrap())
        );
        assert_eq!(
            7,
            u64::from_ne_bytes(
                payload[command_offset..command_offset + 8]
                    .try_into()
                    .unwrap()
            )
        );
        assert_eq!(
            11,
            u64::from_ne_bytes(
                payload[command_offset + 8..command_offset + 16]
                    .try_into()
                    .unwrap()
            )
        );
    }

    #[test]
    fn event_driven_scan_keeps_original_interval_as_registration_fallback() {
        assert_eq!(HOOK_EVENT_RECOVERY_RESCAN_INTERVAL, rescan_interval(true));
        assert_eq!(HOOK_RESCAN_INTERVAL, rescan_interval(false));
    }

    #[test]
    fn explicit_preload_scan_is_bounded_and_slows_after_root_is_armed() {
        let recovery_interval = Duration::from_secs(5);
        assert_eq!(Duration::from_millis(25), HOOK_PRELOAD_RESCAN_INTERVAL);
        assert_eq!(
            HOOK_PRELOAD_RESCAN_INTERVAL,
            hook_scan_interval(true, false, true, true, recovery_interval)
        );
        assert_eq!(
            HOOK_TARGET_SESSION_RESCAN_INTERVAL,
            hook_scan_interval(true, true, true, true, recovery_interval)
        );
        assert!(HOOK_PRELOAD_RESCAN_INTERVAL >= Duration::from_millis(25));
        assert!(HOOK_TARGET_SESSION_RESCAN_INTERVAL >= Duration::from_millis(100));
    }

    #[test]
    fn parent_monitor_uses_a_waitable_process_handle() {
        let current_process = open_process_for_exit_wait(std::process::id()).unwrap();

        assert_eq!(WAIT_TIMEOUT, unsafe {
            WaitForSingleObject(current_process.0, 0)
        });
    }

    #[test]
    fn parent_shutdown_waits_until_hook_resources_are_dropped() {
        struct CleanupProbe(std::sync::Arc<AtomicBool>);

        impl Drop for CleanupProbe {
            fn drop(&mut self) {
                self.0.store(true, Ordering::Release);
            }
        }

        let hook_shutdown = Arc::new(HookShutdown::new());
        let resources_dropped = std::sync::Arc::new(AtomicBool::new(false));
        let worker_shutdown = hook_shutdown.clone();
        let worker_resources_dropped = resources_dropped.clone();
        let worker = thread::spawn(move || {
            let probe = CleanupProbe(worker_resources_dropped);
            while !worker_shutdown.is_requested() {
                thread::yield_now();
            }
            drop(probe);
            worker_shutdown.mark_complete();
        });

        hook_shutdown.request_and_wait();
        assert!(resources_dropped.load(Ordering::Acquire));
        worker.join().unwrap();
    }

    #[test]
    fn hook_shutdown_restores_captures_before_unhooking_target_threads() {
        #[derive(Debug, PartialEq, Eq)]
        enum Event {
            CleanupCaptures,
            Unhook(usize),
            RevokeAuthorization,
            Clear(HookThreadKey),
            Drive(Vec<u32>),
        }

        let events = std::cell::RefCell::new(Vec::new());
        let thread_42 = HookThreadKey::new(100, 42);
        let thread_7 = HookThreadKey::new(100, 7);
        let thread_99 = HookThreadKey::new(200, 99);

        shutdown_hook_resources(
            vec![(thread_42, 420usize as HHOOK), (thread_7, 70usize as HHOOK)],
            vec![
                (thread_99, 990usize as HHOOK),
                (thread_42, 421usize as HHOOK),
            ],
            vec![thread_42, thread_7],
            vec![thread_99, thread_42],
            || events.borrow_mut().push(Event::CleanupCaptures),
            |hook| events.borrow_mut().push(Event::Unhook(hook as usize)),
            || events.borrow_mut().push(Event::RevokeAuthorization),
            |thread| events.borrow_mut().push(Event::Clear(thread)),
            |thread_ids| events.borrow_mut().push(Event::Drive(thread_ids.to_vec())),
        );

        assert_eq!(
            vec![
                Event::CleanupCaptures,
                Event::RevokeAuthorization,
                Event::Unhook(70),
                Event::Unhook(420),
                Event::Unhook(421),
                Event::Unhook(990),
                Event::Clear(thread_7),
                Event::Clear(thread_42),
                Event::Drive(vec![7, 42, 99]),
            ],
            *events.borrow()
        );
    }

    #[test]
    fn native_hook_installation_is_fail_closed_for_global_thread_zero() {
        assert!(validate_target_thread_id(42).is_ok());
        let error = validate_target_thread_id(0).unwrap_err();
        assert!(error.contains("global Windows hooks are prohibited"));

        let source = include_str!("main.rs");
        let removed_field = ["global_", "preload_hook"].concat();
        assert!(!source.contains(&removed_field));
        let install_api = ["SetWindowsHook", "ExW("].concat();
        assert_eq!(1, source.matches(&install_api).count());
    }

    #[test]
    fn hook_unload_posts_every_queue_before_the_synchronous_thread_barrier() {
        #[derive(Debug, PartialEq, Eq)]
        enum Event {
            Post(u32),
            QueueTurn,
            Synchronize(u32),
        }

        let events = std::cell::RefCell::new(Vec::new());
        drive_hook_threads_after_unhook_with(
            &[7, 42],
            |thread_id| events.borrow_mut().push(Event::Post(thread_id)),
            || events.borrow_mut().push(Event::QueueTurn),
            |thread_id| events.borrow_mut().push(Event::Synchronize(thread_id)),
        );

        assert_eq!(
            vec![
                Event::Post(7),
                Event::Post(42),
                Event::QueueTurn,
                Event::Synchronize(7),
                Event::Synchronize(42),
            ],
            *events.borrow()
        );
    }

    #[test]
    fn message_wait_timeout_rounds_up_and_never_uses_infinite_value() {
        assert_eq!(0, wait_timeout_millis(Duration::ZERO));
        assert_eq!(1, wait_timeout_millis(Duration::from_nanos(1)));
        assert_eq!(501, wait_timeout_millis(Duration::from_millis(500)));
        assert_eq!(
            u32::MAX - 1,
            wait_timeout_millis(Duration::from_secs(u64::MAX))
        );
    }

    #[test]
    fn dialog_event_requests_an_immediate_scan() {
        DIALOG_SCAN_REQUESTED.store(false, Ordering::Release);

        unsafe {
            dialog_scan_event(null_mut(), EVENT_SYSTEM_DIALOGSTART, null_mut(), 0, 0, 0, 0);
        }

        assert!(DIALOG_SCAN_REQUESTED.swap(false, Ordering::AcqRel));
    }

    #[test]
    fn host_session_accepts_only_its_parent_and_secret() {
        let session = HostSession {
            parent_process_id: 123,
            secret: "session-secret".to_string(),
        };

        assert!(session.accepts_client(123, 123, "session-secret"));
        assert!(!session.accepts_client(124, 123, "session-secret"));
        assert!(!session.accepts_client(123, 124, "session-secret"));
        assert!(!session.accepts_client(123, 123, "wrong-secret"));
    }

    #[test]
    fn pipe_mode_rejects_remote_clients() {
        assert_ne!(0, PIPE_REJECT_REMOTE_CLIENTS);
    }

    #[test]
    fn response_for_request_rejects_unbound_client() {
        let session = HostSession {
            parent_process_id: 123,
            secret: "session-secret".to_string(),
        };
        let request = r#"{"version":1,"messageType":"HealthProbe","clientProcessId":123,"secret":"session-secret","payload":{}}"#;

        let response = response_for_request(request, &session, 124);

        assert!(response.contains("Unauthorized hook IPC client."));
    }

    #[test]
    fn graceful_shutdown_requires_the_bound_parent_identity_and_secret() {
        let session = HostSession {
            parent_process_id: 123,
            secret: "session-secret".to_string(),
        };
        let authorized = r#"{"version":1,"messageType":"Shutdown","clientProcessId":123,"secret":"session-secret","payload":{}}"#;
        let wrong_secret = r#"{"version":1,"messageType":"Shutdown","clientProcessId":123,"secret":"wrong","payload":{}}"#;

        let accepted = request_outcome(authorized, &session, 123);
        let rejected = request_outcome(wrong_secret, &session, 123);

        assert!(accepted.shutdown_requested);
        assert!(accepted.response.contains("graceful shutdown accepted"));
        assert!(!rejected.shutdown_requested);
        assert!(rejected.response.contains("Unauthorized hook IPC client."));
    }

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
    fn preload_process_family_contains_only_root_and_descendants() {
        let parents = [(10, 1), (20, 10), (30, 20), (40, 1), (50, 40)];

        assert_eq!(
            HashSet::from([10, 20, 30]),
            descendant_process_ids(10, &parents)
        );
        assert_eq!(
            HashSet::from([40, 50]),
            descendant_process_ids(40, &parents)
        );
    }

    #[test]
    fn preload_process_family_handles_out_of_order_process_snapshot() {
        let parents = [(30, 20), (20, 10), (10, 1)];

        assert_eq!(
            HashSet::from([10, 20, 30]),
            descendant_process_ids(10, &parents)
        );
    }

    #[test]
    fn preload_session_uses_topmost_same_executable_ancestor() {
        let processes = [
            ProcessFamilyEntry {
                process_id: 10,
                parent_process_id: 1,
                executable_name: "firefox.exe".to_string(),
            },
            ProcessFamilyEntry {
                process_id: 20,
                parent_process_id: 10,
                executable_name: "firefox.exe".to_string(),
            },
            ProcessFamilyEntry {
                process_id: 30,
                parent_process_id: 20,
                executable_name: "firefox.exe".to_string(),
            },
            ProcessFamilyEntry {
                process_id: 40,
                parent_process_id: 1,
                executable_name: "firefox.exe".to_string(),
            },
        ];

        assert_eq!(
            Some((10, "firefox.exe".to_string())),
            session_process_root(30, &processes)
        );
        assert_eq!(
            Some((40, "firefox.exe".to_string())),
            session_process_root(40, &processes)
        );
    }

    #[test]
    fn health_probe_reply_reflects_native_hook_runtime_state() {
        let status = |reply: String| {
            serde_json::from_str::<serde_json::Value>(&reply).unwrap()["payload"]["status"]
                .as_str()
                .unwrap()
                .to_string()
        };

        assert_eq!(
            "HostUnavailable",
            status(health_probe_reply(HookRuntimeStatus::Starting))
        );
        assert_eq!(
            "Success",
            status(health_probe_reply(HookRuntimeStatus::Ready))
        );
        assert_eq!(
            "Failed",
            status(health_probe_reply(HookRuntimeStatus::Failed))
        );
    }

    #[test]
    fn child_host_launches_from_args_returns_launch_triplets() {
        let launches = child_host_launches_from_args([
            "ListaryOpen.HookHost.exe",
            "--pipe",
            "listary-open-hook-x64",
            "--dll",
            "C:\\ListaryOpen\\hooks\\x64\\ListaryOpen.Hook.dll",
            "--launch-host",
            "C:\\ListaryOpen\\hooks\\x86\\ListaryOpen.HookHost.exe",
            "--launch-pipe",
            "listary-open-hook-x86",
            "--launch-dll",
            "C:\\ListaryOpen\\hooks\\x86\\ListaryOpen.Hook.dll",
        ]);

        assert_eq!(
            vec![ChildHostLaunch {
                host_exe_path: "C:\\ListaryOpen\\hooks\\x86\\ListaryOpen.HookHost.exe".to_string(),
                pipe_name: "listary-open-hook-x86".to_string(),
                dll_path: "C:\\ListaryOpen\\hooks\\x86\\ListaryOpen.Hook.dll".to_string(),
            }],
            launches
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
    fn active_dialog_report_failure_explains_gate_that_failed() {
        let dialog = ObservedDialog {
            dialog_id: "100:200".to_string(),
            window_handle: 200usize as HWND,
            process_id: 100,
            thread_id: 42,
            process_name: "Code.exe".to_string(),
            class_name: "#32770".to_string(),
            title: "Open Folder".to_string(),
            architecture: "x86",
        };

        assert_eq!(
            Some("Active dialog architecture x86 does not match x64 hook host.".to_string()),
            active_dialog_report_failure("x64", &dialog, |_| true)
        );

        let dialog = ObservedDialog {
            architecture: "x64",
            ..dialog
        };

        assert_eq!(
            Some(
                "Target dialog hook has not been installed yet for process 100 thread 42."
                    .to_string()
            ),
            active_dialog_report_failure("x64", &dialog, |_| false)
        );

        assert_eq!(
            None,
            active_dialog_report_failure("x64", &dialog, |thread| {
                thread == HookThreadKey::new(100, 42)
            })
        );
    }

    #[test]
    fn captured_target_hook_ready_does_not_depend_on_foreground_window() {
        assert_eq!(
            Ok(()),
            target_hook_ready(100, 42, |thread| { thread == HookThreadKey::new(100, 42) })
        );

        let unhooked = target_hook_ready(100, 42, |_| false).unwrap_err();
        assert_eq!("NoActiveDialog", unhooked.status);
        assert_eq!(
            "Target dialog hook has not been installed yet.",
            unhooked.message
        );
    }

    #[test]
    fn supported_dialog_shape_is_locale_independent() {
        assert!(is_supported_dialog_shape("#32770"));
    }

    #[test]
    fn supported_dialog_shape_rejects_non_dialog_class() {
        assert!(!is_supported_dialog_shape("Chrome_WidgetWin_1"));
    }

    #[test]
    fn custom_file_browser_message_identifies_blender_file_view() {
        let message = unsupported_custom_file_browser_message(
            "blender.exe",
            "GHOST_WindowClass",
            "Blender File View",
        );

        assert_eq!(
            Some(
                "Foreground window is Blender custom file browser (blender.exe/GHOST_WindowClass/Blender File View), not a standard Win32 file dialog."
                    .to_string()
            ),
            message
        );
        assert_eq!(
            None,
            unsupported_custom_file_browser_message("blender.exe", "#32770", "Open")
        );
    }

    #[test]
    fn observed_dialog_hooking_requires_matching_host_architecture_and_supported_shape() {
        assert!(should_hook_observed_dialog("x64", "x64", "#32770"));
        assert!(should_hook_observed_dialog("x86", "x86", "#32770"));
        assert!(!should_hook_observed_dialog("x64", "x86", "#32770"));
        assert!(!should_hook_observed_dialog(
            "x86",
            "x86",
            "Chrome_WidgetWin_1"
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
    fn dialog_id_includes_process_thread_and_hwnd() {
        assert_eq!(
            "42:77:123456",
            dialog_id_for_window(42, 77, 123456usize as HWND)
        );
        assert_eq!(
            Some((42, 77, 123456usize as HWND)),
            parse_dialog_id("42:77:123456")
        );
        assert_eq!(None, parse_dialog_id("42:123456"));
        assert_eq!(None, parse_dialog_id("42"));
        assert_eq!(None, parse_dialog_id("not-a-pid:77:123456"));
        assert_eq!(None, parse_dialog_id("42:not-a-thread:123456"));
        assert_eq!(None, parse_dialog_id("42:77:not-a-window"));
        assert_eq!(None, parse_dialog_id("42:77:123456:extra"));
    }

    #[test]
    fn captured_target_identity_rejects_a_different_thread() {
        assert!(target_window_identity_matches(42, 77, 42, 77));
        assert!(!target_window_identity_matches(42, 77, 42, 78));
        assert!(!target_window_identity_matches(42, 77, 43, 77));
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
