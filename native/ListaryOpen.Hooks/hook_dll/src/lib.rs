use listary_open_hook_common::{
    JumpAckHeader, JumpCommandHeader, DIALOG_CLASS, JUMP_ACK_COPYDATA_MAGIC,
    JUMP_ACK_STATUS_FAILED, JUMP_ACK_STATUS_SUCCESS, JUMP_ACK_STATUS_UNSUPPORTED_DIALOG,
    JUMP_COPYDATA_MAGIC,
};
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::System::DataExchange::COPYDATASTRUCT;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, GetClassNameW, GetDlgItem, GetWindowTextLengthW, GetWindowTextW, SendMessageW,
    CWPSTRUCT, WM_COPYDATA, WM_KEYDOWN, WM_KEYUP, WM_SETTEXT,
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
    !unsafe { GetDlgItem(hwnd, ADDRESS_BAR_EDIT_CONTROL_ID) }.is_null()
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
    let Some(command) = decode_jump_copydata_payload(copy_data.dwData, bytes) else {
        return;
    };

    let status = navigate_dialog_to_folder(hwnd, &command.folder_path);
    send_jump_ack(command.ack_hwnd, command.command_id, status);
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
    let address_edit = unsafe { GetDlgItem(hwnd, ADDRESS_BAR_EDIT_CONTROL_ID) };
    if address_edit.is_null() {
        return JumpAckStatus::UnsupportedDialog;
    }

    let wide_path = to_wide_null(folder_path);
    let set_text =
        unsafe { SendMessageW(address_edit, WM_SETTEXT, 0, wide_path.as_ptr() as LPARAM) };
    if set_text == 0 {
        return JumpAckStatus::Failed;
    }

    unsafe {
        SendMessageW(address_edit, WM_KEYDOWN, VK_RETURN_KEY, 0);
        SendMessageW(address_edit, WM_KEYUP, VK_RETURN_KEY, 0);
    }

    JumpAckStatus::Success
}

fn send_jump_ack(ack_hwnd: HWND, command_id: u64, status: JumpAckStatus) {
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
            0,
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
    fn supported_dialog_shape_rejects_unknown_or_non_dialog_shapes() {
        assert!(!is_supported_dialog_shape("#32770", "Properties", false));
        assert!(!is_supported_dialog_shape(
            "MozillaWindowClass",
            "File Upload",
            true
        ));
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
}
