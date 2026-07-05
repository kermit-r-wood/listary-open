namespace ListaryOpen.Infrastructure.Hooks;

public static class HookQuickSwitchBridgeFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    public static IHookQuickSwitchBridge CreateDefault()
    {
        var hostPaths = HookHostPaths.CreateDefault();

        return new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new HookIpcClient(HookQuickSwitchBridge.X64PipeName, ConnectTimeout),
                [HookArchitecture.X86] = new HookIpcClient(HookQuickSwitchBridge.X86PipeName, ConnectTimeout)
            },
            hostPaths,
            new HookHostProcessFactory());
    }
}
