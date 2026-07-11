namespace ListaryOpen.Infrastructure.Hooks;

public static class HookQuickSwitchBridgeFactory
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    public static IHookQuickSwitchBridge CreateDefault()
    {
        var hostPaths = HookHostPaths.CreateDefault();
        var session = HookHostSession.Create();

        return new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new HookIpcClient(session.X64PipeName, ConnectTimeout, session.ParentProcessId, session.Secret),
                [HookArchitecture.X86] = new HookIpcClient(session.X86PipeName, ConnectTimeout, session.ParentProcessId, session.Secret)
            },
            hostPaths,
            new HookHostProcessFactory(),
            session);
    }
}
