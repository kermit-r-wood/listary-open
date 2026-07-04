namespace ListaryOpen.Infrastructure.Hooks;

public enum HookArchitecture
{
    X64,
    X86
}

public static class HookArchitectureExtensions
{
    public static HookArchitecture FromIs64Bit(bool is64Bit) =>
        is64Bit ? HookArchitecture.X64 : HookArchitecture.X86;

    public static string ToFolderName(this HookArchitecture architecture) =>
        architecture switch
        {
            HookArchitecture.X64 => "x64",
            HookArchitecture.X86 => "x86",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "Unknown hook architecture.")
        };
}
