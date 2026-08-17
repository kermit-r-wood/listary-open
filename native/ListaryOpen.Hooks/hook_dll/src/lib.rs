use listary_open_hook_common::{
    CleanupCommandHeader, JumpAckHeader, JumpCommandHeader, PreloadAcknowledgement,
    PreloadAuthorization, SpawnProof, CLEANUP_COPYDATA_MAGIC, DIALOG_CLASS,
    JUMP_ACK_COPYDATA_MAGIC, JUMP_ACK_STATUS_FAILED, JUMP_ACK_STATUS_SUCCESS,
    JUMP_ACK_STATUS_UNSUPPORTED_DIALOG, JUMP_COPYDATA_MAGIC, PRELOAD_ACK_SUCCESS,
    SPAWN_PROOF_FAILED, SPAWN_PROOF_PENDING, SPAWN_PROOF_SUCCESS,
};
use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicUsize, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};
use windows_sys::Win32::Foundation::{
    CloseHandle, LocalFree, HANDLE, HMODULE, HWND, INVALID_HANDLE_VALUE, LPARAM, LRESULT, TRUE,
    WPARAM,
};
use windows_sys::Win32::System::DataExchange::COPYDATASTRUCT;
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Module32FirstW, Module32NextW, Process32FirstW, Process32NextW,
    MODULEENTRY32W, PROCESSENTRY32W, TH32CS_SNAPMODULE, TH32CS_SNAPMODULE32, TH32CS_SNAPPROCESS,
};
use windows_sys::Win32::System::Memory::{
    VirtualAllocEx, VirtualFreeEx, VirtualQuery, MEMORY_BASIC_INFORMATION, MEM_COMMIT, MEM_RELEASE,
    MEM_RESERVE, PAGE_GUARD, PAGE_NOACCESS,
};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, GetAncestor, GetClassNameW, GetWindowTextLengthW, GetWindowTextW,
    GetWindowThreadProcessId, KillTimer, SendMessageW, SetTimer, CWPSTRUCT, GA_ROOT, WM_COPYDATA,
};

const BFFM_SETSELECTIONW: u32 = 0x0400 + 103;
const BFFM_GETSELECTIONW: u32 = 0x0400 + 102;
const FOLDER_PATH_BUFFER_LEN: usize = 32_768;
const CLSCTX_INPROC_SERVER: u32 = 0x1;
const SIGDN_FILESYSPATH: u32 = 0x8005_8000;
const PAGE_READWRITE: u32 = 0x04;
const FILE_MAP_READ: u32 = 0x0004;
const FILE_MAP_WRITE: u32 = 0x0002;
const COINIT_APARTMENTTHREADED: u32 = 0x2;
const RPC_E_CHANGED_MODE: i32 = 0x8001_0106u32 as i32;
const CREATE_SUSPENDED: u32 = 0x0000_0004;
const REMOTE_INITIALIZATION_TIMEOUT_MS: u32 = 5_000;

#[repr(C)]
struct ProcessInformation {
    process: HANDLE,
    thread: HANDLE,
    process_id: u32,
    thread_id: u32,
}

#[repr(C)]
struct FileTime {
    low: u32,
    high: u32,
}

#[repr(C)]
struct UnicodeString {
    length: u16,
    maximum_length: u16,
    buffer: *const u16,
}

#[repr(C)]
struct ProcessBasicInformation {
    reserved1: *mut c_void,
    peb_base_address: *mut c_void,
    reserved2: [*mut c_void; 2],
    unique_process_id: usize,
    inherited_from_unique_process_id: usize,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct RemoteFirefoxInitialization {
    authorization: PreloadAuthorization,
    host_handle: usize,
}

#[repr(C)]
struct LdrDllLoadedNotificationData {
    flags: u32,
    full_dll_name: *const UnicodeString,
    base_dll_name: *const UnicodeString,
    dll_base: *mut c_void,
    size_of_image: u32,
}

#[repr(C)]
#[derive(Clone, Copy, PartialEq, Eq)]
struct Guid {
    data1: u32,
    data2: u16,
    data3: u16,
    data4: [u8; 8],
}

const CLSID_FILE_OPEN_DIALOG: Guid = Guid {
    data1: 0xDC1C5A9C,
    data2: 0xE88A,
    data3: 0x4DDE,
    data4: [0xA5, 0xA1, 0x60, 0xF8, 0x2A, 0x20, 0xAE, 0xF7],
};
const CLSID_FILE_SAVE_DIALOG: Guid = Guid {
    data1: 0xC0B4E2F3,
    data2: 0xBA21,
    data3: 0x4773,
    data4: [0x8D, 0xBA, 0x33, 0x5E, 0xC9, 0x46, 0xEB, 0x8B],
};
const IID_IFILE_DIALOG: Guid = Guid {
    data1: 0x42F85136,
    data2: 0xDB7E,
    data3: 0x439C,
    data4: [0x85, 0xF1, 0xE4, 0x07, 0x5D, 0x13, 0x5F, 0xC8],
};
const IID_IOLE_WINDOW: Guid = Guid {
    data1: 0x00000114,
    data2: 0,
    data3: 0,
    data4: [0xC0, 0, 0, 0, 0, 0, 0, 0x46],
};
const IID_ISHELL_ITEM: Guid = Guid {
    data1: 0x43826D1E,
    data2: 0xE718,
    data3: 0x42EE,
    data4: [0xBC, 0x55, 0xA1, 0xE2, 0x61, 0xC3, 0x7B, 0xFE],
};
const CLSID_STD_GLOBAL_INTERFACE_TABLE: Guid = Guid {
    data1: 0x00000323,
    data2: 0,
    data3: 0,
    data4: [0xC0, 0, 0, 0, 0, 0, 0, 0x46],
};
const IID_IGLOBAL_INTERFACE_TABLE: Guid = Guid {
    data1: 0x00000146,
    data2: 0,
    data3: 0,
    data4: [0xC0, 0, 0, 0, 0, 0, 0, 0x46],
};

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct SharedDialogCapture {
    token: u64,
    generation: u32,
    process_id: u32,
    git_cookie: u32,
    thread_id: u32,
}

#[link(name = "ole32")]
extern "system" {
    fn CoInitializeEx(reserved: *mut c_void, coinit: u32) -> i32;
    fn CoUninitialize();
    fn CoCreateInstance(
        clsid: *const Guid,
        outer: *mut c_void,
        context: u32,
        iid: *const Guid,
        object: *mut *mut c_void,
    ) -> i32;
    fn CoTaskMemFree(memory: *const c_void);
}

#[link(name = "shell32")]
extern "system" {
    fn SHCreateItemFromParsingName(
        path: *const u16,
        bind_context: *mut c_void,
        iid: *const Guid,
        item: *mut *mut c_void,
    ) -> i32;
    fn CommandLineToArgvW(command_line: *const u16, argument_count: *mut i32) -> *mut *mut u16;
}

#[link(name = "kernel32")]
extern "system" {
    fn GetCurrentProcessId() -> u32;
    fn GetCurrentThreadId() -> u32;
    fn GetEnvironmentVariableW(name: *const u16, value: *mut u16, size: u32) -> u32;
    fn SetEnvironmentVariableW(name: *const u16, value: *const u16) -> i32;
    fn GetCurrentProcess() -> HANDLE;
    fn GetModuleHandleExW(flags: u32, module_name: *const u16, module: *mut *mut c_void) -> i32;
    fn GetModuleHandleW(module_name: *const u16) -> HMODULE;
    fn GetModuleFileNameW(module: HMODULE, path: *mut u16, size: u32) -> u32;
    fn FreeLibrary(module: *mut c_void) -> i32;
    fn OpenProcess(access: u32, inherit_handle: i32, process_id: u32) -> *mut c_void;
    fn DuplicateHandle(
        source_process: HANDLE,
        source_handle: HANDLE,
        target_process: HANDLE,
        target_handle: *mut HANDLE,
        desired_access: u32,
        inherit_handle: i32,
        options: u32,
    ) -> i32;
    fn WaitForSingleObject(handle: *mut c_void, milliseconds: u32) -> u32;
    fn FreeLibraryAndExitThread(module: *mut c_void, exit_code: u32) -> !;
    fn MapViewOfFile(
        mapping: *mut c_void,
        desired_access: u32,
        file_offset_high: u32,
        file_offset_low: u32,
        bytes_to_map: usize,
    ) -> *mut c_void;
    fn OpenFileMappingW(access: u32, inherit_handle: i32, name: *const u16) -> *mut c_void;
    fn CreateFileMappingW(
        file: HANDLE,
        attributes: *const c_void,
        protection: u32,
        maximum_size_high: u32,
        maximum_size_low: u32,
        name: *const u16,
    ) -> HANDLE;
    fn UnmapViewOfFile(address: *const c_void) -> i32;
    fn ResumeThread(thread: HANDLE) -> u32;
    fn GetProcessTimes(
        process: HANDLE,
        creation: *mut FileTime,
        exit: *mut FileTime,
        kernel: *mut FileTime,
        user: *mut FileTime,
    ) -> i32;
    fn QueryFullProcessImageNameW(
        process: HANDLE,
        flags: u32,
        path: *mut u16,
        size: *mut u32,
    ) -> i32;
    fn CreateProcessW(
        application_name: *const u16,
        command_line: *mut u16,
        process_attributes: *const c_void,
        thread_attributes: *const c_void,
        inherit_handles: i32,
        creation_flags: u32,
        environment: *const c_void,
        current_directory: *const u16,
        startup_information: *const c_void,
        process_information: *mut ProcessInformation,
    ) -> i32;
    fn CreateProcessAsUserW(
        token: HANDLE,
        application_name: *const u16,
        command_line: *mut u16,
        process_attributes: *const c_void,
        thread_attributes: *const c_void,
        inherit_handles: i32,
        creation_flags: u32,
        environment: *const c_void,
        current_directory: *const u16,
        startup_information: *const c_void,
        process_information: *mut ProcessInformation,
    ) -> i32;
    fn VirtualProtect(
        address: *mut c_void,
        size: usize,
        protection: u32,
        old_protection: *mut u32,
    ) -> i32;
    fn GetProcAddress(module: HMODULE, name: *const u8) -> *mut c_void;
    fn CreateRemoteThread(
        process: HANDLE,
        thread_attributes: *const c_void,
        stack_size: usize,
        start_address: *const c_void,
        parameter: *const c_void,
        creation_flags: u32,
        thread_id: *mut u32,
    ) -> HANDLE;
    fn GetExitCodeThread(thread: HANDLE, exit_code: *mut u32) -> i32;
    fn WriteProcessMemory(
        process: HANDLE,
        base_address: *mut c_void,
        buffer: *const c_void,
        size: usize,
        written: *mut usize,
    ) -> i32;
}

#[link(name = "ntdll")]
extern "system" {
    fn NtQueryInformationProcess(
        process: HANDLE,
        information_class: u32,
        information: *mut c_void,
        information_length: u32,
        return_length: *mut u32,
    ) -> i32;
}

static FILE_DIALOGS: OnceLock<Mutex<Vec<(usize, u32)>>> = OnceLock::new();
static FILE_DIALOG_SHOW_VTABLES: OnceLock<Mutex<Vec<(usize, usize)>>> = OnceLock::new();
static CAPTURED_DIALOG_CLASSES: AtomicU8 = AtomicU8::new(0);
static INSTALLING_DIALOG_CAPTURE: AtomicBool = AtomicBool::new(false);
static CAPTURE_SHUTDOWN_REQUESTED: AtomicBool = AtomicBool::new(false);
static CAPTURE_RESTORE_LOCK: Mutex<()> = Mutex::new(());
static CAPTURE_LIFECYCLE_STARTED: AtomicBool = AtomicBool::new(false);
static PRELOAD_ARMED_THREADS: OnceLock<Mutex<Vec<(u32, u32)>>> = OnceLock::new();
static PENDING_CAPTURE_TIMERS: OnceLock<Mutex<Vec<(u32, u32, usize)>>> = OnceLock::new();
static SHARED_DIALOG_CAPTURES: OnceLock<Mutex<Vec<u32>>> = OnceLock::new();
static CAPTURE_LIFECYCLE_LOCK: Mutex<()> = Mutex::new(());
static ACTIVE_CAPTURED_SHOWS: AtomicUsize = AtomicUsize::new(0);
static PENDING_CLEANUP_ACK: OnceLock<Mutex<Option<(usize, u64, usize)>>> = OnceLock::new();
static ACTIVE_PRELOAD_AUTHORIZATION: Mutex<Option<PreloadAuthorization>> = Mutex::new(None);
static FIREFOX_SPAWN_API_PATCHES: Mutex<Vec<(usize, usize, usize)>> = Mutex::new(Vec::new());
static FIREFOX_CO_CREATE_INSTANCE_ORIGINAL: AtomicUsize = AtomicUsize::new(0);
static FIREFOX_CO_CREATE_INSTANCE_PATCH: Mutex<Option<(usize, usize)>> = Mutex::new(None);
static FIREFOX_DLL_NOTIFICATION_COOKIE: Mutex<Option<usize>> = Mutex::new(None);
static SPAWN_PROOF_MAPPINGS: OnceLock<Mutex<Vec<usize>>> = OnceLock::new();

struct ActiveCapturedShow;

impl ActiveCapturedShow {
    fn enter() -> Self {
        ACTIVE_CAPTURED_SHOWS.fetch_add(1, Ordering::AcqRel);
        Self
    }
}

impl Drop for ActiveCapturedShow {
    fn drop(&mut self) {
        ACTIVE_CAPTURED_SHOWS.fetch_sub(1, Ordering::AcqRel);
    }
}

struct ComInitialization {
    uninitialize: bool,
}

impl ComInitialization {
    fn enter() -> Option<Self> {
        let result = unsafe { CoInitializeEx(std::ptr::null_mut(), COINIT_APARTMENTTHREADED) };
        if result >= 0 {
            Some(Self { uninitialize: true })
        } else if result == RPC_E_CHANGED_MODE {
            // COM is already initialized in a different apartment model. GIT
            // still returns a proxy valid for that existing apartment.
            Some(Self {
                uninitialize: false,
            })
        } else {
            None
        }
    }
}

impl Drop for ComInitialization {
    fn drop(&mut self) {
        if self.uninitialize {
            unsafe { CoUninitialize() };
        }
    }
}

struct CapturedDialogReference {
    pointer: *mut c_void,
    release: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum SharedDialogCaptureError {
    CaptureMissing,
    PayloadInvalid,
    AuthorizationMismatch,
    GlobalTableUnavailable,
    InterfaceUnavailable,
}

impl CapturedDialogReference {
    fn borrowed(pointer: usize) -> Self {
        Self {
            pointer: pointer as *mut c_void,
            release: false,
        }
    }

    fn owned(pointer: *mut c_void) -> Self {
        Self {
            pointer,
            release: true,
        }
    }
}

impl Drop for CapturedDialogReference {
    fn drop(&mut self) {
        if self.release {
            unsafe { release_interface(self.pointer) };
        }
    }
}

fn ensure_capture_lifecycle_started(authorization: PreloadAuthorization) -> bool {
    ensure_capture_lifecycle_started_with_host(authorization, std::ptr::null_mut())
}

fn ensure_capture_lifecycle_started_with_host(
    authorization: PreloadAuthorization,
    provided_host: HANDLE,
) -> bool {
    if CAPTURE_LIFECYCLE_STARTED.load(Ordering::Acquire) {
        if !provided_host.is_null() {
            unsafe { CloseHandle(provided_host) };
        }
        return active_authorization() == Some(authorization)
            && !CAPTURE_SHUTDOWN_REQUESTED.load(Ordering::Acquire);
    }
    let Ok(_lifecycle_guard) = CAPTURE_LIFECYCLE_LOCK.lock() else {
        if !provided_host.is_null() {
            unsafe { CloseHandle(provided_host) };
        }
        return false;
    };
    if CAPTURE_LIFECYCLE_STARTED.load(Ordering::Acquire) {
        if !provided_host.is_null() {
            unsafe { CloseHandle(provided_host) };
        }
        return active_authorization() == Some(authorization)
            && !CAPTURE_SHUTDOWN_REQUESTED.load(Ordering::Acquire);
    }

    const GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS: u32 = 0x0000_0004;
    const PROCESS_SYNCHRONIZE: u32 = 0x0010_0000;
    let mut module = std::ptr::null_mut();
    if unsafe {
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            captured_file_dialog_show as *const () as *const u16,
            &mut module,
        )
    } == 0
    {
        if !provided_host.is_null() {
            unsafe { CloseHandle(provided_host) };
        }
        return false;
    }
    let host = if provided_host.is_null() {
        spawn_preload_host_handle(authorization).unwrap_or_else(|| unsafe {
            OpenProcess(PROCESS_SYNCHRONIZE, 0, authorization.host_process_id)
        })
    } else {
        provided_host
    };
    if host.is_null() {
        unsafe { FreeLibrary(module) };
        return false;
    }

    let module_address = module as usize;
    let host_address = host as usize;
    if let Ok(mut active) = ACTIVE_PRELOAD_AUTHORIZATION.lock() {
        *active = Some(authorization);
    }
    if std::thread::Builder::new()
        .name("listary-hook-lifecycle".to_string())
        .spawn(move || capture_lifecycle_watcher(module_address, host_address, authorization))
        .is_err()
    {
        unsafe { CloseHandle(host) };
        unsafe { FreeLibrary(module) };
        if let Ok(mut active) = ACTIVE_PRELOAD_AUTHORIZATION.lock() {
            *active = None;
        }
        return false;
    }
    CAPTURE_LIFECYCLE_STARTED.store(true, Ordering::Release);
    true
}

fn active_or_preload_authorization() -> Option<PreloadAuthorization> {
    active_authorization().or_else(preload_authorization)
}

fn active_authorization() -> Option<PreloadAuthorization> {
    ACTIVE_PRELOAD_AUTHORIZATION
        .lock()
        .ok()
        .and_then(|active| *active)
}

fn capture_lifecycle_watcher(
    module_address: usize,
    host_address: usize,
    mut authorization: PreloadAuthorization,
) -> ! {
    const WAIT_OBJECT_0: u32 = 0;
    let module = module_address as *mut c_void;
    let mut host = host_address as *mut c_void;
    let mut quiescent_since = None;
    let mut cleanup_ack_sent = false;
    loop {
        let host_exited = unsafe { WaitForSingleObject(host, 10) } == WAIT_OBJECT_0;
        if host_exited {
            CAPTURE_SHUTDOWN_REQUESTED.store(true, Ordering::Release);
        }
        if !CAPTURE_SHUTDOWN_REQUESTED.load(Ordering::Acquire) {
            quiescent_since = None;
            continue;
        }
        let restored = restore_file_dialog_show_captures();
        let active = ACTIVE_CAPTURED_SHOWS.load(Ordering::Acquire);
        if !restored || active != 0 {
            quiescent_since = None;
            continue;
        }
        let quiet_started = quiescent_since.get_or_insert_with(Instant::now);
        if quiet_started.elapsed() >= Duration::from_millis(50) {
            if !cleanup_ack_sent {
                if let Some(pending) = PENDING_CLEANUP_ACK.get() {
                    if let Ok(mut pending) = pending.lock() {
                        if let Some((ack_hwnd, command_id, sender_hwnd)) = pending.take() {
                            send_jump_ack(
                                sender_hwnd as HWND,
                                ack_hwnd as HWND,
                                command_id,
                                JumpAckStatus::Success,
                            );
                            cleanup_ack_sent = true;
                        }
                    }
                }
            }

            // The success ACK means only "vtable prepared". Keep the ordinary
            // module reference until HookHost has unhooked, driven every target
            // queue, and exited; only a signaled host handle permits release.
            if !host_exited {
                continue;
            }

            if let Some(pending) = PENDING_CLEANUP_ACK.get() {
                if let Ok(mut pending) = pending.lock() {
                    pending.take();
                }
            }
            if let Some(next_authorization) = preload_authorization().filter(|next| {
                next.generation != authorization.generation
                    || next.host_process_id != authorization.host_process_id
                    || next.token != authorization.token
            }) {
                const PROCESS_SYNCHRONIZE: u32 = 0x0010_0000;
                let next_host = unsafe {
                    OpenProcess(PROCESS_SYNCHRONIZE, 0, next_authorization.host_process_id)
                };
                if !next_host.is_null() {
                    unsafe { CloseHandle(host) };
                    host = next_host;
                    authorization = next_authorization;
                    reset_capture_state_after_cleanup();
                    if let Ok(mut active) = ACTIVE_PRELOAD_AUTHORIZATION.lock() {
                        *active = Some(next_authorization);
                    }
                    CAPTURE_SHUTDOWN_REQUESTED.store(false, Ordering::Release);
                    quiescent_since = None;
                    cleanup_ack_sent = false;
                    continue;
                }
            }
            unsafe { CloseHandle(host) };
            if let Ok(mut active) = ACTIVE_PRELOAD_AUTHORIZATION.lock() {
                *active = None;
            }
            reset_capture_state_after_cleanup();
            CAPTURE_LIFECYCLE_STARTED.store(false, Ordering::Release);
            CAPTURE_SHUTDOWN_REQUESTED.store(false, Ordering::Release);
            unsafe { FreeLibraryAndExitThread(module, 0) };
        }
    }
}

fn reset_capture_state_after_cleanup() {
    if let Some(armed) = PRELOAD_ARMED_THREADS.get() {
        if let Ok(mut armed) = armed.lock() {
            armed.clear();
        }
    }
}

fn preload_callback_armed(authorization: PreloadAuthorization) -> bool {
    let thread_id = unsafe { GetCurrentThreadId() };
    PRELOAD_ARMED_THREADS
        .get_or_init(|| Mutex::new(Vec::new()))
        .lock()
        .is_ok_and(|armed| armed.contains(&(authorization.generation, thread_id)))
}

fn mark_preload_callback_armed(authorization: PreloadAuthorization) {
    let thread_id = unsafe { GetCurrentThreadId() };
    if let Ok(mut armed) = PRELOAD_ARMED_THREADS
        .get_or_init(|| Mutex::new(Vec::new()))
        .lock()
    {
        if !armed.contains(&(authorization.generation, thread_id)) {
            armed.push((authorization.generation, thread_id));
        }
    }
}

fn schedule_direct_dialog_capture(authorization: PreloadAuthorization) -> bool {
    if CAPTURE_SHUTDOWN_REQUESTED.load(Ordering::Acquire) || preload_callback_armed(authorization) {
        return false;
    }
    let thread_id = unsafe { GetCurrentThreadId() };
    let timers = PENDING_CAPTURE_TIMERS.get_or_init(|| Mutex::new(Vec::new()));
    let Ok(mut timers) = timers.lock() else {
        return false;
    };
    if timers
        .iter()
        .any(|timer| timer.0 == authorization.generation && timer.1 == thread_id)
    {
        return true;
    }

    // SetTimer defers COM activation until normal message dispatch resumes on
    // this exact UI thread. Calling CoCreateInstance from a hook callback is
    // re-entrant and creating the objects on a separate STA can patch only a
    // proxy/apartment-specific vtable instead of the dialog's real vtable.
    let timer_id = unsafe {
        SetTimer(
            std::ptr::null_mut(),
            0,
            1,
            Some(direct_dialog_capture_timer_callback),
        )
    };
    if timer_id == 0 {
        return false;
    }

    // Hold the DLL lifecycle open from scheduling until the timer callback
    // starts. If a blocked target thread never dispatches the timer, leaking
    // this reference is safer than unloading code still registered with USER32.
    ACTIVE_CAPTURED_SHOWS.fetch_add(1, Ordering::AcqRel);
    timers.push((authorization.generation, thread_id, timer_id));
    true
}

unsafe extern "system" fn direct_dialog_capture_timer_callback(
    _window: HWND,
    _message: u32,
    timer_id: usize,
    _time: u32,
) {
    let _active_callback = ActiveCapturedShow::enter();
    unsafe { KillTimer(std::ptr::null_mut(), timer_id) };
    let thread_id = unsafe { GetCurrentThreadId() };
    let generation = PENDING_CAPTURE_TIMERS
        .get()
        .and_then(|timers| timers.lock().ok())
        .and_then(|mut timers| {
            timers
                .iter()
                .position(|timer| timer.1 == thread_id && timer.2 == timer_id)
                .map(|index| timers.remove(index).0)
        });
    let Some(generation) = generation else {
        return;
    };
    ACTIVE_CAPTURED_SHOWS.fetch_sub(1, Ordering::AcqRel);

    let Some(authorization) = active_or_preload_authorization().filter(|authorization| {
        authorization.generation == generation
            && !CAPTURE_SHUTDOWN_REQUESTED.load(Ordering::Acquire)
    }) else {
        return;
    };
    let initialized =
        unsafe { CoInitializeEx(std::ptr::null_mut(), COINIT_APARTMENTTHREADED) } >= 0;
    let installed = initialized && ensure_direct_dialog_capture_installed();
    if initialized {
        unsafe { CoUninitialize() };
    }
    if installed {
        if current_thread_has_spawn_preload_authorization(authorization) {
            report_spawn_preload_success(authorization);
        } else {
            report_preload_success(authorization);
        }
        mark_preload_callback_armed(authorization);
    } else {
        let _ = schedule_direct_dialog_capture(authorization);
    }
}

fn ensure_direct_dialog_capture_installed() -> bool {
    if CAPTURE_SHUTDOWN_REQUESTED.load(Ordering::Acquire)
        || INSTALLING_DIALOG_CAPTURE.swap(true, Ordering::AcqRel)
    {
        return false;
    }
    if CAPTURE_SHUTDOWN_REQUESTED.load(Ordering::Acquire) {
        INSTALLING_DIALOG_CAPTURE.store(false, Ordering::Release);
        return false;
    }
    let open_installed = unsafe { install_file_dialog_show_capture(&CLSID_FILE_OPEN_DIALOG, 0b01) };
    let save_installed = unsafe { install_file_dialog_show_capture(&CLSID_FILE_SAVE_DIALOG, 0b10) };
    INSTALLING_DIALOG_CAPTURE.store(false, Ordering::Release);
    open_installed && save_installed
}

unsafe fn install_file_dialog_show_capture(clsid: &Guid, class_bit: u8) -> bool {
    let mut dialog = std::ptr::null_mut();
    if unsafe {
        CoCreateInstance(
            clsid,
            std::ptr::null_mut(),
            CLSCTX_INPROC_SERVER,
            &IID_IFILE_DIALOG,
            &mut dialog,
        )
    } < 0
        || dialog.is_null()
    {
        return false;
    }

    let installed = unsafe { capture_file_dialog_show_vtable(dialog, class_bit) };
    unsafe { release_interface(dialog) };
    installed
}

unsafe fn capture_file_dialog_show_vtable(dialog: *mut c_void, class_bit: u8) -> bool {
    if dialog.is_null() || CAPTURE_SHUTDOWN_REQUESTED.load(Ordering::Acquire) {
        return false;
    }
    let vtable = unsafe { *(dialog as *const *mut *mut c_void) };
    let show_slot = unsafe { vtable.add(3) } as *mut usize;
    let replacement = captured_file_dialog_show as *const () as usize;
    let current = unsafe { *show_slot };
    let captures = FILE_DIALOG_SHOW_VTABLES.get_or_init(|| Mutex::new(Vec::new()));
    let mut installed = false;
    if let Ok(mut captures) = captures.lock() {
        if captures.iter().any(|capture| capture.0 == vtable as usize) {
            CAPTURED_DIALOG_CLASSES.fetch_or(class_bit, Ordering::Release);
            installed = current == replacement;
        } else if current == replacement {
            // A replacement without its original slot cannot be called safely.
            // Do not claim this apartment is armed if our capture table lacks it.
        } else {
            let mut old_protection = 0u32;
            if unsafe {
                VirtualProtect(
                    show_slot.cast(),
                    std::mem::size_of::<usize>(),
                    PAGE_READWRITE,
                    &mut old_protection,
                )
            } != 0
            {
                captures.push((vtable as usize, current));
                unsafe { *show_slot = replacement };
                CAPTURED_DIALOG_CLASSES.fetch_or(class_bit, Ordering::Release);
                let mut ignored = 0u32;
                unsafe {
                    VirtualProtect(
                        show_slot.cast(),
                        std::mem::size_of::<usize>(),
                        old_protection,
                        &mut ignored,
                    )
                };
                installed = true;
            }
        }
    }
    installed
}

unsafe extern "system" fn captured_file_dialog_show(dialog: *mut c_void, owner: HWND) -> i32 {
    // Count from the first instruction in the replacement so cleanup cannot
    // observe zero after this function has begun but before its vtable lookup.
    let _active_show = ActiveCapturedShow::enter();
    // The lifecycle watcher sets this gate before restoring captured vtables.
    // Reject a callback that races with shutdown before dereferencing the
    // dialog pointer, otherwise a late COM callback can access a freed object
    // while the host is unloading the DLL.
    if CAPTURE_SHUTDOWN_REQUESTED.load(Ordering::Acquire) {
        return -2_147_467_259; // E_UNEXPECTED
    }
    let vtable = unsafe { *(dialog as *const *const *const c_void) } as usize;
    let original = FILE_DIALOG_SHOW_VTABLES
        .get()
        .and_then(|captures| captures.lock().ok())
        .and_then(|captures| {
            captures
                .iter()
                .find(|capture| capture.0 == vtable)
                .map(|capture| capture.1)
        });
    let Some(original) = original else {
        return -2_147_467_259; // E_UNEXPECTED
    };
    // Show is invoked through the IFileDialog/IModalWindow base pointer itself;
    // retain that exact pointer without a re-entrant QueryInterface call while
    // the shell implementation is entering its modal state.
    unsafe { add_ref_interface(dialog) };
    remember_file_dialog(dialog);
    let shared_capture_ready = remember_shared_dialog_capture(dialog);
    if shared_capture_ready || !current_process_is_firefox_child() {
        mark_spawn_proof_captured_show();
    }
    let original: unsafe extern "system" fn(*mut c_void, HWND) -> i32 =
        unsafe { std::mem::transmute(original) };
    let result = unsafe { original(dialog, owner) };
    forget_file_dialog_on_current_thread(dialog);
    result
}

fn shared_dialog_capture_name() -> String {
    let architecture = if usize::BITS == 64 { "x64" } else { "x86" };
    format!("LISTARYOPEN_ACTIVE_DIALOG_{architecture}")
}

fn encode_shared_dialog_capture(capture: SharedDialogCapture) -> String {
    format!(
        "{:016X}:{:08X}:{:08X}:{:08X}:{:08X}",
        capture.token,
        capture.generation,
        capture.process_id,
        capture.git_cookie,
        capture.thread_id
    )
}

fn decode_shared_dialog_capture(value: &str) -> Option<SharedDialogCapture> {
    let mut fields = value.split(':');
    let capture = SharedDialogCapture {
        token: u64::from_str_radix(fields.next()?, 16).ok()?,
        generation: u32::from_str_radix(fields.next()?, 16).ok()?,
        process_id: u32::from_str_radix(fields.next()?, 16).ok()?,
        git_cookie: u32::from_str_radix(fields.next()?, 16).ok()?,
        thread_id: u32::from_str_radix(fields.next()?, 16).ok()?,
    };
    fields.next().is_none().then_some(capture)
}

unsafe fn create_global_interface_table() -> Option<*mut c_void> {
    let mut table = std::ptr::null_mut();
    let result = unsafe {
        CoCreateInstance(
            &CLSID_STD_GLOBAL_INTERFACE_TABLE,
            std::ptr::null_mut(),
            CLSCTX_INPROC_SERVER,
            &IID_IGLOBAL_INTERFACE_TABLE,
            &mut table,
        )
    };
    (result >= 0 && !table.is_null()).then_some(table)
}

unsafe fn register_interface_in_global(table: *mut c_void, dialog: *mut c_void) -> Option<u32> {
    let register: unsafe extern "system" fn(
        *mut c_void,
        *mut c_void,
        *const Guid,
        *mut u32,
    ) -> i32 = unsafe { interface_method(table, 3) };
    let mut cookie = 0u32;
    (unsafe { register(table, dialog, &IID_IFILE_DIALOG, &mut cookie) } >= 0 && cookie != 0)
        .then_some(cookie)
}

unsafe fn revoke_interface_from_global(table: *mut c_void, cookie: u32) -> bool {
    let revoke: unsafe extern "system" fn(*mut c_void, u32) -> i32 =
        unsafe { interface_method(table, 4) };
    (unsafe { revoke(table, cookie) }) >= 0
}

unsafe fn get_interface_from_global(table: *mut c_void, cookie: u32) -> Option<*mut c_void> {
    let get: unsafe extern "system" fn(*mut c_void, u32, *const Guid, *mut *mut c_void) -> i32 =
        unsafe { interface_method(table, 5) };
    let mut dialog = std::ptr::null_mut();
    (unsafe { get(table, cookie, &IID_IFILE_DIALOG, &mut dialog) } >= 0 && !dialog.is_null())
        .then_some(dialog)
}

fn remember_shared_dialog_capture(dialog: *mut c_void) -> bool {
    let Some(authorization) = active_or_preload_authorization() else {
        return false;
    };
    let Some(table) = (unsafe { create_global_interface_table() }) else {
        return false;
    };
    let Some(cookie) = (unsafe { register_interface_in_global(table, dialog) }) else {
        unsafe { release_interface(table) };
        return false;
    };
    let capture = SharedDialogCapture {
        token: authorization.token,
        generation: authorization.generation,
        process_id: unsafe { GetCurrentProcessId() },
        git_cookie: cookie,
        thread_id: unsafe { GetCurrentThreadId() },
    };
    let variable_name = to_wide_null(&shared_dialog_capture_name());
    let variable_value = to_wide_null(&encode_shared_dialog_capture(capture));
    let published =
        unsafe { SetEnvironmentVariableW(variable_name.as_ptr(), variable_value.as_ptr()) != 0 };
    let retained = if published {
        if let Ok(mut captures) = SHARED_DIALOG_CAPTURES
            .get_or_init(|| Mutex::new(Vec::new()))
            .lock()
        {
            captures.push(cookie);
            true
        } else {
            false
        }
    } else {
        false
    };
    if !retained {
        let _ = unsafe { revoke_interface_from_global(table, cookie) };
        if published {
            unsafe { SetEnvironmentVariableW(variable_name.as_ptr(), std::ptr::null()) };
        }
    }
    unsafe { release_interface(table) };
    retained
}

fn acquire_shared_dialog_capture() -> Result<CapturedDialogReference, SharedDialogCaptureError> {
    let variable_name = to_wide_null(&shared_dialog_capture_name());
    let mut variable_value = [0u16; 256];
    let length = unsafe {
        GetEnvironmentVariableW(
            variable_name.as_ptr(),
            variable_value.as_mut_ptr(),
            variable_value.len() as u32,
        )
    };
    if length == 0 {
        return Err(SharedDialogCaptureError::CaptureMissing);
    }
    if length as usize >= variable_value.len() {
        return Err(SharedDialogCaptureError::PayloadInvalid);
    }
    let capture = decode_shared_dialog_capture(&String::from_utf16_lossy(
        &variable_value[..length as usize],
    ))
    .ok_or(SharedDialogCaptureError::PayloadInvalid)?;
    let authorized = {
        let authorized = active_authorization()
            .into_iter()
            .chain(preload_authorization())
            .any(|authorization| {
                capture.token == authorization.token
                    && capture.generation == authorization.generation
            });
        authorized
            && capture.process_id == unsafe { GetCurrentProcessId() }
            && capture.git_cookie != 0
    };
    if !authorized {
        return Err(SharedDialogCaptureError::AuthorizationMismatch);
    }
    let table = unsafe { create_global_interface_table() }
        .ok_or(SharedDialogCaptureError::GlobalTableUnavailable)?;
    let dialog = unsafe { get_interface_from_global(table, capture.git_cookie) };
    unsafe { release_interface(table) };
    dialog
        .map(CapturedDialogReference::owned)
        .ok_or(SharedDialogCaptureError::InterfaceUnavailable)
}

fn revoke_shared_dialog_captures() -> bool {
    if ACTIVE_CAPTURED_SHOWS.load(Ordering::Acquire) != 0 {
        return false;
    }
    let Some(captures) = SHARED_DIALOG_CAPTURES.get() else {
        return true;
    };
    let Ok(mut captures) = captures.lock() else {
        return false;
    };
    if captures.is_empty() {
        return true;
    }
    let _com = ComInitialization::enter();
    let table = unsafe { create_global_interface_table() };
    let mut revoked = true;
    for cookie in captures.drain(..) {
        if let Some(table) = table {
            revoked &= unsafe { revoke_interface_from_global(table, cookie) };
        }
    }
    let variable_name = to_wide_null(&shared_dialog_capture_name());
    unsafe { SetEnvironmentVariableW(variable_name.as_ptr(), std::ptr::null()) };
    if let Some(table) = table {
        unsafe { release_interface(table) };
    }
    revoked
}

fn forget_file_dialog_on_current_thread(dialog: *mut c_void) {
    let Some(dialogs) = FILE_DIALOGS.get() else {
        return;
    };
    let Ok(mut dialogs) = dialogs.lock() else {
        return;
    };
    let current_thread_id = unsafe { GetCurrentThreadId() };
    if let Some(index) = dialogs
        .iter()
        .position(|stored| stored.0 == dialog as usize && stored.1 == current_thread_id)
    {
        let (dialog, _) = dialogs.remove(index);
        unsafe { release_interface(dialog as *mut c_void) };
    }
}

fn readable_pointer(address: *const c_void) -> Option<usize> {
    if address.is_null() {
        return None;
    }
    let mut information = std::mem::MaybeUninit::<MEMORY_BASIC_INFORMATION>::zeroed();
    let queried = unsafe {
        VirtualQuery(
            address,
            information.as_mut_ptr(),
            std::mem::size_of::<MEMORY_BASIC_INFORMATION>(),
        )
    };
    if queried != std::mem::size_of::<MEMORY_BASIC_INFORMATION>() {
        return None;
    }
    let information = unsafe { information.assume_init() };
    if information.State != MEM_COMMIT
        || information.Protect & PAGE_NOACCESS != 0
        || information.Protect & PAGE_GUARD != 0
    {
        return None;
    }
    Some(unsafe { *(address as *const usize) })
}

fn restore_file_dialog_show_captures() -> bool {
    CAPTURE_SHUTDOWN_REQUESTED.store(true, Ordering::Release);
    let Ok(_restore_guard) = CAPTURE_RESTORE_LOCK.lock() else {
        return false;
    };
    while INSTALLING_DIALOG_CAPTURE.load(Ordering::Acquire) {
        std::thread::yield_now();
    }
    let firefox_plugins_restored =
        restore_firefox_spawn_plugin() && restore_firefox_dialog_factory_plugin();
    let shared_dialogs_revoked = revoke_shared_dialog_captures();

    let replacement = captured_file_dialog_show as *const () as usize;
    let restored = FILE_DIALOG_SHOW_VTABLES
        .get()
        .and_then(|captures| captures.lock().ok())
        .map(|mut captures| {
            restore_captured_vtable_slots(&mut captures, |slot, original| {
                let show_slot = unsafe { (slot as *mut *mut c_void).add(3) } as *mut usize;
                let Some(current) = readable_pointer(show_slot as *const c_void) else {
                    // The COM implementation may have freed a per-instance
                    // vtable before shutdown. Treat that capture as already
                    // retired instead of dereferencing freed memory.
                    return true;
                };
                match capture_slot_restore_action(current, replacement, original) {
                    CaptureSlotRestoreAction::AlreadyRestored => return true,
                    CaptureSlotRestoreAction::Conflict => return false,
                    CaptureSlotRestoreAction::Restore => {}
                }
                let mut old_protection = 0u32;
                if unsafe {
                    VirtualProtect(
                        show_slot.cast(),
                        std::mem::size_of::<usize>(),
                        PAGE_READWRITE,
                        &mut old_protection,
                    )
                } == 0
                {
                    return false;
                }
                unsafe { *show_slot = original };
                let mut ignored = 0u32;
                (unsafe {
                    VirtualProtect(
                        show_slot.cast(),
                        std::mem::size_of::<usize>(),
                        old_protection,
                        &mut ignored,
                    )
                }) != 0
            })
        })
        .unwrap_or(true);

    if restored && firefox_plugins_restored {
        close_spawn_proof_mappings();
        CAPTURED_DIALOG_CLASSES.store(0, Ordering::Release);
        release_current_thread_dialogs();
    }
    restored && firefox_plugins_restored && shared_dialogs_revoked
}

fn restore_firefox_spawn_plugin() -> bool {
    let Ok(mut patches) = FIREFOX_SPAWN_API_PATCHES.lock() else {
        return false;
    };
    if patches.is_empty() {
        return true;
    }
    let mut failed = Vec::new();
    for (slot, original, replacement) in patches.drain(..) {
        let slot_pointer = slot as *mut usize;
        let Some(current) = readable_pointer(slot_pointer as *const c_void) else {
            continue;
        };
        if current == original {
            continue;
        }
        if current != replacement {
            failed.push((slot, original, replacement));
            continue;
        }
        let mut old_protection = 0u32;
        if unsafe {
            VirtualProtect(
                slot_pointer.cast(),
                std::mem::size_of::<usize>(),
                PAGE_READWRITE,
                &mut old_protection,
            )
        } == 0
        {
            failed.push((slot, original, replacement));
            continue;
        }
        unsafe { *slot_pointer = original };
        let mut ignored = 0u32;
        if unsafe {
            VirtualProtect(
                slot_pointer.cast(),
                std::mem::size_of::<usize>(),
                old_protection,
                &mut ignored,
            )
        } == 0
        {
            failed.push((slot, original, replacement));
        }
    }
    *patches = failed;
    if patches.is_empty() {
        close_spawn_proof_mappings();
        true
    } else {
        false
    }
}

fn restore_firefox_dialog_factory_plugin() -> bool {
    if !unregister_firefox_dll_notification() {
        return false;
    }
    let Ok(mut patch) = FIREFOX_CO_CREATE_INSTANCE_PATCH.lock() else {
        return false;
    };
    let Some((slot, original)) = *patch else {
        return true;
    };
    let slot = slot as *mut usize;
    let replacement = captured_co_create_instance as *const () as usize;
    let Some(current) = readable_pointer(slot as *const c_void) else {
        *patch = None;
        FIREFOX_CO_CREATE_INSTANCE_ORIGINAL.store(0, Ordering::Release);
        return true;
    };
    if current == original {
        *patch = None;
        FIREFOX_CO_CREATE_INSTANCE_ORIGINAL.store(0, Ordering::Release);
        return true;
    }
    if current != replacement {
        return false;
    }
    let mut old_protection = 0u32;
    if unsafe {
        VirtualProtect(
            slot.cast(),
            std::mem::size_of::<usize>(),
            PAGE_READWRITE,
            &mut old_protection,
        )
    } == 0
    {
        return false;
    }
    unsafe { *slot = original };
    let mut ignored = 0u32;
    if unsafe {
        VirtualProtect(
            slot.cast(),
            std::mem::size_of::<usize>(),
            old_protection,
            &mut ignored,
        )
    } == 0
    {
        return false;
    }
    *patch = None;
    FIREFOX_CO_CREATE_INSTANCE_ORIGINAL.store(0, Ordering::Release);
    true
}

fn close_spawn_proof_mappings() {
    let Some(mappings) = SPAWN_PROOF_MAPPINGS.get() else {
        return;
    };
    let Ok(mut mappings) = mappings.lock() else {
        return;
    };
    for mapping in mappings.drain(..) {
        unsafe { CloseHandle(mapping as HANDLE) };
    }
}

fn retain_spawn_proof_mapping(mapping: HANDLE) {
    if let Ok(mut mappings) = SPAWN_PROOF_MAPPINGS
        .get_or_init(|| Mutex::new(Vec::new()))
        .lock()
    {
        mappings.push(mapping as usize);
    } else {
        unsafe { CloseHandle(mapping) };
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum CaptureSlotRestoreAction {
    AlreadyRestored,
    Restore,
    Conflict,
}

fn capture_slot_restore_action(
    current: usize,
    replacement: usize,
    original: usize,
) -> CaptureSlotRestoreAction {
    if current == original {
        CaptureSlotRestoreAction::AlreadyRestored
    } else if current == replacement {
        CaptureSlotRestoreAction::Restore
    } else {
        CaptureSlotRestoreAction::Conflict
    }
}

fn restore_captured_vtable_slots<R>(captures: &mut Vec<(usize, usize)>, mut restore: R) -> bool
where
    R: FnMut(usize, usize) -> bool,
{
    let mut all_restored = true;
    captures.retain(|(slot, original)| {
        let restored = restore(*slot, *original);
        all_restored &= restored;
        !restored
    });
    all_restored
}

fn release_current_thread_dialogs() {
    let Some(dialogs) = FILE_DIALOGS.get() else {
        return;
    };
    let Ok(mut dialogs) = dialogs.lock() else {
        return;
    };
    let current_thread_id = unsafe { GetCurrentThreadId() };
    dialogs.retain(|stored| {
        if stored.1 != current_thread_id {
            return true;
        }
        unsafe { release_interface(stored.0 as *mut c_void) };
        false
    });
}

fn remember_file_dialog(dialog: *mut c_void) {
    let dialogs = FILE_DIALOGS.get_or_init(|| Mutex::new(Vec::new()));
    let Ok(mut dialogs) = dialogs.lock() else {
        unsafe { release_interface(dialog) };
        return;
    };
    if dialogs.iter().any(|stored| stored.0 == dialog as usize) {
        unsafe { release_interface(dialog) };
    } else {
        dialogs.push((dialog as usize, unsafe { GetCurrentThreadId() }));
    }
}

fn find_file_dialog_for_window(hwnd: HWND) -> Option<usize> {
    let dialogs = FILE_DIALOGS.get_or_init(|| Mutex::new(Vec::new()));
    let Ok(mut dialogs) = dialogs.lock() else {
        return None;
    };
    // Prefer a match on the dialog thread that is handling WM_COPYDATA, but
    // also accept a same-root match from another thread (browser hosts).
    let mut found_current_thread = None;
    let mut found_any_thread = None;
    let mut sole_current_thread_dialog = None;
    let mut current_thread_dialog_count = 0usize;
    let current_thread_id = unsafe { GetCurrentThreadId() };
    dialogs.retain(|stored| {
        let dialog = stored.0 as *mut c_void;
        if stored.1 == current_thread_id {
            current_thread_dialog_count += 1;
            sole_current_thread_dialog = Some(stored.0);
        }
        let Some(ole_window) = (unsafe { query_interface(dialog, &IID_IOLE_WINDOW) }) else {
            // Firefox can transiently reject IOleWindow while its native picker
            // is already modal. The retained IFileDialog remains valid until
            // captured Show returns, so keep it as a same-thread candidate.
            return true;
        };
        let mut dialog_hwnd: HWND = std::ptr::null_mut();
        let get_window: unsafe extern "system" fn(*mut c_void, *mut HWND) -> i32 =
            unsafe { interface_method(ole_window, 3) };
        let result = unsafe { get_window(ole_window, &mut dialog_hwnd) };
        unsafe { release_interface(ole_window) };
        if result < 0 || dialog_hwnd.is_null() {
            true
        } else {
            if dialog_window_matches(dialog_hwnd, hwnd) {
                if stored.1 == current_thread_id {
                    found_current_thread = Some(stored.0);
                } else if found_any_thread.is_none() {
                    found_any_thread = Some(stored.0);
                }
            }
            true
        }
    });
    found_current_thread.or(found_any_thread).or_else(|| {
        (current_thread_dialog_count == 1)
            .then_some(sole_current_thread_dialog)
            .flatten()
    })
}

fn dialog_window_matches(dialog_hwnd: HWND, target_hwnd: HWND) -> bool {
    if dialog_hwnd.is_null() || target_hwnd.is_null() {
        return false;
    }
    if dialog_hwnd == target_hwnd {
        return true;
    }

    // Browser dialogs sometimes surface a child control as the jump target while
    // IOleWindow::GetWindow returns the #32770 root (or vice versa).
    let dialog_root = unsafe { GetAncestor(dialog_hwnd, GA_ROOT) };
    let target_root = unsafe { GetAncestor(target_hwnd, GA_ROOT) };
    let dialog_root = if dialog_root.is_null() {
        dialog_hwnd
    } else {
        dialog_root
    };
    let target_root = if target_root.is_null() {
        target_hwnd
    } else {
        target_root
    };
    dialog_root == target_root
}

unsafe fn query_interface(instance: *mut c_void, iid: &Guid) -> Option<*mut c_void> {
    if instance.is_null() {
        return None;
    }
    let query: unsafe extern "system" fn(*mut c_void, *const Guid, *mut *mut c_void) -> i32 =
        unsafe { interface_method(instance, 0) };
    let mut result = std::ptr::null_mut();
    (unsafe { query(instance, iid, &mut result) } >= 0 && !result.is_null()).then_some(result)
}

unsafe fn release_interface(instance: *mut c_void) {
    if !instance.is_null() {
        let release: unsafe extern "system" fn(*mut c_void) -> u32 =
            unsafe { interface_method(instance, 2) };
        unsafe { release(instance) };
    }
}

unsafe fn add_ref_interface(instance: *mut c_void) {
    let add_ref: unsafe extern "system" fn(*mut c_void) -> u32 =
        unsafe { interface_method(instance, 1) };
    unsafe { add_ref(instance) };
}

unsafe fn interface_method<T>(instance: *mut c_void, index: usize) -> T
where
    T: Copy,
{
    let vtable = unsafe { *(instance as *const *const *const c_void) };
    unsafe { std::mem::transmute_copy(&*vtable.add(index)) }
}

#[no_mangle]
pub unsafe extern "system" fn ListaryOpenHookProc(
    code: i32,
    w_param: WPARAM,
    l_param: LPARAM,
) -> LRESULT {
    let _active_hook_callback = ActiveCapturedShow::enter();
    if code >= 0 {
        if let Some(authorization) = active_or_preload_authorization() {
            if ensure_capture_lifecycle_started(authorization) {
                if !current_process_is_firefox_child() {
                    let _ = schedule_direct_dialog_capture(authorization);
                }
            }
        }
        let cwp = l_param as *const CWPSTRUCT;
        if !cwp.is_null() {
            let cwp = unsafe { &*cwp };
            if cwp.message == WM_COPYDATA {
                handle_dialog_message(cwp.hwnd, cwp.message, cwp.wParam, cwp.lParam);
            }
        }
    }

    unsafe { CallNextHookEx(std::ptr::null_mut(), code, w_param, l_param) }
}

fn preload_authorization() -> Option<PreloadAuthorization> {
    let current_process_id = unsafe { GetCurrentProcessId() };
    let root_process_id = current_process_session_root();
    std::iter::once(current_process_id)
        .chain(root_process_id.filter(|root| *root != current_process_id))
        .find_map(preload_authorization_for_process)
}

fn preload_authorization_for_process(process_id: u32) -> Option<PreloadAuthorization> {
    let architecture = if usize::BITS == 64 { "x64" } else { "x86" };
    let authorization_name = to_wide_null(&format!(
        "Local\\ListaryOpen.NativePreload.{architecture}.{process_id}"
    ));
    let mapping = unsafe { OpenFileMappingW(FILE_MAP_READ, 0, authorization_name.as_ptr()) };
    if mapping.is_null() {
        return None;
    }
    let view = unsafe {
        MapViewOfFile(
            mapping,
            FILE_MAP_READ,
            0,
            0,
            std::mem::size_of::<PreloadAuthorization>(),
        )
    };
    let authorization = if view.is_null() {
        None
    } else {
        let authorization = unsafe { *(view as *const PreloadAuthorization) };
        unsafe { UnmapViewOfFile(view) };
        (authorization.token != 0 && authorization.host_process_id != 0).then_some(authorization)
    };
    unsafe { CloseHandle(mapping) };
    authorization
}

fn preload_acknowledgement_name(process_id: u32, thread_id: u32, generation: u32) -> String {
    let architecture = if usize::BITS == 64 { "x64" } else { "x86" };
    format!("Local\\ListaryOpen.PreloadAck.{architecture}.{process_id}.{thread_id}.{generation}")
}

fn spawn_preload_acknowledgement_name(process_id: u32, thread_id: u32, generation: u32) -> String {
    let architecture = if usize::BITS == 64 { "x64" } else { "x86" };
    format!(
        "Local\\ListaryOpen.SpawnPreloadAck.{architecture}.{process_id}.{thread_id}.{generation}"
    )
}

fn report_preload_success_to_name(name: String, authorization: PreloadAuthorization) {
    let name = to_wide_null(&name);
    let mapping = unsafe { OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, 0, name.as_ptr()) };
    if mapping.is_null() {
        return;
    }
    let view = unsafe {
        MapViewOfFile(
            mapping,
            FILE_MAP_READ | FILE_MAP_WRITE,
            0,
            0,
            std::mem::size_of::<PreloadAcknowledgement>(),
        )
    };
    if !view.is_null() {
        let acknowledgement = view as *mut PreloadAcknowledgement;
        if unsafe { (*acknowledgement).token } == authorization.token {
            unsafe {
                std::ptr::write_volatile(&mut (*acknowledgement).status, PRELOAD_ACK_SUCCESS);
            }
        }
        unsafe { UnmapViewOfFile(view) };
    }
    unsafe { CloseHandle(mapping) };
}

fn report_preload_success(authorization: PreloadAuthorization) {
    report_preload_success_to_name(
        preload_acknowledgement_name(
            unsafe { GetCurrentProcessId() },
            unsafe { GetCurrentThreadId() },
            authorization.generation,
        ),
        authorization,
    );
}

fn report_spawn_preload_success(authorization: PreloadAuthorization) {
    report_preload_success_to_name(
        spawn_preload_acknowledgement_name(
            unsafe { GetCurrentProcessId() },
            unsafe { GetCurrentThreadId() },
            authorization.generation,
        ),
        authorization,
    );
}

fn current_thread_has_spawn_preload_authorization(authorization: PreloadAuthorization) -> bool {
    let name = to_wide_null(&spawn_preload_acknowledgement_name(
        unsafe { GetCurrentProcessId() },
        unsafe { GetCurrentThreadId() },
        authorization.generation,
    ));
    let mapping = unsafe { OpenFileMappingW(FILE_MAP_READ, 0, name.as_ptr()) };
    if mapping.is_null() {
        return false;
    }
    let view = unsafe {
        MapViewOfFile(
            mapping,
            FILE_MAP_READ,
            0,
            0,
            std::mem::size_of::<PreloadAcknowledgement>(),
        )
    };
    let authorized = if view.is_null() {
        false
    } else {
        let acknowledgement =
            unsafe { std::ptr::read_volatile(view as *const PreloadAcknowledgement) };
        unsafe { UnmapViewOfFile(view) };
        acknowledgement.token == authorization.token
    };
    unsafe { CloseHandle(mapping) };
    authorized
}

fn spawn_preload_host_handle(authorization: PreloadAuthorization) -> Option<HANDLE> {
    let name = to_wide_null(&spawn_preload_acknowledgement_name(
        unsafe { GetCurrentProcessId() },
        unsafe { GetCurrentThreadId() },
        authorization.generation,
    ));
    let mapping = unsafe { OpenFileMappingW(FILE_MAP_READ, 0, name.as_ptr()) };
    if mapping.is_null() {
        return None;
    }
    let view = unsafe {
        MapViewOfFile(
            mapping,
            FILE_MAP_READ,
            0,
            0,
            std::mem::size_of::<PreloadAcknowledgement>(),
        )
    };
    let handle = if view.is_null() {
        None
    } else {
        let acknowledgement =
            unsafe { std::ptr::read_volatile(view as *const PreloadAcknowledgement) };
        unsafe { UnmapViewOfFile(view) };
        (acknowledgement.token == authorization.token && acknowledgement.host_handle != 0)
            .then_some(acknowledgement.host_handle as HANDLE)
    };
    unsafe { CloseHandle(mapping) };
    handle
}

fn current_process_session_root() -> Option<u32> {
    let current_process_id = unsafe { GetCurrentProcessId() };
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
    if snapshot == INVALID_HANDLE_VALUE {
        return None;
    }
    let mut processes = Vec::new();
    let mut process: PROCESSENTRY32W = unsafe { std::mem::zeroed() };
    process.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
    let mut has_process = unsafe { Process32FirstW(snapshot, &mut process) } != 0;
    while has_process {
        let name_len = process
            .szExeFile
            .iter()
            .position(|unit| *unit == 0)
            .unwrap_or(process.szExeFile.len());
        processes.push((
            process.th32ProcessID,
            process.th32ParentProcessID,
            String::from_utf16_lossy(&process.szExeFile[..name_len]),
        ));
        has_process = unsafe { Process32NextW(snapshot, &mut process) } != 0;
    }
    unsafe { CloseHandle(snapshot) };

    let current = processes
        .iter()
        .find(|(process_id, _, _)| *process_id == current_process_id)?;
    let executable_name = current.2.clone();
    let mut root_process_id = current.0;
    let mut parent_process_id = current.1;
    for _ in 0..processes.len() {
        let Some(parent) = processes.iter().find(|(process_id, _, name)| {
            *process_id == parent_process_id && name.eq_ignore_ascii_case(&executable_name)
        }) else {
            break;
        };
        if parent.0 == root_process_id {
            break;
        }
        root_process_id = parent.0;
        parent_process_id = parent.1;
    }
    Some(root_process_id)
}

fn spawn_proof_name(parent_process_id: u32, child_process_id: u32, generation: u32) -> String {
    let architecture = if usize::BITS == 64 { "x64" } else { "x86" };
    format!(
        "Local\\ListaryOpen.SpawnProof.{architecture}.{parent_process_id}.{child_process_id}.{generation}"
    )
}

fn mark_spawn_proof_captured_show() {
    let Some(authorization) = active_or_preload_authorization() else {
        return;
    };
    let Some(parent_process_id) =
        current_firefox_parent_process_id().or_else(current_process_session_root)
    else {
        return;
    };
    let child_process_id = unsafe { GetCurrentProcessId() };
    if parent_process_id == child_process_id {
        return;
    }
    let name = to_wide_null(&spawn_proof_name(
        parent_process_id,
        child_process_id,
        authorization.generation,
    ));
    let mapping = unsafe { OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, 0, name.as_ptr()) };
    if mapping.is_null() {
        return;
    }
    let view = unsafe {
        MapViewOfFile(
            mapping,
            FILE_MAP_READ | FILE_MAP_WRITE,
            0,
            0,
            std::mem::size_of::<SpawnProof>(),
        )
    };
    if !view.is_null() {
        let proof = view as *mut SpawnProof;
        if unsafe {
            (*proof).token == authorization.token
                && (*proof).parent_process_id == parent_process_id
                && (*proof).child_process_id == child_process_id
        } {
            unsafe {
                std::ptr::write_volatile(&mut (*proof).captured_show_observed, 1);
            }
        }
        unsafe { UnmapViewOfFile(view) };
    }
    unsafe { CloseHandle(mapping) };
}

fn ensure_firefox_spawn_plugin_installed(authorization: PreloadAuthorization) -> bool {
    if !current_process_is_firefox() || current_process_is_firefox_child() {
        return true;
    }
    let modules = [
        (unsafe { GetModuleHandleW(std::ptr::null()) }, true),
        (
            unsafe { GetModuleHandleW(to_wide_null("xul.dll").as_ptr()) },
            true,
        ),
        (
            unsafe { GetModuleHandleW(to_wide_null("mozglue.dll").as_ptr()) },
            true,
        ),
        (
            unsafe { GetModuleHandleW(to_wide_null("nss3.dll").as_ptr()) },
            false,
        ),
    ];
    if modules
        .iter()
        .any(|(module, required)| *required && module.is_null())
    {
        return false;
    }
    let Ok(mut patches) = FIREFOX_SPAWN_API_PATCHES.lock() else {
        return false;
    };
    let targets = [
        (
            "CreateProcessW",
            captured_firefox_create_process as *const () as usize,
            true,
        ),
        (
            "CreateProcessAsUserW",
            captured_firefox_create_process_as_user as *const () as usize,
            true,
        ),
    ];
    let mut creation_api_patched = false;
    for (module, _) in modules {
        if module.is_null() {
            continue;
        }
        for (import_name, replacement, creation_api) in targets {
            let Some(slot) = (unsafe { find_import_address_slot(module, import_name) }) else {
                continue;
            };
            match unsafe { patch_firefox_spawn_api_slot(slot, replacement, &mut patches) } {
                Ok(()) => creation_api_patched |= creation_api,
                Err(()) => {
                    // A discovered import must remain ours for the entire
                    // authorized session; silently losing it would make the
                    // before-resume proof untrustworthy.
                    return false;
                }
            }
        }
    }
    creation_api_patched && active_authorization() == Some(authorization)
}

unsafe fn patch_firefox_spawn_api_slot(
    slot: *mut usize,
    replacement: usize,
    patches: &mut Vec<(usize, usize, usize)>,
) -> Result<(), ()> {
    if let Some((_, original, existing_replacement)) = patches
        .iter()
        .find(|(existing, _, _)| *existing == slot as usize)
    {
        return if *existing_replacement == replacement && unsafe { *slot } == replacement {
            Ok(())
        } else {
            let _ = original;
            Err(())
        };
    }
    let original = unsafe { *slot };
    if original == 0 || original == replacement {
        return Err(());
    }
    let mut old_protection = 0u32;
    if unsafe {
        VirtualProtect(
            slot.cast(),
            std::mem::size_of::<usize>(),
            PAGE_READWRITE,
            &mut old_protection,
        )
    } == 0
    {
        return Err(());
    }
    unsafe { *slot = replacement };
    let mut ignored = 0u32;
    if unsafe {
        VirtualProtect(
            slot.cast(),
            std::mem::size_of::<usize>(),
            old_protection,
            &mut ignored,
        )
    } == 0
    {
        unsafe { *slot = original };
        return Err(());
    }
    patches.push((slot as usize, original, replacement));
    Ok(())
}

fn ensure_firefox_dialog_factory_plugin_installed(authorization: PreloadAuthorization) -> bool {
    if !current_process_is_firefox_file_dialog_utility() {
        return false;
    }
    let xul = unsafe { GetModuleHandleW(to_wide_null("xul.dll").as_ptr()) };
    if xul.is_null() {
        return false;
    }
    install_firefox_dialog_factory_plugin(xul, authorization)
}

fn install_firefox_dialog_factory_plugin(
    xul: HMODULE,
    authorization: PreloadAuthorization,
) -> bool {
    let Ok(mut patch) = FIREFOX_CO_CREATE_INSTANCE_PATCH.lock() else {
        return false;
    };
    if patch.is_some() && FIREFOX_CO_CREATE_INSTANCE_ORIGINAL.load(Ordering::Acquire) != 0 {
        return true;
    }
    let Some(slot) = (unsafe { find_import_address_slot(xul, "CoCreateInstance") }) else {
        return false;
    };
    let original = unsafe { *slot };
    if original == 0 || original == captured_co_create_instance as *const () as usize {
        return false;
    }
    let mut old_protection = 0u32;
    if unsafe {
        VirtualProtect(
            slot.cast(),
            std::mem::size_of::<usize>(),
            PAGE_READWRITE,
            &mut old_protection,
        )
    } == 0
    {
        return false;
    }
    FIREFOX_CO_CREATE_INSTANCE_ORIGINAL.store(original, Ordering::Release);
    unsafe { *slot = captured_co_create_instance as *const () as usize };
    let mut ignored = 0u32;
    if unsafe {
        VirtualProtect(
            slot.cast(),
            std::mem::size_of::<usize>(),
            old_protection,
            &mut ignored,
        )
    } == 0
    {
        unsafe { *slot = original };
        FIREFOX_CO_CREATE_INSTANCE_ORIGINAL.store(0, Ordering::Release);
        return false;
    }
    *patch = Some((slot as usize, original));
    active_authorization() == Some(authorization)
}

unsafe extern "system" fn firefox_dll_notification(
    reason: u32,
    data: *const c_void,
    _context: *mut c_void,
) {
    const LDR_DLL_NOTIFICATION_REASON_LOADED: u32 = 1;
    if reason != LDR_DLL_NOTIFICATION_REASON_LOADED || data.is_null() {
        return;
    }
    let loaded = unsafe { &*(data as *const LdrDllLoadedNotificationData) };
    if loaded.base_dll_name.is_null() || loaded.dll_base.is_null() {
        return;
    }
    let name = unsafe { &*loaded.base_dll_name };
    if name.buffer.is_null() || name.length == 0 {
        return;
    }
    let units = unsafe { std::slice::from_raw_parts(name.buffer, usize::from(name.length) / 2) };
    if !String::from_utf16_lossy(units).eq_ignore_ascii_case("xul.dll") {
        return;
    }
    if let Some(authorization) = active_authorization() {
        let _ = install_firefox_dialog_factory_plugin(loaded.dll_base as HMODULE, authorization);
    }
}

unsafe fn register_dll_notification(cookie: *mut *mut c_void) -> i32 {
    type Callback = unsafe extern "system" fn(u32, *const c_void, *mut c_void);
    type Register =
        unsafe extern "system" fn(u32, Option<Callback>, *mut c_void, *mut *mut c_void) -> i32;
    let ntdll = unsafe { GetModuleHandleW(to_wide_null("ntdll.dll").as_ptr()) };
    if ntdll.is_null() {
        return -1;
    }
    let address = unsafe { GetProcAddress(ntdll, b"LdrRegisterDllNotification\0".as_ptr()) };
    if address.is_null() {
        return -1;
    }
    let register: Register = unsafe { std::mem::transmute(address) };
    unsafe {
        register(
            0,
            Some(firefox_dll_notification),
            std::ptr::null_mut(),
            cookie,
        )
    }
}

unsafe fn unregister_dll_notification(cookie: *mut c_void) -> i32 {
    type Unregister = unsafe extern "system" fn(*mut c_void) -> i32;
    let ntdll = unsafe { GetModuleHandleW(to_wide_null("ntdll.dll").as_ptr()) };
    if ntdll.is_null() {
        return -1;
    }
    let address = unsafe { GetProcAddress(ntdll, b"LdrUnregisterDllNotification\0".as_ptr()) };
    if address.is_null() {
        return -1;
    }
    let unregister: Unregister = unsafe { std::mem::transmute(address) };
    unsafe { unregister(cookie) }
}

fn ensure_firefox_dll_notification_registered(authorization: PreloadAuthorization) -> bool {
    let Ok(mut registered) = FIREFOX_DLL_NOTIFICATION_COOKIE.lock() else {
        return false;
    };
    if registered.is_some() {
        return active_authorization() == Some(authorization);
    }
    let mut cookie = std::ptr::null_mut();
    if unsafe { register_dll_notification(&mut cookie) } < 0 || cookie.is_null() {
        return false;
    }
    *registered = Some(cookie as usize);
    drop(registered);

    let xul = unsafe { GetModuleHandleW(to_wide_null("xul.dll").as_ptr()) };
    xul.is_null() || install_firefox_dialog_factory_plugin(xul, authorization)
}

fn unregister_firefox_dll_notification() -> bool {
    let Ok(mut registered) = FIREFOX_DLL_NOTIFICATION_COOKIE.lock() else {
        return false;
    };
    let Some(cookie) = registered.take() else {
        return true;
    };
    if unsafe { unregister_dll_notification(cookie as *mut c_void) } < 0 {
        *registered = Some(cookie);
        return false;
    }
    true
}

unsafe extern "system" fn captured_co_create_instance(
    clsid: *const Guid,
    outer: *mut c_void,
    context: u32,
    iid: *const Guid,
    object: *mut *mut c_void,
) -> i32 {
    let _active_factory_call = ActiveCapturedShow::enter();
    let result = unsafe { CoCreateInstance(clsid, outer, context, iid, object) };
    if result < 0 || clsid.is_null() || object.is_null() || unsafe { (*object).is_null() } {
        return result;
    }
    let Some(class_bit) = file_dialog_class_bit(unsafe { &*clsid }) else {
        return result;
    };
    if let Some(dialog) = unsafe { query_interface(*object, &IID_IFILE_DIALOG) } {
        let _ = unsafe { capture_file_dialog_show_vtable(dialog, class_bit) };
        unsafe { release_interface(dialog) };
    }
    result
}

fn file_dialog_class_bit(clsid: &Guid) -> Option<u8> {
    if *clsid == CLSID_FILE_OPEN_DIALOG {
        Some(0b01)
    } else if *clsid == CLSID_FILE_SAVE_DIALOG {
        Some(0b10)
    } else {
        None
    }
}

fn current_process_is_firefox() -> bool {
    let mut path = [0u16; 32_768];
    let length =
        unsafe { GetModuleFileNameW(std::ptr::null_mut(), path.as_mut_ptr(), path.len() as u32) };
    if length == 0 {
        return false;
    }
    String::from_utf16_lossy(&path[..length as usize])
        .rsplit(['\\', '/'])
        .next()
        .is_some_and(|name| name.eq_ignore_ascii_case("firefox.exe"))
}

fn current_process_is_firefox_child() -> bool {
    current_process_is_firefox() && current_firefox_parent_process_id().is_some()
}

fn current_process_is_firefox_file_dialog_utility() -> bool {
    if !current_process_is_firefox() {
        return false;
    }
    let arguments = process_command_line_arguments(unsafe { GetCurrentProcess() });
    let Some(parent_process_id) = firefox_parent_process_id(&arguments) else {
        return false;
    };
    is_firefox_file_dialog_spawn("", &arguments, parent_process_id)
}

fn current_firefox_parent_process_id() -> Option<u32> {
    let arguments = process_command_line_arguments(unsafe { GetCurrentProcess() });
    firefox_parent_process_id(&arguments)
}

fn firefox_parent_process_id(arguments: &[String]) -> Option<u32> {
    arguments.windows(2).find_map(|pair| {
        pair[0]
            .eq_ignore_ascii_case("-parentPid")
            .then(|| pair[1].parse::<u32>().ok())
            .flatten()
    })
}

unsafe fn find_import_address_slot(module: HMODULE, import_name: &str) -> Option<*mut usize> {
    let base = module as usize;
    let pe_offset = unsafe { *((base + 0x3c) as *const u32) } as usize;
    if unsafe { *((base + pe_offset) as *const u32) } != 0x0000_4550 {
        return None;
    }
    let optional = base + pe_offset + 24;
    let magic = unsafe { *(optional as *const u16) };
    let data_directory = optional
        + if magic == 0x20b {
            112
        } else if magic == 0x10b {
            96
        } else {
            return None;
        };
    let import_rva = unsafe { *((data_directory + 8) as *const u32) } as usize;
    if import_rva != 0 {
        let mut descriptor = base + import_rva;
        loop {
            let original_first_thunk = unsafe { *(descriptor as *const u32) } as usize;
            let first_thunk = unsafe { *((descriptor + 16) as *const u32) } as usize;
            if original_first_thunk == 0 && first_thunk == 0 {
                break;
            }
            let names = if original_first_thunk == 0 {
                first_thunk
            } else {
                original_first_thunk
            };
            let mut index = 0usize;
            loop {
                let name_entry = unsafe {
                    *((base + names + index * std::mem::size_of::<usize>()) as *const usize)
                };
                if name_entry == 0 {
                    break;
                }
                let ordinal_flag = 1usize << (usize::BITS - 1);
                if name_entry & ordinal_flag == 0 {
                    let name = unsafe { read_ascii_z((base + name_entry + 2) as *const u8, 256) };
                    if name == import_name {
                        return Some(
                            (base + first_thunk + index * std::mem::size_of::<usize>())
                                as *mut usize,
                        );
                    }
                }
                index += 1;
            }
            descriptor += 20;
        }
    }
    unsafe { find_delay_import_address_slot(base, data_directory, import_name) }
}

unsafe fn find_delay_import_address_slot(
    base: usize,
    data_directory: usize,
    import_name: &str,
) -> Option<*mut usize> {
    const DELAY_IMPORT_DIRECTORY_INDEX: usize = 13;
    let delay_rva =
        unsafe { *((data_directory + DELAY_IMPORT_DIRECTORY_INDEX * 8) as *const u32) } as usize;
    if delay_rva == 0 {
        return None;
    }
    let mut descriptor = base + delay_rva;
    loop {
        let attributes = unsafe { *(descriptor as *const u32) };
        let iat_value = unsafe { *((descriptor + 12) as *const u32) } as usize;
        let names_value = unsafe { *((descriptor + 16) as *const u32) } as usize;
        if attributes == 0 && iat_value == 0 && names_value == 0 {
            return None;
        }
        if iat_value != 0 && names_value != 0 {
            let values_are_rvas = attributes & 1 != 0;
            let iat = if values_are_rvas {
                base + iat_value
            } else {
                iat_value
            };
            let names = if values_are_rvas {
                base + names_value
            } else {
                names_value
            };
            let mut index = 0usize;
            loop {
                let name_entry =
                    unsafe { *((names + index * std::mem::size_of::<usize>()) as *const usize) };
                if name_entry == 0 {
                    break;
                }
                let ordinal_flag = 1usize << (usize::BITS - 1);
                if name_entry & ordinal_flag == 0 {
                    let name_address = if values_are_rvas {
                        base + name_entry
                    } else {
                        name_entry
                    };
                    let name = unsafe { read_ascii_z((name_address + 2) as *const u8, 256) };
                    if name == import_name {
                        return Some((iat + index * std::mem::size_of::<usize>()) as *mut usize);
                    }
                }
                index += 1;
            }
        }
        descriptor += 32;
    }
}

unsafe fn read_ascii_z(pointer: *const u8, maximum: usize) -> String {
    let mut bytes = Vec::new();
    for index in 0..maximum {
        let byte = unsafe { *pointer.add(index) };
        if byte == 0 {
            break;
        }
        bytes.push(byte);
    }
    String::from_utf8_lossy(&bytes).into_owned()
}

unsafe fn read_utf16_z(pointer: *const u16, maximum: usize) -> String {
    if pointer.is_null() {
        return String::new();
    }
    let mut units = Vec::new();
    for index in 0..maximum {
        let unit = unsafe { *pointer.add(index) };
        if unit == 0 {
            break;
        }
        units.push(unit);
    }
    String::from_utf16_lossy(&units)
}

fn is_firefox_file_dialog_spawn(
    application_name: &str,
    arguments: &[String],
    parent_process_id: u32,
) -> bool {
    let executable = if application_name.trim().is_empty() {
        arguments.first().map(String::as_str).unwrap_or_default()
    } else {
        application_name
    };
    let value_after = |name: &str| {
        arguments.windows(2).find_map(|pair| {
            pair[0]
                .eq_ignore_ascii_case(name)
                .then_some(pair[1].as_str())
        })
    };
    firefox_executable_matches(executable)
        && arguments
            .iter()
            .any(|value| value.eq_ignore_ascii_case("-contentproc"))
        && value_after("-parentPid").and_then(|value| value.parse::<u32>().ok())
            == Some(parent_process_id)
        && arguments
            .iter()
            .any(|value| value.eq_ignore_ascii_case("utility"))
}

fn firefox_executable_matches(executable: &str) -> bool {
    executable
        .trim()
        .trim_matches('"')
        .rsplit(['\\', '/'])
        .next()
        .is_some_and(|name| name.eq_ignore_ascii_case("firefox.exe"))
}

unsafe fn command_line_arguments(command_line: *const u16) -> Vec<String> {
    if command_line.is_null() {
        return Vec::new();
    }
    let mut count = 0i32;
    let raw = unsafe { CommandLineToArgvW(command_line, &mut count) };
    if raw.is_null() || count <= 0 {
        return Vec::new();
    }
    let arguments = (0..count as usize)
        .map(|index| unsafe { read_utf16_z(*raw.add(index), 32_768) })
        .collect();
    unsafe { LocalFree(raw.cast()) };
    arguments
}

fn firefox_file_dialog_creation_requested(
    application_name: *const u16,
    command_line: *const u16,
) -> bool {
    let application_name = unsafe { read_utf16_z(application_name, 32_768) };
    let arguments = unsafe { command_line_arguments(command_line) };
    is_firefox_file_dialog_spawn(&application_name, &arguments, unsafe {
        GetCurrentProcessId()
    })
}

fn firefox_precapture_creation_flags(creation_flags: u32, precapture: bool) -> (u32, bool) {
    if !precapture {
        return (creation_flags, false);
    }
    (
        creation_flags | CREATE_SUSPENDED,
        creation_flags & CREATE_SUSPENDED == 0,
    )
}

unsafe fn prepare_created_firefox_utility(
    process_information: *mut ProcessInformation,
    authorization: PreloadAuthorization,
) {
    if process_information.is_null() {
        return;
    }
    let information = unsafe { &*process_information };
    let parent_process_id = unsafe { GetCurrentProcessId() };
    let arguments = process_command_line_arguments(information.process);
    if spawned_child_identity_matches(information, parent_process_id)
        && is_firefox_file_dialog_spawn("", &arguments, parent_process_id)
    {
        let _ = unsafe { prepare_spawned_firefox_utility(information, authorization) };
    }
}

#[allow(clippy::too_many_arguments)]
unsafe extern "system" fn captured_firefox_create_process(
    application_name: *const u16,
    command_line: *mut u16,
    process_attributes: *const c_void,
    thread_attributes: *const c_void,
    inherit_handles: i32,
    creation_flags: u32,
    environment: *const c_void,
    current_directory: *const u16,
    startup_information: *const c_void,
    process_information: *mut ProcessInformation,
) -> i32 {
    let _active_spawn_callback = ActiveCapturedShow::enter();
    let authorization = active_or_preload_authorization();
    let precapture = authorization.is_some()
        && firefox_file_dialog_creation_requested(application_name, command_line);
    let (effective_flags, added_suspension) =
        firefox_precapture_creation_flags(creation_flags, precapture);
    let result = unsafe {
        CreateProcessW(
            application_name,
            command_line,
            process_attributes,
            thread_attributes,
            inherit_handles,
            effective_flags,
            environment,
            current_directory,
            startup_information,
            process_information,
        )
    };
    if result != 0 && precapture {
        unsafe { prepare_created_firefox_utility(process_information, authorization.unwrap()) };
        if added_suspension && !process_information.is_null() {
            unsafe { ResumeThread((*process_information).thread) };
        }
    }
    result
}

#[allow(clippy::too_many_arguments)]
unsafe extern "system" fn captured_firefox_create_process_as_user(
    token: HANDLE,
    application_name: *const u16,
    command_line: *mut u16,
    process_attributes: *const c_void,
    thread_attributes: *const c_void,
    inherit_handles: i32,
    creation_flags: u32,
    environment: *const c_void,
    current_directory: *const u16,
    startup_information: *const c_void,
    process_information: *mut ProcessInformation,
) -> i32 {
    let _active_spawn_callback = ActiveCapturedShow::enter();
    let authorization = active_or_preload_authorization();
    let precapture = authorization.is_some()
        && firefox_file_dialog_creation_requested(application_name, command_line);
    let (effective_flags, added_suspension) =
        firefox_precapture_creation_flags(creation_flags, precapture);
    let result = unsafe {
        CreateProcessAsUserW(
            token,
            application_name,
            command_line,
            process_attributes,
            thread_attributes,
            inherit_handles,
            effective_flags,
            environment,
            current_directory,
            startup_information,
            process_information,
        )
    };
    if result != 0 && precapture {
        unsafe { prepare_created_firefox_utility(process_information, authorization.unwrap()) };
        if added_suspension && !process_information.is_null() {
            unsafe { ResumeThread((*process_information).thread) };
        }
    }
    result
}

fn process_command_line_arguments(process: HANDLE) -> Vec<String> {
    const PROCESS_COMMAND_LINE_INFORMATION: u32 = 60;
    let mut required = 0u32;
    unsafe {
        NtQueryInformationProcess(
            process,
            PROCESS_COMMAND_LINE_INFORMATION,
            std::ptr::null_mut(),
            0,
            &mut required,
        )
    };
    if required < std::mem::size_of::<UnicodeString>() as u32 || required > 1_048_576 {
        return Vec::new();
    }
    let mut buffer = vec![0u8; required as usize];
    if unsafe {
        NtQueryInformationProcess(
            process,
            PROCESS_COMMAND_LINE_INFORMATION,
            buffer.as_mut_ptr().cast(),
            required,
            &mut required,
        )
    } < 0
    {
        return Vec::new();
    }
    let command_line = unsafe { &*(buffer.as_ptr() as *const UnicodeString) };
    if command_line.buffer.is_null() || command_line.length == 0 {
        return Vec::new();
    }
    let units = unsafe {
        std::slice::from_raw_parts(command_line.buffer, usize::from(command_line.length) / 2)
    };
    let mut owned = units.to_vec();
    owned.push(0);
    unsafe { command_line_arguments(owned.as_ptr()) }
}

fn spawned_child_identity_matches(
    information: &ProcessInformation,
    expected_parent_process_id: u32,
) -> bool {
    let mut parent_image = [0u16; 32_768];
    let parent_length = unsafe {
        GetModuleFileNameW(
            std::ptr::null_mut(),
            parent_image.as_mut_ptr(),
            parent_image.len() as u32,
        )
    };
    let mut child_image = [0u16; 32_768];
    let mut child_length = child_image.len() as u32;
    if parent_length == 0
        || unsafe {
            QueryFullProcessImageNameW(
                information.process,
                0,
                child_image.as_mut_ptr(),
                &mut child_length,
            )
        } == 0
        || !String::from_utf16_lossy(&parent_image[..parent_length as usize]).eq_ignore_ascii_case(
            &String::from_utf16_lossy(&child_image[..child_length as usize]),
        )
    {
        return false;
    }

    let mut basic: ProcessBasicInformation = unsafe { std::mem::zeroed() };
    let mut returned = 0u32;
    (unsafe {
        NtQueryInformationProcess(
            information.process,
            0,
            (&mut basic as *mut ProcessBasicInformation).cast(),
            std::mem::size_of::<ProcessBasicInformation>() as u32,
            &mut returned,
        )
    }) >= 0
        && basic.unique_process_id == information.process_id as usize
        && basic.inherited_from_unique_process_id == expected_parent_process_id as usize
}

fn module_name(units: &[u16]) -> String {
    let length = units
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(units.len());
    String::from_utf16_lossy(&units[..length])
}

fn find_remote_module_base(process_id: u32, expected_name: &str) -> Option<usize> {
    for _ in 0..8 {
        let snapshot = unsafe {
            CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, process_id)
        };
        if snapshot == INVALID_HANDLE_VALUE {
            std::thread::yield_now();
            continue;
        }
        let mut module: MODULEENTRY32W = unsafe { std::mem::zeroed() };
        module.dwSize = std::mem::size_of::<MODULEENTRY32W>() as u32;
        let mut has_module = unsafe { Module32FirstW(snapshot, &mut module) } != 0;
        while has_module {
            if module_name(&module.szModule).eq_ignore_ascii_case(expected_name) {
                let base = module.modBaseAddr as usize;
                unsafe { CloseHandle(snapshot) };
                return Some(base);
            }
            has_module = unsafe { Module32NextW(snapshot, &mut module) } != 0;
        }
        unsafe { CloseHandle(snapshot) };
        std::thread::yield_now();
    }
    None
}

fn find_remote_module_base_by_path(process_id: u32, expected_path: &str) -> Option<usize> {
    for _ in 0..8 {
        let snapshot = unsafe {
            CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, process_id)
        };
        if snapshot == INVALID_HANDLE_VALUE {
            std::thread::yield_now();
            continue;
        }
        let mut module: MODULEENTRY32W = unsafe { std::mem::zeroed() };
        module.dwSize = std::mem::size_of::<MODULEENTRY32W>() as u32;
        let mut has_module = unsafe { Module32FirstW(snapshot, &mut module) } != 0;
        while has_module {
            let loaded_path = module_name(&module.szExePath);
            if folder_paths_match(&loaded_path, expected_path) {
                let base = module.modBaseAddr as usize;
                unsafe { CloseHandle(snapshot) };
                return Some(base);
            }
            has_module = unsafe { Module32NextW(snapshot, &mut module) } != 0;
        }
        unsafe { CloseHandle(snapshot) };
        std::thread::yield_now();
    }
    None
}

fn adjacent_hook_runtime_path(hook_path: &str) -> Option<String> {
    let parent = std::path::Path::new(hook_path).parent()?;
    Some(parent.join("libunwind.dll").to_string_lossy().into_owned())
}

fn local_module_and_path(address: *const c_void) -> Option<(HMODULE, String)> {
    const FROM_ADDRESS: u32 = 0x0000_0004;
    let mut module = std::ptr::null_mut();
    if unsafe { GetModuleHandleExW(FROM_ADDRESS, address.cast(), &mut module) } == 0 {
        return None;
    }
    let mut path = [0u16; 32_768];
    let length = unsafe { GetModuleFileNameW(module, path.as_mut_ptr(), path.len() as u32) };
    if length == 0 || length as usize >= path.len() {
        unsafe { FreeLibrary(module) };
        return None;
    }
    Some((module, String::from_utf16_lossy(&path[..length as usize])))
}

fn remote_function_address(process_id: u32, function: *const c_void) -> Option<*const c_void> {
    let (local_module, module_path) = local_module_and_path(function)?;
    let module_name = module_path.rsplit(['\\', '/']).next()?.to_string();
    let offset = (function as usize).checked_sub(local_module as usize)?;
    unsafe { FreeLibrary(local_module) };
    let remote_module = find_remote_module_base(process_id, &module_name)?;
    Some((remote_module + offset) as *const c_void)
}

unsafe fn write_remote_value<T>(process: HANDLE, value: &T) -> Option<*mut c_void> {
    let size = std::mem::size_of::<T>();
    let remote = unsafe {
        VirtualAllocEx(
            process,
            std::ptr::null(),
            size,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE,
        )
    };
    if remote.is_null() {
        return None;
    }
    let mut written = 0usize;
    if unsafe {
        WriteProcessMemory(
            process,
            remote,
            (value as *const T).cast(),
            size,
            &mut written,
        )
    } == 0
        || written != size
    {
        unsafe { VirtualFreeEx(process, remote, 0, MEM_RELEASE) };
        return None;
    }
    Some(remote)
}

unsafe fn write_remote_slice<T>(process: HANDLE, values: &[T]) -> Option<*mut c_void> {
    let size = std::mem::size_of_val(values);
    if size == 0 {
        return None;
    }
    let remote = unsafe {
        VirtualAllocEx(
            process,
            std::ptr::null(),
            size,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE,
        )
    };
    if remote.is_null() {
        return None;
    }
    let mut written = 0usize;
    if unsafe { WriteProcessMemory(process, remote, values.as_ptr().cast(), size, &mut written) }
        == 0
        || written != size
    {
        unsafe { VirtualFreeEx(process, remote, 0, MEM_RELEASE) };
        return None;
    }
    Some(remote)
}

unsafe fn run_remote_thread(
    process: HANDLE,
    start_address: *const c_void,
    parameter: *const c_void,
) -> Option<u32> {
    let thread = unsafe {
        CreateRemoteThread(
            process,
            std::ptr::null(),
            0,
            start_address,
            parameter,
            0,
            std::ptr::null_mut(),
        )
    };
    if thread.is_null() {
        return None;
    }
    const WAIT_OBJECT_0: u32 = 0;
    let completed =
        unsafe { WaitForSingleObject(thread, REMOTE_INITIALIZATION_TIMEOUT_MS) } == WAIT_OBJECT_0;
    let mut exit_code = 0u32;
    let read_exit = completed && unsafe { GetExitCodeThread(thread, &mut exit_code) } != 0;
    unsafe { CloseHandle(thread) };
    read_exit.then_some(exit_code)
}

unsafe fn duplicate_host_handle_into_child(
    child_process: HANDLE,
    authorization: PreloadAuthorization,
) -> Option<HANDLE> {
    const PROCESS_SYNCHRONIZE: u32 = 0x0010_0000;
    let host = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, 0, authorization.host_process_id) };
    if host.is_null() {
        return None;
    }
    let mut child_host = std::ptr::null_mut();
    let duplicated = unsafe {
        DuplicateHandle(
            GetCurrentProcess(),
            host,
            child_process,
            &mut child_host,
            PROCESS_SYNCHRONIZE,
            0,
            0,
        )
    } != 0;
    unsafe { CloseHandle(host) };
    duplicated.then_some(child_host)
}

unsafe fn close_remote_handle(process: HANDLE, remote_handle: HANDLE) {
    if remote_handle.is_null() {
        return;
    }
    const DUPLICATE_CLOSE_SOURCE: u32 = 0x0000_0001;
    const DUPLICATE_SAME_ACCESS: u32 = 0x0000_0002;
    let mut local_handle = std::ptr::null_mut();
    if unsafe {
        DuplicateHandle(
            process,
            remote_handle,
            GetCurrentProcess(),
            &mut local_handle,
            0,
            0,
            DUPLICATE_CLOSE_SOURCE | DUPLICATE_SAME_ACCESS,
        )
    } != 0
        && !local_handle.is_null()
    {
        unsafe { CloseHandle(local_handle) };
    }
}

unsafe fn unload_remote_module(process: HANDLE, process_id: u32, module: usize) {
    let kernel32 = unsafe { GetModuleHandleW(to_wide_null("kernel32.dll").as_ptr()) };
    if kernel32.is_null() {
        return;
    }
    let free_library = unsafe { GetProcAddress(kernel32, b"FreeLibrary\0".as_ptr()) };
    if free_library.is_null() {
        return;
    }
    let remote_free_library =
        remote_function_address(process_id, free_library).unwrap_or(free_library);
    let _ = unsafe { run_remote_thread(process, remote_free_library, module as *const c_void) };
}

unsafe fn load_remote_library(
    process: HANDLE,
    remote_load_library: *const c_void,
    path: &str,
) -> bool {
    let wide_path = to_wide_null(path);
    let Some(remote_path) = (unsafe { write_remote_slice(process, &wide_path) }) else {
        return false;
    };
    let result = unsafe { run_remote_thread(process, remote_load_library, remote_path) };
    unsafe { VirtualFreeEx(process, remote_path, 0, MEM_RELEASE) };
    result.is_some_and(|module| module != 0)
}

unsafe fn inject_firefox_utility_before_resume(
    information: &ProcessInformation,
    authorization: PreloadAuthorization,
) -> bool {
    let (hook_module, hook_path) = match local_module_and_path(
        ListaryOpenInitializeFirefoxUtility as *const () as *const c_void,
    ) {
        Some(module) => module,
        None => return false,
    };
    let hook_name = match hook_path.rsplit(['\\', '/']).next() {
        Some(name) => name.to_string(),
        None => {
            unsafe { FreeLibrary(hook_module) };
            return false;
        }
    };
    let runtime_path = match adjacent_hook_runtime_path(&hook_path) {
        Some(path) => path,
        None => {
            unsafe { FreeLibrary(hook_module) };
            return false;
        }
    };
    let runtime_name = match runtime_path.rsplit(['\\', '/']).next() {
        Some(name) => name.to_string(),
        None => {
            unsafe { FreeLibrary(hook_module) };
            return false;
        }
    };

    let kernel32 = unsafe { GetModuleHandleW(to_wide_null("kernel32.dll").as_ptr()) };
    let load_library = if kernel32.is_null() {
        std::ptr::null_mut()
    } else {
        unsafe { GetProcAddress(kernel32, b"LoadLibraryW\0".as_ptr()) }
    };
    let remote_load_library = (!load_library.is_null()).then(|| {
        remote_function_address(information.process_id, load_library)
            // Windows maps the shared system DLL image at one session-wide
            // address for same-architecture processes. A just-created,
            // suspended process can precede Toolhelp's module-list snapshot,
            // so the already resolved shared address is the safe bootstrap.
            .unwrap_or(load_library)
    });
    let Some(remote_load_library) = remote_load_library else {
        unsafe { FreeLibrary(hook_module) };
        return false;
    };

    // A newly-created Firefox utility process does not use the Hook.dll
    // directory when resolving imports for a remote LoadLibraryW call. Load the
    // adjacent packaged runtime by its full path first so Hook.dll cannot bind
    // to a developer-toolchain copy found through PATH (or fail on a clean PC).
    if !unsafe { load_remote_library(information.process, remote_load_library, &runtime_path) } {
        unsafe { FreeLibrary(hook_module) };
        return false;
    }
    let Some(remote_runtime_module) =
        find_remote_module_base_by_path(information.process_id, &runtime_path)
    else {
        if let Some(module) = find_remote_module_base(information.process_id, &runtime_name) {
            unsafe { unload_remote_module(information.process, information.process_id, module) };
        }
        unsafe { FreeLibrary(hook_module) };
        return false;
    };

    if !unsafe { load_remote_library(information.process, remote_load_library, &hook_path) } {
        unsafe {
            unload_remote_module(
                information.process,
                information.process_id,
                remote_runtime_module,
            );
            FreeLibrary(hook_module);
        }
        return false;
    }

    let Some(remote_hook_module) = find_remote_module_base(information.process_id, &hook_name)
    else {
        unsafe {
            unload_remote_module(
                information.process,
                information.process_id,
                remote_runtime_module,
            );
            FreeLibrary(hook_module);
        }
        return false;
    };
    // Hook.dll now owns its dependency reference. Balance only the explicit
    // bootstrap reference so normal Hook.dll teardown also unloads the runtime.
    unsafe {
        unload_remote_module(
            information.process,
            information.process_id,
            remote_runtime_module,
        )
    };
    let initializer_offset =
        ListaryOpenInitializeFirefoxUtility as *const () as usize - hook_module as usize;
    let remote_initializer = (remote_hook_module + initializer_offset) as *const c_void;
    unsafe { FreeLibrary(hook_module) };

    let Some(child_host) =
        (unsafe { duplicate_host_handle_into_child(information.process, authorization) })
    else {
        unsafe {
            unload_remote_module(
                information.process,
                information.process_id,
                remote_hook_module,
            )
        };
        return false;
    };
    let initialization = RemoteFirefoxInitialization {
        authorization,
        host_handle: child_host as usize,
    };
    let Some(remote_initialization) =
        (unsafe { write_remote_value(information.process, &initialization) })
    else {
        unsafe {
            close_remote_handle(information.process, child_host);
            unload_remote_module(
                information.process,
                information.process_id,
                remote_hook_module,
            );
        }
        return false;
    };
    let initialization_result = unsafe {
        run_remote_thread(
            information.process,
            remote_initializer,
            remote_initialization,
        )
    };
    let initialized = initialization_result == Some(1);
    unsafe { VirtualFreeEx(information.process, remote_initialization, 0, MEM_RELEASE) };
    if !initialized {
        unsafe {
            unload_remote_module(
                information.process,
                information.process_id,
                remote_hook_module,
            )
        };
    }
    initialized
}

#[no_mangle]
pub unsafe extern "system" fn ListaryOpenInitializeFirefoxUtility(context: *mut c_void) -> u32 {
    if context.is_null() {
        return 0;
    }
    let initialization = unsafe { *(context as *const RemoteFirefoxInitialization) };
    if !current_process_is_firefox_file_dialog_utility()
        || initialization.authorization.token == 0
        || initialization.authorization.generation == 0
        || initialization.host_handle == 0
    {
        if initialization.host_handle != 0 {
            unsafe { CloseHandle(initialization.host_handle as HANDLE) };
        }
        return 0;
    }
    if let Ok(mut active) = ACTIVE_PRELOAD_AUTHORIZATION.lock() {
        *active = Some(initialization.authorization);
    } else {
        unsafe { CloseHandle(initialization.host_handle as HANDLE) };
        return 0;
    }
    CAPTURE_SHUTDOWN_REQUESTED.store(false, Ordering::Release);
    if !ensure_firefox_dll_notification_registered(initialization.authorization) {
        if let Ok(mut active) = ACTIVE_PRELOAD_AUTHORIZATION.lock() {
            *active = None;
        }
        unsafe { CloseHandle(initialization.host_handle as HANDLE) };
        return 0;
    }
    if !ensure_capture_lifecycle_started_with_host(
        initialization.authorization,
        initialization.host_handle as HANDLE,
    ) {
        let _ = unregister_firefox_dll_notification();
        if let Ok(mut active) = ACTIVE_PRELOAD_AUTHORIZATION.lock() {
            *active = None;
        }
        return 0;
    }

    // LoadLibraryW supplied the bootstrap reference. The lifecycle watcher now
    // owns an independent reference, so hand the bootstrap reference back
    // before this remote initialization thread exits.
    const FROM_ADDRESS: u32 = 0x0000_0004;
    const UNCHANGED_REFCOUNT: u32 = 0x0000_0002;
    let mut module = std::ptr::null_mut();
    if unsafe {
        GetModuleHandleExW(
            FROM_ADDRESS | UNCHANGED_REFCOUNT,
            ListaryOpenInitializeFirefoxUtility as *const () as *const u16,
            &mut module,
        )
    } != 0
        && !module.is_null()
    {
        unsafe { FreeLibrary(module) };
    }
    1
}

unsafe fn prepare_spawned_firefox_utility(
    information: &ProcessInformation,
    authorization: PreloadAuthorization,
) -> bool {
    let Some(child_creation_time) = (unsafe { process_creation_time(information.process) }) else {
        return false;
    };
    let proof_name = to_wide_null(&spawn_proof_name(
        unsafe { GetCurrentProcessId() },
        information.process_id,
        authorization.generation,
    ));
    let proof_mapping = unsafe {
        CreateFileMappingW(
            INVALID_HANDLE_VALUE,
            std::ptr::null(),
            PAGE_READWRITE,
            0,
            std::mem::size_of::<SpawnProof>() as u32,
            proof_name.as_ptr(),
        )
    };
    if proof_mapping.is_null() {
        return false;
    }
    let proof_view = unsafe {
        MapViewOfFile(
            proof_mapping,
            FILE_MAP_READ | FILE_MAP_WRITE,
            0,
            0,
            std::mem::size_of::<SpawnProof>(),
        )
    };
    if proof_view.is_null() {
        unsafe { CloseHandle(proof_mapping) };
        return false;
    }
    let proof = proof_view as *mut SpawnProof;
    unsafe {
        *proof = SpawnProof {
            token: authorization.token,
            child_creation_time,
            generation: authorization.generation,
            parent_process_id: GetCurrentProcessId(),
            child_process_id: information.process_id,
            child_thread_id: information.thread_id,
            status: SPAWN_PROOF_PENDING,
            installed_before_resume: 0,
            captured_show_observed: 0,
        };
    }
    let initialized = unsafe { inject_firefox_utility_before_resume(information, authorization) };
    unsafe {
        std::ptr::write_volatile(
            &mut (*proof).installed_before_resume,
            u32::from(initialized),
        );
        std::ptr::write_volatile(
            &mut (*proof).status,
            completed_spawn_proof_status(initialized),
        );
        UnmapViewOfFile(proof_view);
    }
    retain_spawn_proof_mapping(proof_mapping);
    initialized
}

fn completed_spawn_proof_status(success: bool) -> u32 {
    if success {
        SPAWN_PROOF_SUCCESS
    } else {
        SPAWN_PROOF_FAILED
    }
}

unsafe fn process_creation_time(process: HANDLE) -> Option<u64> {
    let mut creation = FileTime { low: 0, high: 0 };
    let mut exit = FileTime { low: 0, high: 0 };
    let mut kernel = FileTime { low: 0, high: 0 };
    let mut user = FileTime { low: 0, high: 0 };
    (unsafe { GetProcessTimes(process, &mut creation, &mut exit, &mut kernel, &mut user) } != 0)
        .then_some((u64::from(creation.high) << 32) | u64::from(creation.low))
}

#[no_mangle]
pub unsafe extern "system" fn ListaryOpenPreloadHookProc(
    code: i32,
    w_param: WPARAM,
    l_param: LPARAM,
) -> LRESULT {
    let _active_hook_callback = ActiveCapturedShow::enter();
    if code >= 0 {
        if let Some(authorization) = active_or_preload_authorization() {
            if !preload_callback_armed(authorization)
                && ensure_capture_lifecycle_started(authorization)
            {
                let firefox_child = current_process_is_firefox_child();
                if firefox_child && current_process_is_firefox_file_dialog_utility() {
                    if ensure_firefox_dialog_factory_plugin_installed(authorization) {
                        // A newly spawned utility reports through its
                        // parent-created mapping. An existing utility reports
                        // through HookHost's ordinary preload mapping. Both
                        // paths arm the factory before any dialog is observed.
                        if current_thread_has_spawn_preload_authorization(authorization) {
                            report_spawn_preload_success(authorization);
                        } else {
                            report_preload_success(authorization);
                        }
                        mark_preload_callback_armed(authorization);
                    }
                } else if firefox_child {
                    // Other Firefox children never receive the dialog-factory
                    // patch. Acknowledge the bounded process-family preload so
                    // HookHost can retire their temporary thread hooks.
                    report_preload_success(authorization);
                    mark_preload_callback_armed(authorization);
                } else if !firefox_child && current_process_is_firefox() {
                    if ensure_firefox_spawn_plugin_installed(authorization) {
                        report_preload_success(authorization);
                        mark_preload_callback_armed(authorization);
                    }
                } else if !firefox_child {
                    // The target-thread timer reports success only after both
                    // dialog classes have been captured in this STA.
                    let _ = schedule_direct_dialog_capture(authorization);
                }
            }
        }
    }
    unsafe { CallNextHookEx(std::ptr::null_mut(), code, w_param, l_param) }
}

fn is_supported_dialog(hwnd: HWND) -> bool {
    is_supported_dialog_from_probe(class_name(hwnd), || window_text(hwnd), || false)
}

fn is_supported_dialog_from_probe<T, A>(
    class_name: Option<String>,
    title: T,
    has_address_control: A,
) -> bool
where
    T: FnOnce() -> String,
    A: FnOnce() -> bool,
{
    let Some(class_name) = class_name else {
        return false;
    };
    if class_name != DIALOG_CLASS {
        return false;
    }

    is_supported_dialog_shape(&class_name, &title(), has_address_control())
}

fn is_supported_dialog_shape(class_name: &str, _title: &str, _has_address_control: bool) -> bool {
    class_name == DIALOG_CLASS
}

fn is_legacy_browse_for_folder_dialog(class_name: &str, title: &str) -> bool {
    class_name == DIALOG_CLASS && title.trim().eq_ignore_ascii_case("Browse For Folder")
}

fn is_legacy_browse_for_folder_window(hwnd: HWND) -> bool {
    class_name(hwnd).as_deref().is_some_and(|class_name| {
        is_legacy_browse_for_folder_dialog(class_name, &window_text(hwnd))
    })
}

fn class_name(hwnd: HWND) -> Option<String> {
    let mut class_buffer = [0u16; 64];
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

fn handle_dialog_message(hwnd: HWND, message: u32, w_param: WPARAM, l_param: LPARAM) {
    if let Some(command) = decode_cleanup_copydata_message(message, l_param) {
        let sender_hwnd = w_param as HWND;
        let mut sender_process_id = 0u32;
        let sender_thread_id = if sender_hwnd.is_null() {
            0
        } else {
            unsafe { GetWindowThreadProcessId(sender_hwnd, &mut sender_process_id) }
        };
        let authorized = preload_authorization().is_some_and(|authorization| {
            authorization.token == command.authorization_token
                && authorization.host_process_id == sender_process_id
                && sender_thread_id != 0
                && sender_hwnd == command.ack_hwnd
        });
        if !authorized {
            send_jump_ack(
                hwnd,
                command.ack_hwnd,
                command.command_id,
                JumpAckStatus::Failed,
            );
            return;
        }
        let pending = PENDING_CLEANUP_ACK.get_or_init(|| Mutex::new(None));
        if let Ok(mut pending) = pending.lock() {
            *pending = Some((command.ack_hwnd as usize, command.command_id, hwnd as usize));
        }
        CAPTURE_SHUTDOWN_REQUESTED.store(true, Ordering::Release);
        let _ = restore_file_dialog_show_captures();
        return;
    }

    let Some((command, status)) = dispatch_hook_copydata_message(
        message,
        || decode_jump_copydata_message(l_param),
        || is_supported_dialog(hwnd),
        |command| navigate_dialog_to_folder(hwnd, &command.folder_path),
    ) else {
        return;
    };

    send_jump_ack(hwnd, command.ack_hwnd, command.command_id, status);
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct CleanupCommand {
    ack_hwnd: HWND,
    command_id: u64,
    authorization_token: u64,
}

fn decode_cleanup_copydata_message(message: u32, l_param: LPARAM) -> Option<CleanupCommand> {
    if message != WM_COPYDATA || l_param == 0 {
        return None;
    }
    let copy_data = unsafe { &*(l_param as *const COPYDATASTRUCT) };
    if copy_data.dwData != CLEANUP_COPYDATA_MAGIC
        || copy_data.lpData.is_null()
        || copy_data.cbData as usize != std::mem::size_of::<CleanupCommandHeader>()
    {
        return None;
    }
    let bytes = unsafe {
        std::slice::from_raw_parts(copy_data.lpData.cast::<u8>(), copy_data.cbData as usize)
    };
    decode_cleanup_copydata_payload(bytes)
}

fn decode_cleanup_copydata_payload(bytes: &[u8]) -> Option<CleanupCommand> {
    let header_size = std::mem::size_of::<CleanupCommandHeader>();
    let usize_size = std::mem::size_of::<usize>();
    let command_offset = align_up(usize_size * 2, std::mem::align_of::<u64>());
    if bytes.len() != header_size {
        return None;
    }
    let magic = usize::from_ne_bytes(bytes[..usize_size].try_into().ok()?);
    let ack_hwnd = usize::from_ne_bytes(bytes[usize_size..usize_size * 2].try_into().ok()?);
    let command_id = u64::from_ne_bytes(bytes[command_offset..command_offset + 8].try_into().ok()?);
    let token_offset = command_offset + 8;
    let authorization_token =
        u64::from_ne_bytes(bytes[token_offset..token_offset + 8].try_into().ok()?);
    if magic != CLEANUP_COPYDATA_MAGIC
        || ack_hwnd == 0
        || command_id == 0
        || authorization_token == 0
    {
        return None;
    }
    Some(CleanupCommand {
        ack_hwnd: ack_hwnd as HWND,
        command_id,
        authorization_token,
    })
}

fn decode_jump_copydata_message(l_param: LPARAM) -> Option<JumpCommand> {
    if l_param == 0 {
        return None;
    }
    let copy_data = unsafe { &*(l_param as *const COPYDATASTRUCT) };
    if copy_data.lpData.is_null() || copy_data.cbData == 0 {
        return None;
    }

    let bytes = unsafe {
        std::slice::from_raw_parts(copy_data.lpData.cast::<u8>(), copy_data.cbData as usize)
    };

    decode_jump_copydata_payload(copy_data.dwData, bytes)
}

fn dispatch_hook_copydata_message<D, S, N>(
    message: u32,
    decode_command: D,
    is_supported_dialog: S,
    navigate_dialog: N,
) -> Option<(JumpCommand, JumpAckStatus)>
where
    D: FnOnce() -> Option<JumpCommand>,
    S: FnOnce() -> bool,
    N: FnOnce(&JumpCommand) -> JumpAckStatus,
{
    if message != WM_COPYDATA {
        return None;
    }

    let command = decode_command()?;
    let status = if is_supported_dialog() {
        navigate_dialog(&command)
    } else {
        JumpAckStatus::UnsupportedDialog
    };

    Some((command, status))
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct JumpCommand {
    ack_hwnd: HWND,
    command_id: u64,
    folder_path: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum JumpAckStatus {
    Success,
    UnsupportedDialog,
    Failed,
}

fn decode_jump_copydata_payload(dw_data: usize, bytes: &[u8]) -> Option<JumpCommand> {
    if dw_data != JUMP_COPYDATA_MAGIC {
        return None;
    }

    let header_size = std::mem::size_of::<JumpCommandHeader>();
    let usize_size = std::mem::size_of::<usize>();
    let u32_size = std::mem::size_of::<u32>();
    let command_offset = align_up(usize_size * 2, std::mem::align_of::<u64>());
    let utf16_len_offset = command_offset.checked_add(std::mem::size_of::<u64>())?;
    if bytes.len() < header_size || bytes.len() < utf16_len_offset + u32_size {
        return None;
    }

    let magic = usize::from_ne_bytes(bytes[..usize_size].try_into().ok()?);
    if magic != JUMP_COPYDATA_MAGIC {
        return None;
    }

    let ack_hwnd = usize::from_ne_bytes(bytes[usize_size..usize_size * 2].try_into().ok()?);
    if ack_hwnd == 0 {
        return None;
    }

    let command_id = u64::from_ne_bytes(bytes[command_offset..command_offset + 8].try_into().ok()?);
    if command_id == 0 {
        return None;
    }

    let utf16_code_units = u32::from_ne_bytes(
        bytes[utf16_len_offset..utf16_len_offset + u32_size]
            .try_into()
            .ok()?,
    ) as usize;
    if utf16_code_units == 0 {
        return None;
    }

    let utf16_byte_len = utf16_code_units.checked_mul(2)?;
    let expected_len = header_size.checked_add(utf16_byte_len)?;
    if bytes.len() != expected_len {
        return None;
    }

    let utf16 = bytes[header_size..]
        .chunks_exact(2)
        .map(|chunk| u16::from_ne_bytes([chunk[0], chunk[1]]))
        .collect::<Vec<_>>();
    if utf16.last().copied() != Some(0) {
        return None;
    }

    Some(JumpCommand {
        ack_hwnd: ack_hwnd as HWND,
        command_id,
        folder_path: String::from_utf16(&utf16[..utf16.len() - 1]).ok()?,
    })
}

fn navigate_dialog_to_folder(hwnd: HWND, folder_path: &str) -> JumpAckStatus {
    let mut _com_initialization = None;
    let mut shared_capture_error = None;
    let dialog = find_file_dialog_for_window(hwnd)
        .map(CapturedDialogReference::borrowed)
        .or_else(|| {
            _com_initialization = ComInitialization::enter();
            _com_initialization.as_ref()?;
            match acquire_shared_dialog_capture() {
                Ok(dialog) => Some(dialog),
                Err(error) => {
                    shared_capture_error = Some(error);
                    None
                }
            }
        });
    if let Some(dialog) = dialog {
        let wide_path = to_wide_null(folder_path);
        let mut shell_item = std::ptr::null_mut();
        let create_result = unsafe {
            SHCreateItemFromParsingName(
                wide_path.as_ptr(),
                std::ptr::null_mut(),
                &IID_ISHELL_ITEM,
                &mut shell_item,
            )
        };
        if create_result < 0 || shell_item.is_null() {
            return JumpAckStatus::Failed;
        }

        let set_folder: unsafe extern "system" fn(*mut c_void, *mut c_void) -> i32 =
            unsafe { interface_method(dialog.pointer, 12) };
        let set_result = unsafe { set_folder(dialog.pointer, shell_item) };
        unsafe { release_interface(shell_item) };
        if set_result < 0 {
            return JumpAckStatus::Failed;
        }

        // SetFolder's HRESULT is the authoritative synchronous COM result. Some
        // shell implementations publish GetFolder only after this WM_COPYDATA
        // callback returns to their modal loop. Desktop acceptance then accepts a
        // sentinel in the target folder to verify the actual navigation outcome.
        let _deferred_readback = verify_file_dialog_folder(dialog.pointer, folder_path);
        return JumpAckStatus::Success;
    }
    if shared_capture_error != Some(SharedDialogCaptureError::CaptureMissing) {
        return JumpAckStatus::Failed;
    }

    // Modern dialogs must have been captured through IFileDialog before Show.
    // The only non-COM contract retained here is the native selection message
    // defined by the legacy browse-for-folder dialog itself.
    navigate_browse_for_folder_dialog_to_folder(hwnd, folder_path)
}

fn verify_file_dialog_folder(dialog: *mut c_void, target_folder: &str) -> JumpAckStatus {
    let get_folder: unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> i32 =
        unsafe { interface_method(dialog, 13) };
    let mut folder = std::ptr::null_mut();
    if unsafe { get_folder(dialog, &mut folder) } < 0 || folder.is_null() {
        return JumpAckStatus::Failed;
    }

    let get_display_name: unsafe extern "system" fn(*mut c_void, u32, *mut *mut u16) -> i32 =
        unsafe { interface_method(folder, 5) };
    let mut display_name = std::ptr::null_mut();
    let result = unsafe { get_display_name(folder, SIGDN_FILESYSPATH, &mut display_name) };
    unsafe { release_interface(folder) };
    if result < 0 || display_name.is_null() {
        return JumpAckStatus::Failed;
    }

    let current_folder = unsafe { wide_ptr_to_string(display_name) };
    unsafe { CoTaskMemFree(display_name.cast()) };
    jump_status_for_verified_folder(Some(&current_folder), target_folder)
}

unsafe fn wide_ptr_to_string(value: *const u16) -> String {
    let mut len = 0usize;
    while unsafe { *value.add(len) } != 0 {
        len += 1;
    }
    String::from_utf16_lossy(unsafe { std::slice::from_raw_parts(value, len) })
}

fn navigate_browse_for_folder_dialog_to_folder(hwnd: HWND, folder_path: &str) -> JumpAckStatus {
    if !is_legacy_browse_for_folder_window(hwnd) {
        return JumpAckStatus::UnsupportedDialog;
    }

    let wide_path = to_wide_null(folder_path);
    unsafe {
        SendMessageW(
            hwnd,
            BFFM_SETSELECTIONW,
            TRUE as WPARAM,
            wide_path.as_ptr() as LPARAM,
        );
    }

    let mut selected_path = vec![0u16; FOLDER_PATH_BUFFER_LEN];
    let selected = unsafe {
        SendMessageW(
            hwnd,
            BFFM_GETSELECTIONW,
            0,
            selected_path.as_mut_ptr() as LPARAM,
        )
    };
    if selected == 0 {
        return JumpAckStatus::Failed;
    }

    let selected_path = wide_null_to_string(&selected_path);
    jump_status_for_verified_folder(Some(&selected_path), folder_path)
}

fn jump_status_for_verified_folder(
    verified_current_folder: Option<&str>,
    target_folder: &str,
) -> JumpAckStatus {
    match verified_current_folder {
        Some(current_folder) if folder_paths_match(current_folder, target_folder) => {
            JumpAckStatus::Success
        }
        _ => JumpAckStatus::Failed,
    }
}

fn folder_paths_match(current_path: &str, target_path: &str) -> bool {
    let normalize = |path: &str| {
        let path = path.trim().replace('/', "\\");
        let path = path.trim_end_matches('\\');
        (!path.is_empty()).then(|| path.to_ascii_lowercase())
    };

    normalize(current_path) == normalize(target_path)
}

fn wide_null_to_string(value: &[u16]) -> String {
    let len = value
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(value.len());
    String::from_utf16_lossy(&value[..len])
}

fn send_jump_ack(sender_hwnd: HWND, ack_hwnd: HWND, command_id: u64, status: JumpAckStatus) {
    if ack_hwnd.is_null() || command_id == 0 {
        return;
    }

    let mut payload = build_jump_ack_payload(command_id, status);
    let Ok(cb_data) = u32::try_from(payload.len()) else {
        return;
    };

    let mut copy_data = COPYDATASTRUCT {
        dwData: JUMP_ACK_COPYDATA_MAGIC,
        cbData: cb_data,
        lpData: payload.as_mut_ptr().cast(),
    };

    unsafe {
        SendMessageW(
            ack_hwnd,
            WM_COPYDATA,
            sender_hwnd as WPARAM,
            (&mut copy_data as *mut COPYDATASTRUCT) as LPARAM,
        );
    }
}

fn build_jump_ack_payload(command_id: u64, status: JumpAckStatus) -> Vec<u8> {
    let usize_size = std::mem::size_of::<usize>();
    let command_offset = align_up(usize_size, std::mem::align_of::<u64>());
    let status_offset = command_offset + std::mem::size_of::<u64>();
    let mut payload = Vec::with_capacity(std::mem::size_of::<JumpAckHeader>());
    payload.extend_from_slice(&JUMP_ACK_COPYDATA_MAGIC.to_ne_bytes());
    payload.resize(command_offset, 0);
    payload.extend_from_slice(&command_id.to_ne_bytes());
    payload.extend_from_slice(&jump_ack_status_code(status).to_ne_bytes());
    payload.resize(status_offset + std::mem::size_of::<u32>(), 0);
    payload.resize(std::mem::size_of::<JumpAckHeader>(), 0);

    payload
}

fn jump_ack_status_code(status: JumpAckStatus) -> u32 {
    match status {
        JumpAckStatus::Success => JUMP_ACK_STATUS_SUCCESS,
        JumpAckStatus::UnsupportedDialog => JUMP_ACK_STATUS_UNSUPPORTED_DIALOG,
        JumpAckStatus::Failed => JUMP_ACK_STATUS_FAILED,
    }
}

fn align_up(value: usize, alignment: usize) -> usize {
    debug_assert!(alignment.is_power_of_two());
    (value + alignment - 1) & !(alignment - 1)
}

fn to_wide_null(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hook_module_lifecycle_never_uses_the_permanent_pin_flag() {
        let forbidden = ["GET_MODULE_HANDLE_EX_FLAG_", "PIN"].concat();
        assert!(!include_str!("lib.rs").contains(&forbidden));
    }

    #[test]
    fn preload_ack_name_binds_process_thread_generation_and_architecture() {
        let first = preload_acknowledgement_name(100, 7, 11);
        assert!(first.contains(if usize::BITS == 64 { "x64" } else { "x86" }));
        assert_ne!(first, preload_acknowledgement_name(100, 8, 11));
        assert_ne!(first, preload_acknowledgement_name(101, 7, 11));
        assert_ne!(first, preload_acknowledgement_name(100, 7, 12));
        assert_ne!(first, spawn_preload_acknowledgement_name(100, 7, 11));
    }

    #[test]
    fn firefox_factory_plugin_targets_only_file_open_and_save_classes() {
        assert_eq!(Some(0b01), file_dialog_class_bit(&CLSID_FILE_OPEN_DIALOG));
        assert_eq!(Some(0b10), file_dialog_class_bit(&CLSID_FILE_SAVE_DIALOG));
        assert_eq!(None, file_dialog_class_bit(&IID_IFILE_DIALOG));
    }

    #[test]
    fn firefox_spawn_proof_transitions_from_pending_to_precapture_success() {
        assert_eq!(SPAWN_PROOF_PENDING, 0);
        assert_eq!(SPAWN_PROOF_SUCCESS, completed_spawn_proof_status(true));
        assert_eq!(SPAWN_PROOF_FAILED, completed_spawn_proof_status(false));
    }

    #[test]
    fn firefox_precapture_adds_and_balances_only_its_own_suspend_count() {
        assert_eq!(
            (CREATE_SUSPENDED, true),
            firefox_precapture_creation_flags(0, true)
        );
        assert_eq!(
            (CREATE_SUSPENDED, false),
            firefox_precapture_creation_flags(CREATE_SUSPENDED, true)
        );
        assert_eq!(
            (0x20, false),
            firefox_precapture_creation_flags(0x20, false)
        );
    }

    #[test]
    fn shared_dialog_capture_payload_round_trips_all_authorization_fields() {
        let expected = SharedDialogCapture {
            token: 0xFEDC_BA98_7654_3210,
            generation: 77,
            process_id: 1234,
            git_cookie: 99,
            thread_id: 4321,
        };
        assert_eq!(
            Some(expected),
            decode_shared_dialog_capture(&encode_shared_dialog_capture(expected))
        );
        assert_eq!(None, decode_shared_dialog_capture("invalid"));
    }

    #[test]
    fn create_process_preparation_never_waits_for_child_acknowledgement() {
        let preparation = include_str!("lib.rs")
            .split("unsafe fn prepare_spawned_firefox_utility")
            .nth(1)
            .and_then(|tail| tail.split("fn completed_spawn_proof_status").next())
            .unwrap();
        assert!(!preparation.contains("while "));
        assert!(!preparation.contains("thread::sleep"));
        assert!(preparation.contains("inject_firefox_utility_before_resume"));
        assert!(preparation.contains("installed_before_resume"));
        let create_process_wrapper = include_str!("lib.rs")
            .split("captured_firefox_create_process(")
            .nth(1)
            .and_then(|tail| tail.split("captured_firefox_create_process_as_user").next())
            .unwrap();
        assert!(create_process_wrapper.contains("prepare_created_firefox_utility"));
        assert!(create_process_wrapper.contains("ResumeThread"));
    }

    #[test]
    fn capture_timer_reports_no_failure_ack_while_work_is_pending() {
        let forbidden_failure_status = ["PRELOAD_ACK_", "FAILED"].concat();
        assert!(!include_str!("lib.rs").contains(&forbidden_failure_status));
    }

    #[test]
    fn direct_capture_is_deferred_to_the_originating_ui_thread() {
        let source = include_str!("lib.rs");
        let scheduler = source
            .split("fn schedule_direct_dialog_capture")
            .nth(1)
            .and_then(|tail| {
                tail.split("fn ensure_direct_dialog_capture_installed")
                    .next()
            })
            .unwrap();
        assert!(scheduler.contains("SetTimer"));
        assert!(scheduler.contains("direct_dialog_capture_timer_callback"));
        assert!(scheduler.contains("CoInitializeEx"));
    }

    #[test]
    fn hook_callbacks_never_create_file_dialog_com_objects_directly() {
        let source = include_str!("lib.rs");
        let preload = source
            .split("fn ListaryOpenPreloadHookProc")
            .nth(1)
            .and_then(|tail| tail.split("fn is_supported_dialog").next())
            .unwrap();
        assert!(!preload.contains("CoCreateInstance"));
    }

    #[test]
    fn firefox_file_dialog_spawn_parser_handles_quoted_program_files_path() {
        let arguments = vec![
            r"C:\Program Files\Mozilla Firefox\firefox.exe".to_string(),
            "-contentproc".to_string(),
            "-sandboxingKind".to_string(),
            "4".to_string(),
            "-parentPid".to_string(),
            "26492".to_string(),
            "-".to_string(),
            "12".to_string(),
            "utility".to_string(),
        ];
        assert!(is_firefox_file_dialog_spawn("", &arguments, 26492));
        assert!(!is_firefox_file_dialog_spawn("", &arguments, 26493));
        for sandbox_kind in ["0", "1", "2", "4"] {
            let mut current_version = arguments.clone();
            current_version[3] = sandbox_kind.to_string();
            assert!(is_firefox_file_dialog_spawn("", &current_version, 26492));
        }
        let mut non_utility = arguments.clone();
        non_utility[8] = "socket".to_string();
        assert!(!is_firefox_file_dialog_spawn("", &non_utility, 26492));
    }

    #[test]
    fn firefox_precapture_runtime_is_resolved_beside_the_hook_dll() {
        assert_eq!(
            Some(r"C:\ListaryOpen\hooks\x64\libunwind.dll".to_string()),
            adjacent_hook_runtime_path(r"C:\ListaryOpen\hooks\x64\ListaryOpen.Hook.dll")
        );
    }

    #[test]
    fn captured_vtable_restore_keeps_failed_slots_for_a_safe_retry() {
        let mut captures = vec![(100, 10), (200, 20), (300, 30)];
        let restored = restore_captured_vtable_slots(&mut captures, |slot, _| slot != 200);

        assert!(!restored);
        assert_eq!(vec![(200, 20)], captures);
    }

    #[test]
    fn captured_vtable_restore_never_clobbers_a_third_party_replacement() {
        assert_eq!(
            CaptureSlotRestoreAction::AlreadyRestored,
            capture_slot_restore_action(10, 99, 10)
        );
        assert_eq!(
            CaptureSlotRestoreAction::Restore,
            capture_slot_restore_action(99, 99, 10)
        );
        assert_eq!(
            CaptureSlotRestoreAction::Conflict,
            capture_slot_restore_action(77, 99, 10)
        );
    }

    #[test]
    fn cleanup_command_requires_magic_ack_command_and_authorization_token() {
        let pointer_size = std::mem::size_of::<usize>();
        let command_offset = align_up(pointer_size * 2, std::mem::align_of::<u64>());
        let mut payload = vec![0u8; std::mem::size_of::<CleanupCommandHeader>()];
        payload[..pointer_size].copy_from_slice(&CLEANUP_COPYDATA_MAGIC.to_ne_bytes());
        payload[pointer_size..pointer_size * 2].copy_from_slice(&42usize.to_ne_bytes());
        payload[command_offset..command_offset + 8].copy_from_slice(&7u64.to_ne_bytes());
        payload[command_offset + 8..command_offset + 16].copy_from_slice(&11u64.to_ne_bytes());

        assert_eq!(
            Some(CleanupCommand {
                ack_hwnd: 42usize as HWND,
                command_id: 7,
                authorization_token: 11,
            }),
            decode_cleanup_copydata_payload(&payload)
        );
        payload[command_offset + 8..command_offset + 16].fill(0);
        assert_eq!(None, decode_cleanup_copydata_payload(&payload));
    }

    #[test]
    fn folder_paths_match_ignores_case_separators_and_trailing_slashes() {
        assert!(folder_paths_match(r"C:\Work\Project\", "c:/work/project"));
        assert!(!folder_paths_match(r"C:\Work\Project", r"C:\Work\Other"));
    }

    #[test]
    fn standard_dialog_readback_rejects_missing_or_mismatched_folder() {
        assert_eq!(
            JumpAckStatus::Failed,
            jump_status_for_verified_folder(None, r"C:\Work\Project")
        );
        assert_eq!(
            JumpAckStatus::Failed,
            jump_status_for_verified_folder(Some(r"C:\Work\Other"), r"C:\Work\Project")
        );
        assert_eq!(
            JumpAckStatus::Success,
            jump_status_for_verified_folder(Some(r"c:/work/project/"), r"C:\Work\Project")
        );
    }

    #[test]
    fn decode_jump_payload_accepts_valid_magic_header_ack_command_and_terminated_path() {
        let payload = payload_bytes(
            listary_open_hook_common::JUMP_COPYDATA_MAGIC,
            0x1234,
            0xABCD_EF01_2345_6789,
            "C:\\Temp".encode_utf16().chain(Some(0)).collect::<Vec<_>>(),
        );

        let command =
            decode_jump_copydata_payload(listary_open_hook_common::JUMP_COPYDATA_MAGIC, &payload)
                .expect("valid jump payload should decode");

        assert_eq!(0x1234, command.ack_hwnd as usize);
        assert_eq!(0xABCD_EF01_2345_6789, command.command_id);
        assert_eq!("C:\\Temp", command.folder_path);
    }

    #[test]
    fn decode_jump_payload_rejects_mismatched_dwdata_magic() {
        let payload = payload_bytes(
            listary_open_hook_common::JUMP_COPYDATA_MAGIC,
            0x1234,
            42,
            "C:\\Temp".encode_utf16().chain(Some(0)).collect::<Vec<_>>(),
        );

        assert_eq!(None, decode_jump_copydata_payload(0, &payload));
    }

    #[test]
    fn decode_jump_payload_rejects_mismatched_header_magic() {
        let payload = payload_bytes(
            0,
            0x1234,
            42,
            "C:\\Temp".encode_utf16().chain(Some(0)).collect(),
        );

        assert_eq!(
            None,
            decode_jump_copydata_payload(listary_open_hook_common::JUMP_COPYDATA_MAGIC, &payload)
        );
    }

    #[test]
    fn decode_jump_payload_rejects_missing_null_terminator() {
        let payload = payload_bytes(
            listary_open_hook_common::JUMP_COPYDATA_MAGIC,
            0x1234,
            42,
            "C:\\Temp".encode_utf16().collect(),
        );

        assert_eq!(
            None,
            decode_jump_copydata_payload(listary_open_hook_common::JUMP_COPYDATA_MAGIC, &payload)
        );
    }

    #[test]
    fn supported_dialog_shape_accepts_file_and_upload_dialogs_without_process_names() {
        assert!(is_supported_dialog_shape("#32770", "Open", false));
        assert!(is_supported_dialog_shape("#32770", "File Upload", false));
        assert!(is_supported_dialog_shape(
            "#32770",
            "Choose which file(s) to upload...",
            false
        ));
        assert!(is_supported_dialog_shape("#32770", "Firefox", true));
    }

    #[test]
    fn dialog_window_match_accepts_identical_handles() {
        let hwnd = 0x1234usize as HWND;
        assert!(dialog_window_matches(hwnd, hwnd));
        assert!(!dialog_window_matches(std::ptr::null_mut(), hwnd));
        assert!(!dialog_window_matches(hwnd, std::ptr::null_mut()));
    }

    #[test]
    fn navigation_without_file_dialog_uses_only_the_legacy_folder_dialog_contract() {
        let source = include_str!("lib.rs");
        let navigate = source
            .split("fn navigate_dialog_to_folder")
            .nth(1)
            .and_then(|tail| tail.split("fn verify_file_dialog_folder").next())
            .expect("navigate_dialog_to_folder body");
        assert!(navigate.contains("find_file_dialog_for_window"));
        assert!(navigate.contains("navigate_browse_for_folder_dialog_to_folder"));
    }

    #[test]
    fn supported_dialog_shape_rejects_non_dialog_classes() {
        assert!(is_supported_dialog_shape("#32770", "Properties", false));
        assert!(!is_supported_dialog_shape(
            "MozillaWindowClass",
            "File Upload",
            true
        ));
    }

    #[test]
    fn legacy_browse_for_folder_uses_only_its_native_selection_message() {
        assert!(is_legacy_browse_for_folder_dialog(
            "#32770",
            "Browse For Folder"
        ));
        assert!(!is_legacy_browse_for_folder_dialog("#32770", "Open"));
        assert!(!is_legacy_browse_for_folder_dialog(
            "GHOST_WindowClass",
            "Blender File View"
        ));
    }

    #[test]
    fn hook_copydata_dispatch_skips_non_copydata_without_decoding_or_shape_probe() {
        let mut decoded = false;
        let mut shape_probed = false;
        let mut navigated = false;

        let result = dispatch_hook_copydata_message(
            0,
            || {
                decoded = true;
                Some(test_jump_command())
            },
            || {
                shape_probed = true;
                true
            },
            |_| {
                navigated = true;
                JumpAckStatus::Success
            },
        );

        assert_eq!(None, result);
        assert!(!decoded);
        assert!(!shape_probed);
        assert!(!navigated);
    }

    #[test]
    fn hook_copydata_dispatch_returns_unsupported_for_decoded_command_with_unsupported_shape() {
        let command = test_jump_command();
        let mut navigated = false;

        let result = dispatch_hook_copydata_message(
            WM_COPYDATA,
            || Some(command.clone()),
            || false,
            |_| {
                navigated = true;
                JumpAckStatus::Success
            },
        );

        assert_eq!(Some((command, JumpAckStatus::UnsupportedDialog)), result);
        assert!(!navigated);
    }

    #[test]
    fn supported_dialog_probe_returns_before_title_or_address_for_non_dialog_class() {
        let result = is_supported_dialog_from_probe(
            Some("MozillaWindowClass".to_string()),
            || panic!("title should not be read for non-dialog class"),
            || panic!("address control should not be read for non-dialog class"),
        );

        assert!(!result);
    }

    #[test]
    fn encode_jump_ack_payload_includes_magic_command_and_status() {
        let payload =
            build_jump_ack_payload(0xABCD_EF01_2345_6789, JumpAckStatus::UnsupportedDialog);
        let usize_size = std::mem::size_of::<usize>();
        let magic = usize::from_ne_bytes(payload[..usize_size].try_into().expect("ack magic"));
        let command_offset = align_up(usize_size, std::mem::align_of::<u64>());
        let command_id = u64::from_ne_bytes(
            payload[command_offset..command_offset + std::mem::size_of::<u64>()]
                .try_into()
                .expect("ack command id"),
        );
        let status_offset = command_offset + std::mem::size_of::<u64>();
        let status = u32::from_ne_bytes(
            payload[status_offset..status_offset + std::mem::size_of::<u32>()]
                .try_into()
                .expect("ack status"),
        );

        assert_eq!(listary_open_hook_common::JUMP_ACK_COPYDATA_MAGIC, magic);
        assert_eq!(0xABCD_EF01_2345_6789, command_id);
        assert_eq!(
            listary_open_hook_common::JUMP_ACK_STATUS_UNSUPPORTED_DIALOG,
            status
        );
    }

    fn payload_bytes(
        header_magic: usize,
        ack_hwnd: usize,
        command_id: u64,
        utf16_units: Vec<u16>,
    ) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(&header_magic.to_ne_bytes());
        payload.extend_from_slice(&ack_hwnd.to_ne_bytes());
        payload.resize(align_up(payload.len(), std::mem::align_of::<u64>()), 0);
        payload.extend_from_slice(&command_id.to_ne_bytes());
        payload.extend_from_slice(&(utf16_units.len() as u32).to_ne_bytes());
        payload.resize(
            std::mem::size_of::<listary_open_hook_common::JumpCommandHeader>(),
            0,
        );
        for unit in utf16_units {
            payload.extend_from_slice(&unit.to_ne_bytes());
        }

        payload
    }

    fn test_jump_command() -> JumpCommand {
        JumpCommand {
            ack_hwnd: 0x1234usize as HWND,
            command_id: 42,
            folder_path: "C:\\Users\\paulx".to_string(),
        }
    }
}
