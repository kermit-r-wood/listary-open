use listary_open_hook_common::{JumpCommandHeader, DIALOG_CLASS, JUMP_COPYDATA_MAGIC};
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::System::DataExchange::COPYDATASTRUCT;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, GetClassNameW, GetDlgItem, SendMessageW, CWPSTRUCT, WM_COPYDATA, WM_KEYDOWN,
    WM_KEYUP, WM_SETTEXT,
};

const ADDRESS_BAR_EDIT_CONTROL_ID: i32 = 41477;
const VK_RETURN_KEY: WPARAM = 0x0D;

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
            if is_supported_dialog(cwp.hwnd) && cwp.message == WM_COPYDATA {
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

fn handle_dialog_message(hwnd: HWND, message: u32, _w_param: WPARAM, l_param: LPARAM) {
    if message != WM_COPYDATA || l_param == 0 {
        return;
    }

    let copy_data = unsafe { &*(l_param as *const COPYDATASTRUCT) };
    if copy_data.lpData.is_null() || copy_data.cbData == 0 {
        return;
    }

    let bytes = unsafe {
        std::slice::from_raw_parts(copy_data.lpData.cast::<u8>(), copy_data.cbData as usize)
    };
    let Some(folder_path) = decode_jump_copydata_payload(copy_data.dwData, bytes) else {
        return;
    };

    navigate_dialog_to_folder(hwnd, &folder_path);
}

fn decode_jump_copydata_payload(dw_data: usize, bytes: &[u8]) -> Option<String> {
    if dw_data != JUMP_COPYDATA_MAGIC {
        return None;
    }

    let header_size = std::mem::size_of::<JumpCommandHeader>();
    let usize_size = std::mem::size_of::<usize>();
    let u32_size = std::mem::size_of::<u32>();
    if bytes.len() < header_size || bytes.len() < usize_size + u32_size {
        return None;
    }

    let magic = usize::from_ne_bytes(bytes[..usize_size].try_into().ok()?);
    if magic != JUMP_COPYDATA_MAGIC {
        return None;
    }

    let utf16_code_units =
        u32::from_ne_bytes(bytes[usize_size..usize_size + u32_size].try_into().ok()?) as usize;
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

    String::from_utf16(&utf16[..utf16.len() - 1]).ok()
}

fn navigate_dialog_to_folder(hwnd: HWND, folder_path: &str) -> bool {
    let address_edit = unsafe { GetDlgItem(hwnd, ADDRESS_BAR_EDIT_CONTROL_ID) };
    if address_edit.is_null() {
        return false;
    }

    let wide_path = to_wide_null(folder_path);
    let set_text =
        unsafe { SendMessageW(address_edit, WM_SETTEXT, 0, wide_path.as_ptr() as LPARAM) };
    if set_text == 0 {
        return false;
    }

    unsafe {
        SendMessageW(address_edit, WM_KEYDOWN, VK_RETURN_KEY, 0);
        SendMessageW(address_edit, WM_KEYUP, VK_RETURN_KEY, 0);
    }

    true
}

fn to_wide_null(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(Some(0)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decode_jump_payload_accepts_valid_magic_header_and_terminated_path() {
        let payload = payload_bytes(
            listary_open_hook_common::JUMP_COPYDATA_MAGIC,
            "C:\\Temp".encode_utf16().chain(Some(0)).collect::<Vec<_>>(),
        );

        assert_eq!(
            Some("C:\\Temp".to_string()),
            decode_jump_copydata_payload(listary_open_hook_common::JUMP_COPYDATA_MAGIC, &payload)
        );
    }

    #[test]
    fn decode_jump_payload_rejects_mismatched_dwdata_magic() {
        let payload = payload_bytes(
            listary_open_hook_common::JUMP_COPYDATA_MAGIC,
            "C:\\Temp".encode_utf16().chain(Some(0)).collect::<Vec<_>>(),
        );

        assert_eq!(None, decode_jump_copydata_payload(0, &payload));
    }

    #[test]
    fn decode_jump_payload_rejects_mismatched_header_magic() {
        let payload = payload_bytes(0, "C:\\Temp".encode_utf16().chain(Some(0)).collect());

        assert_eq!(
            None,
            decode_jump_copydata_payload(listary_open_hook_common::JUMP_COPYDATA_MAGIC, &payload)
        );
    }

    #[test]
    fn decode_jump_payload_rejects_missing_null_terminator() {
        let payload = payload_bytes(
            listary_open_hook_common::JUMP_COPYDATA_MAGIC,
            "C:\\Temp".encode_utf16().collect(),
        );

        assert_eq!(
            None,
            decode_jump_copydata_payload(listary_open_hook_common::JUMP_COPYDATA_MAGIC, &payload)
        );
    }

    fn payload_bytes(header_magic: usize, utf16_units: Vec<u16>) -> Vec<u8> {
        let mut payload = Vec::new();
        payload.extend_from_slice(&header_magic.to_ne_bytes());
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
}
