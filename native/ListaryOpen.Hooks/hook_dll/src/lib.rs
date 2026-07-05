use listary_open_hook_common::{DIALOG_CLASS, WM_LISTARY_OPEN_JUMP};
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, GetClassNameW, CWPSTRUCT, WM_COPYDATA,
};

#[no_mangle]
pub unsafe extern "system" fn ListaryOpenHookProc(
    code: i32,
    w_param: WPARAM,
    l_param: LPARAM,
) -> LRESULT {
    if code >= 0 {
        let cwp = l_param as *const CWPSTRUCT;
        if !cwp.is_null() {
            let cwp = unsafe { &*cwp };
            if is_supported_dialog(cwp.hwnd)
                && (cwp.message == WM_COPYDATA || cwp.message == WM_LISTARY_OPEN_JUMP)
            {
                handle_dialog_message(cwp.hwnd, cwp.message, cwp.wParam, cwp.lParam);
            }
        }
    }

    unsafe { CallNextHookEx(std::ptr::null_mut(), code, w_param, l_param) }
}

fn is_supported_dialog(hwnd: HWND) -> bool {
    let mut class_buffer = [0u16; 64];
    let class_len =
        unsafe { GetClassNameW(hwnd, class_buffer.as_mut_ptr(), class_buffer.len() as i32) };

    class_len > 0
        && DIALOG_CLASS
            .encode_utf16()
            .eq(class_buffer[..class_len as usize].iter().copied())
}

fn handle_dialog_message(_hwnd: HWND, _message: u32, _w_param: WPARAM, _l_param: LPARAM) {
    // Task 10 will translate these messages into dialog jump behavior.
}
