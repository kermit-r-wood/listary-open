namespace ListaryOpen.Infrastructure.Hooks;

public static class HookQuickSwitchBridgeFactory
{
    public static IHookQuickSwitchBridge CreateDefault()
    {
        return new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>());
    }
}
