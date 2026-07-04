namespace ListaryOpen.Infrastructure.Hooks;

public sealed record HookArchitectureStatus(
    HookArchitecture Architecture,
    bool Enabled,
    bool HostRunning,
    bool HookDllPresent,
    string Message);

public sealed record HookQuickSwitchStatus(
    bool Enabled,
    HookArchitectureStatus X64,
    HookArchitectureStatus X86)
{
    public string DisplayText
    {
        get
        {
            if (!Enabled)
            {
                return "Hook quick switch: disabled";
            }

            if (X64.HostRunning && X86.HostRunning)
            {
                return "Hook quick switch: enabled for x64 and x86";
            }

            if (X64.HostRunning || X86.HostRunning)
            {
                return $"Hook quick switch: partial ({X64.Message}; {X86.Message})";
            }

            return $"Hook quick switch: unavailable ({X64.Message}; {X86.Message})";
        }
    }

    public static HookQuickSwitchStatus Disabled() => new(
        false,
        new HookArchitectureStatus(HookArchitecture.X64, false, false, false, "x64 disabled"),
        new HookArchitectureStatus(HookArchitecture.X86, false, false, false, "x86 disabled"));
}
