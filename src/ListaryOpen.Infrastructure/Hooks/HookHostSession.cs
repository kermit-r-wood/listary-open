using System.Security.Cryptography;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed record HookHostSession(
    string X64PipeName,
    string X86PipeName,
    int ParentProcessId,
    string Secret)
{
    public static HookHostSession Create()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var parentProcessId = Environment.ProcessId;
        return new HookHostSession(
            $"{HookQuickSwitchBridge.X64PipeName}-{parentProcessId}-{sessionId}",
            $"{HookQuickSwitchBridge.X86PipeName}-{parentProcessId}-{sessionId}",
            parentProcessId,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }
}
