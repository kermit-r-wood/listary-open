use windows_sys::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::UI::WindowsAndMessaging::CallNextHookEx;

#[no_mangle]
pub unsafe extern "system" fn ListaryOpenHookProc(code: i32, w_param: WPARAM, l_param: LPARAM) -> LRESULT {
    CallNextHookEx(std::ptr::null_mut(), code, w_param, l_param)
}
