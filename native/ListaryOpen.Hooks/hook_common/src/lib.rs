pub const HOOK_MAGIC: &str = "ListaryOpenHookV1";
pub const HOOK_DLL_EXPORT: &[u8] = b"ListaryOpenHookProc\0";
pub const DIALOG_CLASS: &str = "#32770";
pub const JUMP_COPYDATA_MAGIC: usize = 0x4C4F_4A55;
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
    pub utf16_code_units: u32,
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
        assert_eq!(
            std::mem::align_of::<usize>(),
            std::mem::align_of::<JumpCommandHeader>()
        );

        #[cfg(target_pointer_width = "64")]
        assert_eq!(16, std::mem::size_of::<JumpCommandHeader>());

        #[cfg(target_pointer_width = "32")]
        assert_eq!(8, std::mem::size_of::<JumpCommandHeader>());
    }
}
