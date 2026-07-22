pub const HOOK_MAGIC: &str = "ListaryOpenHookV1";
pub const HOOK_DLL_EXPORT: &[u8] = b"ListaryOpenHookProc\0";
pub const DIALOG_CLASS: &str = "#32770";
pub const JUMP_COPYDATA_MAGIC: usize = 0x4C4F_4A55;
pub const CLEANUP_COPYDATA_MAGIC: usize = 0x4C4F_434C;
pub const JUMP_ACK_COPYDATA_MAGIC: usize = 0x4C4F_414B;
pub const JUMP_ACK_STATUS_SUCCESS: u32 = 1;
pub const JUMP_ACK_STATUS_UNSUPPORTED_DIALOG: u32 = 2;
pub const JUMP_ACK_STATUS_FAILED: u32 = 3;
pub const WM_USER_HOOK_BASE: u32 = 0x0400 + 0x4C4F;
/// Reserved for the written hook plan, but not an external command channel.
/// `WH_CALLWNDPROC` can observe messages only; commands need explicit validation
/// and acknowledgement instead of relying on the hook return value.
pub const WM_LISTARY_OPEN_JUMP: u32 = WM_USER_HOOK_BASE + 1;
pub const WM_LISTARY_OPEN_REPORT: u32 = WM_USER_HOOK_BASE + 2;

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JumpCommandHeader {
    pub magic: usize,
    pub ack_hwnd: usize,
    pub command_id: u64,
    pub utf16_code_units: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct JumpAckHeader {
    pub magic: usize,
    pub command_id: u64,
    pub status: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CleanupCommandHeader {
    pub magic: usize,
    pub ack_hwnd: usize,
    pub command_id: u64,
    pub authorization_token: u64,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PreloadAuthorization {
    pub token: u64,
    pub host_process_id: u32,
    pub generation: u32,
}

pub const PRELOAD_ACK_PENDING: u32 = 0;
pub const PRELOAD_ACK_SUCCESS: u32 = 1;
pub const PRELOAD_ACK_FAILED: u32 = 2;

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct PreloadAcknowledgement {
    pub token: u64,
    pub status: u32,
    pub host_handle: usize,
}

pub const SPAWN_PROOF_PENDING: u32 = 0;
pub const SPAWN_PROOF_SUCCESS: u32 = 1;
pub const SPAWN_PROOF_FAILED: u32 = 2;

#[repr(C)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SpawnProof {
    pub token: u64,
    pub child_creation_time: u64,
    pub generation: u32,
    pub parent_process_id: u32,
    pub child_process_id: u32,
    pub child_thread_id: u32,
    pub status: u32,
    pub installed_before_resume: u32,
    pub captured_show_observed: u32,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exposes_dialog_hook_protocol_constants() {
        assert_eq!("#32770", DIALOG_CLASS);
        assert_eq!(0x0400 + 0x4C4F, WM_USER_HOOK_BASE);
        assert_eq!(WM_USER_HOOK_BASE + 1, WM_LISTARY_OPEN_JUMP);
        assert_eq!(WM_USER_HOOK_BASE + 2, WM_LISTARY_OPEN_REPORT);
    }

    #[test]
    fn exposes_jump_copydata_magic_and_header_layout() {
        assert_eq!(0x4C4F_4A55, JUMP_COPYDATA_MAGIC);
        assert_eq!(0x4C4F_434C, CLEANUP_COPYDATA_MAGIC);
        assert_eq!(0x4C4F_414B, JUMP_ACK_COPYDATA_MAGIC);
        assert_eq!(1, JUMP_ACK_STATUS_SUCCESS);
        assert_eq!(2, JUMP_ACK_STATUS_UNSUPPORTED_DIALOG);
        assert_eq!(3, JUMP_ACK_STATUS_FAILED);
        assert_eq!(0, PRELOAD_ACK_PENDING);
        assert_eq!(1, PRELOAD_ACK_SUCCESS);
        assert_eq!(2, PRELOAD_ACK_FAILED);
        assert_eq!(0, SPAWN_PROOF_PENDING);
        assert_eq!(1, SPAWN_PROOF_SUCCESS);
        assert_eq!(2, SPAWN_PROOF_FAILED);
        assert!(std::mem::size_of::<PreloadAcknowledgement>() >= 16);
        assert_eq!(48, std::mem::size_of::<SpawnProof>());
        assert_eq!(
            std::mem::align_of::<usize>().max(std::mem::align_of::<u64>()),
            std::mem::align_of::<JumpCommandHeader>()
        );
        assert_eq!(
            std::mem::align_of::<usize>().max(std::mem::align_of::<u64>()),
            std::mem::align_of::<JumpAckHeader>()
        );

        let align_up = |value: usize, alignment: usize| (value + alignment - 1) & !(alignment - 1);
        let pointer_size = std::mem::size_of::<usize>();
        let u64_alignment = std::mem::align_of::<u64>();
        let command_fields_end =
            align_up(pointer_size * 2, u64_alignment) + std::mem::size_of::<u64>() + 4;
        let ack_fields_end = align_up(pointer_size, u64_alignment) + std::mem::size_of::<u64>() + 4;
        assert_eq!(
            align_up(
                command_fields_end,
                std::mem::align_of::<JumpCommandHeader>()
            ),
            std::mem::size_of::<JumpCommandHeader>()
        );
        assert_eq!(
            align_up(ack_fields_end, std::mem::align_of::<JumpAckHeader>()),
            std::mem::size_of::<JumpAckHeader>()
        );
        assert_eq!(
            align_up(
                align_up(pointer_size * 2, u64_alignment) + std::mem::size_of::<u64>() * 2,
                std::mem::align_of::<CleanupCommandHeader>()
            ),
            std::mem::size_of::<CleanupCommandHeader>()
        );
    }
}
