using System.Diagnostics;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookQuickSwitchBridgeTests
{
    [Fact]
    public async Task EnableStartsAvailableHostsAndRaisesOneStatusChangedEvent()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        hookFiles.CreateHostAndDll(HookArchitecture.X86);
        var processFactory = new RecordingHookHostProcessFactory(startResult: new Process());
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            CreateEmptyClients(),
            hookFiles.Paths,
            processFactory);
        var statuses = new List<HookQuickSwitchStatus>();
        bridge.StatusChanged += (_, status) => statuses.Add(status);

        await bridge.EnableAsync(CancellationToken.None);

        Assert.Single(statuses);
        Assert.True(bridge.Status.Enabled);
        Assert.True(bridge.Status.X64.HostRunning);
        Assert.True(bridge.Status.X64.HookDllPresent);
        Assert.True(bridge.Status.X86.HostRunning);
        Assert.True(bridge.Status.X86.HookDllPresent);
        Assert.Equal(
            new[] { "listary-open-hook-x64", "listary-open-hook-x86" },
            processFactory.Starts.Select(start => start.StartInfo.ArgumentList[1]));
        Assert.Equal(
            new[] { hookFiles.ForArchitecture(HookArchitecture.X64).HookDllPath, hookFiles.ForArchitecture(HookArchitecture.X86).HookDllPath },
            processFactory.Starts.Select(start => start.StartInfo.ArgumentList[3]));
    }

    [Fact]
    public async Task EnableReportsMissingHostAndDllPerArchitecture()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateDll(HookArchitecture.X64);
        hookFiles.CreateHost(HookArchitecture.X86);
        var processFactory = new RecordingHookHostProcessFactory(startResult: new Process());
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            CreateEmptyClients(),
            hookFiles.Paths,
            processFactory);

        await bridge.EnableAsync(CancellationToken.None);

        Assert.Empty(processFactory.Starts);
        Assert.False(bridge.Status.X64.HostRunning);
        Assert.True(bridge.Status.X64.HookDllPresent);
        Assert.Contains("missing hook host", bridge.Status.X64.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(hookFiles.ForArchitecture(HookArchitecture.X64).HostExePath, bridge.Status.X64.Message, StringComparison.Ordinal);
        Assert.False(bridge.Status.X86.HostRunning);
        Assert.False(bridge.Status.X86.HookDllPresent);
        Assert.Contains("missing hook DLL", bridge.Status.X86.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(hookFiles.ForArchitecture(HookArchitecture.X86).HookDllPath, bridge.Status.X86.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnableReportsStartFailuresWithoutThrowing()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        hookFiles.CreateHostAndDll(HookArchitecture.X86);
        var processFactory = new RecordingHookHostProcessFactory(
            startResults: new Dictionary<string, Process?>
            {
                ["listary-open-hook-x64"] = null
            },
            startExceptions: new Dictionary<string, Exception>
            {
                ["listary-open-hook-x86"] = new InvalidOperationException("UAC was canceled")
            });
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            CreateEmptyClients(),
            hookFiles.Paths,
            processFactory);

        await bridge.EnableAsync(CancellationToken.None);

        Assert.False(bridge.Status.X64.HostRunning);
        Assert.Contains("did not return a process", bridge.Status.X64.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(bridge.Status.X86.HostRunning);
        Assert.Contains("UAC was canceled", bridge.Status.X86.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnableDoesNotStartDuplicateTrackedHostProcesses()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        hookFiles.CreateHostAndDll(HookArchitecture.X86);
        var processFactory = new RecordingHookHostProcessFactory(startResult: new Process());
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            CreateEmptyClients(),
            hookFiles.Paths,
            processFactory);

        await bridge.EnableAsync(CancellationToken.None);
        await bridge.EnableAsync(CancellationToken.None);

        Assert.Equal(2, processFactory.Starts.Count);
    }

    [Fact]
    public async Task EnableHonorsCancellationBeforeStartingHosts()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        var processFactory = new RecordingHookHostProcessFactory(startResult: new Process());
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            CreateEmptyClients(),
            hookFiles.Paths,
            processFactory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => bridge.EnableAsync(cancellation.Token));
        Assert.Empty(processFactory.Starts);
    }

    [Fact]
    public async Task JumpUsesActiveDialogArchitectureClient()
    {
        var dialog = new HookDialogContext(
            "dlg-1",
            new IntPtr(100),
            200,
            300,
            HookArchitecture.X86,
            "MobaXterm",
            "#32770",
            "Choose which file(s) to upload...",
            DateTimeOffset.UtcNow);
        var client = new RecordingHookClient(dialog, HookJumpResult.Success("Jumped."));
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog.")),
                [HookArchitecture.X86] = client
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.Success, result.Status);
        Assert.Equal("dlg-1", client.LastDialogId);
        Assert.Equal("C:\\Users\\paulx", client.LastFolderPath);
    }

    [Fact]
    public async Task JumpReturnsNoActiveDialogWhenNoClientHasDialog()
    {
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog.")),
                [HookArchitecture.X86] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."))
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.NoActiveDialog, result.Status);
    }

    [Fact]
    public async Task JumpUsesSnapshotOfClientDictionary()
    {
        var dialog = new HookDialogContext(
            "dlg-2",
            new IntPtr(101),
            201,
            301,
            HookArchitecture.X86,
            "MobaXterm",
            "#32770",
            "Choose which file(s) to upload...",
            DateTimeOffset.UtcNow);
        var originalClient = new RecordingHookClient(dialog, HookJumpResult.Success("Jumped."));
        var clients = new Dictionary<HookArchitecture, IHookIpcClient>
        {
            [HookArchitecture.X64] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog.")),
            [HookArchitecture.X86] = originalClient
        };
        var bridge = new HookQuickSwitchBridge(HookQuickSwitchStatus.Disabled(), clients);

        clients[HookArchitecture.X86] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."));
        clients.Remove(HookArchitecture.X64);

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.Success, result.Status);
        Assert.Equal("dlg-2", originalClient.LastDialogId);
    }

    [Fact]
    public void ConstructorRejectsNullClients()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => new HookQuickSwitchBridge(HookQuickSwitchStatus.Disabled(), null!));

        Assert.Equal("clients", exception.ParamName);
    }

    [Fact]
    public void ConstructorRejectsNullClient()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new HookQuickSwitchBridge(
                HookQuickSwitchStatus.Disabled(),
                new Dictionary<HookArchitecture, IHookIpcClient>
                {
                    [HookArchitecture.X64] = null!
                }));

        Assert.Equal("clients", exception.ParamName);
    }

    private sealed class RecordingHookClient : IHookIpcClient
    {
        private readonly HookDialogContext? _activeDialog;
        private readonly HookJumpResult _jumpResult;

        public RecordingHookClient(HookDialogContext? activeDialog, HookJumpResult jumpResult)
        {
            _activeDialog = activeDialog;
            _jumpResult = jumpResult;
        }

        public string? LastDialogId { get; private set; }

        public string? LastFolderPath { get; private set; }

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_activeDialog);

        public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken)
        {
            LastDialogId = dialogId;
            LastFolderPath = folderPath;
            return Task.FromResult(_jumpResult);
        }
    }

    private static IReadOnlyDictionary<HookArchitecture, IHookIpcClient> CreateEmptyClients() =>
        new Dictionary<HookArchitecture, IHookIpcClient>();

    private sealed class RecordingHookHostProcessFactory : HookHostProcessFactory
    {
        private readonly Process? _startResult;
        private readonly IReadOnlyDictionary<string, Process?> _startResults;
        private readonly IReadOnlyDictionary<string, Exception> _startExceptions;

        public RecordingHookHostProcessFactory(Process? startResult)
            : this(startResult, new Dictionary<string, Process?>(), new Dictionary<string, Exception>())
        {
        }

        public RecordingHookHostProcessFactory(
            IReadOnlyDictionary<string, Process?> startResults,
            IReadOnlyDictionary<string, Exception> startExceptions)
            : this(new Process(), startResults, startExceptions)
        {
        }

        private RecordingHookHostProcessFactory(
            Process? startResult,
            IReadOnlyDictionary<string, Process?> startResults,
            IReadOnlyDictionary<string, Exception> startExceptions)
        {
            _startResult = startResult;
            _startResults = startResults;
            _startExceptions = startExceptions;
        }

        public List<RecordedStart> Starts { get; } = new();

        public override Process? Start(ProcessStartInfo startInfo)
        {
            var pipeName = startInfo.ArgumentList[1];
            Starts.Add(new RecordedStart(startInfo));

            if (_startExceptions.TryGetValue(pipeName, out var exception))
            {
                throw exception;
            }

            return _startResults.TryGetValue(pipeName, out var process)
                ? process
                : _startResult;
        }
    }

    private sealed record RecordedStart(ProcessStartInfo StartInfo);

    private sealed class HookFileFixture : IDisposable
    {
        private readonly DirectoryInfo _root;

        private HookFileFixture(DirectoryInfo root)
        {
            _root = root;
            Paths = new HookHostPaths(root.FullName);
        }

        public HookHostPaths Paths { get; }

        public static HookFileFixture Create() =>
            new(Directory.CreateTempSubdirectory("listary-open-hook-bridge-"));

        public HookArchitecturePaths ForArchitecture(HookArchitecture architecture) =>
            Paths.ForArchitecture(architecture);

        public void CreateHostAndDll(HookArchitecture architecture)
        {
            CreateHost(architecture);
            CreateDll(architecture);
        }

        public void CreateHost(HookArchitecture architecture)
        {
            var paths = Paths.ForArchitecture(architecture);
            Directory.CreateDirectory(paths.DirectoryPath);
            File.WriteAllText(paths.HostExePath, string.Empty);
        }

        public void CreateDll(HookArchitecture architecture)
        {
            var paths = Paths.ForArchitecture(architecture);
            Directory.CreateDirectory(paths.DirectoryPath);
            File.WriteAllText(paths.HookDllPath, string.Empty);
        }

        public void Dispose()
        {
            _root.Delete(recursive: true);
        }
    }
}
