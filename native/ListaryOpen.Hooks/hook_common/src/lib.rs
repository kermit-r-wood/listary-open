pub const HOOK_MAGIC: &str = "ListaryOpenHookV1";
pub const HOOK_DLL_EXPORT: &[u8] = b"ListaryOpenHookProc\0";
pub const DIALOG_CLASS: &str = "#32770";
pub const WM_USER_HOOK_BASE: u32 = 0x0400 + 0x4C4F;
pub const WM_LISTARY_OPEN_JUMP: u32 = WM_USER_HOOK_BASE + 1;
pub const WM_LISTARY_OPEN_REPORT: u32 = WM_USER_HOOK_BASE + 2;

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
}
