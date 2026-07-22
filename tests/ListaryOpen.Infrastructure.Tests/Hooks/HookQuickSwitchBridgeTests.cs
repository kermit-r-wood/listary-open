using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookQuickSwitchBridgeTests
{
    [Fact]
    public async Task EnableStartsAvailableHostsWithSingleElevatedLaunchAndRaisesOneStatusChangedEvent()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        hookFiles.CreateHostAndDll(HookArchitecture.X86);
        var processFactory = new RecordingHookHostProcessFactory(startResult: new Process());
        var x64HealthClient = new SequenceHealthProbeHookClient(
            new HookJumpResult(HookJumpStatus.HostUnavailable, "No host."),
            HookJumpResult.Success("Hook host healthy."));
        var x86HealthClient = new SequenceHealthProbeHookClient(
            new HookJumpResult(HookJumpStatus.HostUnavailable, "No host."),
            new HookJumpResult(HookJumpStatus.HostUnavailable, "Host is starting."),
            HookJumpResult.Success("Hook host healthy."));
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = x64HealthClient,
                [HookArchitecture.X86] = x86HealthClient
            },
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
        Assert.Equal(2, x64HealthClient.ProbeCount);
        Assert.Equal(3, x86HealthClient.ProbeCount);
        var start = Assert.Single(processFactory.Starts);
        Assert.Equal(hookFiles.ForArchitecture(HookArchitecture.X64).HostExePath, start.StartInfo.FileName);
        Assert.Equal(
            new[]
            {
                "--pipe",
                "listary-open-hook-x64",
                "--dll",
                hookFiles.ForArchitecture(HookArchitecture.X64).HookDllPath,
                "--launch-host",
                hookFiles.ForArchitecture(HookArchitecture.X86).HostExePath,
                "--launch-pipe",
                "listary-open-hook-x86",
                "--launch-dll",
                hookFiles.ForArchitecture(HookArchitecture.X86).HookDllPath
            },
            start.StartInfo.ArgumentList);
    }

    [Fact]
    public async Task EnableDoesNotMarkLaunchedHostsRunningWithoutHealthProof()
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

        Assert.False(bridge.Status.X64.HostRunning);
        Assert.Contains("health was not confirmed", bridge.Status.X64.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(bridge.Status.X86.HostRunning);
        Assert.Contains("health was not confirmed", bridge.Status.X86.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnableUsesHealthyExistingHostWithoutStartingDuplicateProcess()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        hookFiles.CreateHostAndDll(HookArchitecture.X86);
        var processFactory = new RecordingHookHostProcessFactory(startResult: new Process());
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new HealthProbeHookClient(HookJumpResult.Success("Hook host healthy.")),
                [HookArchitecture.X86] = new HealthProbeHookClient(new HookJumpResult(HookJumpStatus.HostUnavailable, "No host."))
            },
            hookFiles.Paths,
            processFactory);

        await bridge.EnableAsync(CancellationToken.None);

        Assert.True(bridge.Status.X64.HostRunning);
        Assert.Contains("healthy", bridge.Status.X64.Message, StringComparison.OrdinalIgnoreCase);
        var start = Assert.Single(processFactory.Starts);
        Assert.Equal("listary-open-hook-x86", start.StartInfo.ArgumentList[1]);
    }

    [Fact]
    public async Task ConcurrentEnableStartsAtMostOneHostPerArchitecture()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        hookFiles.CreateHostAndDll(HookArchitecture.X86);
        var processFactory = new BlockingHookHostProcessFactory();
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            CreateEmptyClients(),
            hookFiles.Paths,
            processFactory);

        var firstEnable = Task.Run(() => bridge.EnableAsync(CancellationToken.None));
        await processFactory.FirstStartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondEnable = Task.Run(() => bridge.EnableAsync(CancellationToken.None));
        await Task.Delay(100);

        processFactory.Release();
        await Task.WhenAll(firstEnable, secondEnable).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, processFactory.Starts.Count);
        Assert.Equal("listary-open-hook-x64", processFactory.Starts[0].StartInfo.ArgumentList[1]);
        Assert.Contains("--launch-pipe", processFactory.Starts[0].StartInfo.ArgumentList);
        Assert.Equal("listary-open-hook-x86", processFactory.Starts[1].StartInfo.ArgumentList[1]);
    }

    [Fact]
    public async Task DisposeDuringHostStartTerminatesReturnedProcessAndSkipsStatusUpdate()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        hookFiles.CreateHostAndDll(HookArchitecture.X86);
        var processFactory = new BlockingHookHostProcessFactory();
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            CreateEmptyClients(),
            hookFiles.Paths,
            processFactory);
        var statusChangedCount = 0;
        bridge.StatusChanged += (_, _) => statusChangedCount++;

        var enableTask = Task.Run(() => bridge.EnableAsync(CancellationToken.None));
        await processFactory.FirstStartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        bridge.Dispose();
        processFactory.Release();
        await enableTask.WaitAsync(TimeSpan.FromSeconds(2));
        bridge.Dispose();

        Assert.Single(processFactory.Starts);
        var terminated = Assert.Single(processFactory.TerminatedProcesses);
        Assert.Same(processFactory.ReturnedProcesses.Single(), terminated);
        Assert.Equal(0, statusChangedCount);
    }

    [Fact]
    public async Task DisposeDuringHealthProbeSkipsHostStartAndStatusUpdate()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        hookFiles.CreateHostAndDll(HookArchitecture.X86);
        var healthClient = new BlockingHealthProbeHookClient();
        var processFactory = new RecordingHookHostProcessFactory(startResult: new Process());
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = healthClient
            },
            hookFiles.Paths,
            processFactory);
        var statusChangedCount = 0;
        bridge.StatusChanged += (_, _) => statusChangedCount++;

        var enableTask = Task.Run(() => bridge.EnableAsync(CancellationToken.None));
        await healthClient.ProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        bridge.Dispose();
        healthClient.Release(new HookJumpResult(HookJumpStatus.HostUnavailable, "No host."));
        await enableTask.WaitAsync(TimeSpan.FromSeconds(2));
        bridge.Dispose();

        Assert.Empty(processFactory.Starts);
        Assert.Equal(0, statusChangedCount);
    }

    [Fact]
    public async Task DisposeRequestsGracefulShutdownBeforeWaitingForOwnedHostExit()
    {
        using var hookFiles = HookFileFixture.Create();
        hookFiles.CreateHostAndDll(HookArchitecture.X64);
        hookFiles.CreateHostAndDll(HookArchitecture.X86);
        var x64Client = new ShutdownHealthHookClient(
            new HookJumpResult(HookJumpStatus.HostUnavailable, "No host."),
            HookJumpResult.Success("Hook host healthy."));
        var x86Client = new ShutdownHealthHookClient(
            new HookJumpResult(HookJumpStatus.HostUnavailable, "No host."),
            HookJumpResult.Success("Hook host healthy."));
        var processFactory = new ShutdownOrderingProcessFactory(
            () => x64Client.ShutdownCount == 1 && x86Client.ShutdownCount == 1);
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = x64Client,
                [HookArchitecture.X86] = x86Client
            },
            hookFiles.Paths,
            processFactory);

        await bridge.EnableAsync(CancellationToken.None);
        bridge.Dispose();

        Assert.Equal(1, x64Client.ShutdownCount);
        Assert.Equal(1, x86Client.ShutdownCount);
        Assert.True(processFactory.GracefulWaitCalled);
        Assert.True(processFactory.ShutdownWasObservedBeforeWait);
        Assert.False(processFactory.ImmediateTerminateCalled);
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
        Assert.Contains("x86 hook host launch was skipped", bridge.Status.X86.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnableRetriesX86HostWhenChildLaunchHealthWasNotConfirmed()
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
        Assert.Equal("listary-open-hook-x86", processFactory.Starts[1].StartInfo.ArgumentList[1]);
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
            EnabledStatus(),
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
    public async Task JumpToCapturedDialogUsesTargetArchitectureWithoutQueryingActiveDialogs()
    {
        var dialog = new HookDialogContext(
            "dlg-captured",
            new IntPtr(100),
            200,
            300,
            HookArchitecture.X86,
            "MobaXterm",
            "#32770",
            "Choose which file(s) to upload...",
            DateTimeOffset.UtcNow);
        var x64Client = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."));
        var x86Client = new RecordingHookClient(null, HookJumpResult.Success("Jumped."));
        var bridge = new HookQuickSwitchBridge(
            EnabledStatus(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = x64Client,
                [HookArchitecture.X86] = x86Client
            });

        var result = await bridge.JumpDialogToFolderAsync(
            dialog,
            "C:\\Users\\paulx",
            CancellationToken.None);

        Assert.Equal(HookJumpStatus.Success, result.Status);
        Assert.Equal(0, x64Client.ActiveDialogQueryCount);
        Assert.Equal(0, x86Client.ActiveDialogQueryCount);
        Assert.Null(x64Client.LastDialogId);
        Assert.Equal("dlg-captured", x86Client.LastDialogId);
        Assert.Equal("C:\\Users\\paulx", x86Client.LastFolderPath);
    }

    [Fact]
    public async Task JumpReturnsNoActiveDialogWhenNoClientHasDialog()
    {
        var bridge = new HookQuickSwitchBridge(
            EnabledStatus(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog.")),
                [HookArchitecture.X86] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."))
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.NoActiveDialog, result.Status);
    }

    [Fact]
    public async Task JumpReturnsHostUnavailableWhenActiveDialogQueryPipeIsMissing()
    {
        var client = new HookIpcClient("listary-open-missing-active-" + Guid.NewGuid(), TimeSpan.FromMilliseconds(50));
        var bridge = new HookQuickSwitchBridge(
            EnabledStatus(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = client
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.HostUnavailable, result.Status);
        Assert.DoesNotContain("No active hook-controlled dialog", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JumpReturnsTimeoutWhenActiveDialogQueryHostDoesNotReply()
    {
        var pipeName = "listary-open-active-timeout-" + Guid.NewGuid();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = ServeWithoutReplyOnceAsync(pipeName, serverCancellation.Token);
        var client = new HookIpcClient(pipeName, TimeSpan.FromMilliseconds(100));
        var bridge = new HookQuickSwitchBridge(
            EnabledStatus(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = client
            });

        try
        {
            var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

            Assert.Equal(HookJumpStatus.Timeout, result.Status);
            Assert.DoesNotContain("No active hook-controlled dialog", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await StopServerAsync(serverCancellation, serverTask);
        }
    }

    [Fact]
    public async Task JumpPreservesSpecificNoActiveDialogDiagnostics()
    {
        var bridge = new HookQuickSwitchBridge(
            EnabledStatus(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new ActiveDialogResultHookClient(
                    HookActiveDialogResult.NoActiveDialog("No supported foreground hook dialog.")),
                [HookArchitecture.X86] = new ActiveDialogResultHookClient(
                    HookActiveDialogResult.NoActiveDialog("Target dialog hook has not been installed yet for process 10 thread 20."))
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.NoActiveDialog, result.Status);
        Assert.Contains("x86 hook host", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Target dialog hook has not been installed yet", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task JumpPrefersTargetArchitecturePendingDiagnosticOverMismatch()
    {
        var bridge = new HookQuickSwitchBridge(
            EnabledStatus(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new ActiveDialogResultHookClient(
                    HookActiveDialogResult.NoActiveDialog("Active dialog architecture x86 does not match x64 hook host.")),
                [HookArchitecture.X86] = new ActiveDialogResultHookClient(
                    HookActiveDialogResult.NoActiveDialog("Target dialog hook has not been installed yet for process 10 thread 20."))
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.NoActiveDialog, result.Status);
        Assert.Contains("x86 hook host", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Target dialog hook has not been installed yet", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not match", result.Message, StringComparison.OrdinalIgnoreCase);
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
        var bridge = new HookQuickSwitchBridge(EnabledStatus(), clients);

        clients[HookArchitecture.X86] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."));
        clients.Remove(HookArchitecture.X64);

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.Success, result.Status);
        Assert.Equal("dlg-2", originalClient.LastDialogId);
    }

    [Fact]
    public async Task DisabledBridgeDoesNotQueryOrJumpHookClients()
    {
        var dialog = new HookDialogContext(
            "disabled-dialog",
            new IntPtr(101),
            201,
            301,
            HookArchitecture.X64,
            "notepad",
            "#32770",
            "Open",
            DateTimeOffset.UtcNow);
        var client = new RecordingHookClient(dialog, HookJumpResult.Success("Jumped."));
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = client
            });

        var activeDialog = await bridge.GetActiveDialogAsync(CancellationToken.None);
        var activeJump = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);
        var capturedJump = await bridge.JumpDialogToFolderAsync(
            dialog,
            "C:\\Users\\paulx",
            CancellationToken.None);

        Assert.Null(activeDialog);
        Assert.Equal(HookJumpStatus.HostUnavailable, activeJump.Status);
        Assert.Equal(HookJumpStatus.HostUnavailable, capturedJump.Status);
        Assert.Equal(0, client.ActiveDialogQueryCount);
        Assert.Null(client.LastDialogId);
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

        public int ActiveDialogQueryCount { get; private set; }

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken)
        {
            ActiveDialogQueryCount++;
            return Task.FromResult(_activeDialog);
        }

        public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken)
        {
            LastDialogId = dialogId;
            LastFolderPath = folderPath;
            return Task.FromResult(_jumpResult);
        }
    }

    private static HookQuickSwitchStatus EnabledStatus() => new(
        true,
        new HookArchitectureStatus(HookArchitecture.X64, true, true, true, "x64 healthy"),
        new HookArchitectureStatus(HookArchitecture.X86, true, true, true, "x86 healthy"));

    private sealed class HealthProbeHookClient : IHookIpcClient, IHookHealthProbeClient
    {
        private readonly HookJumpResult _healthResult;

        public HealthProbeHookClient(HookJumpResult healthResult)
        {
            _healthResult = healthResult;
        }

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<HookDialogContext?>(null);

        public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken) =>
            Task.FromResult(new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."));

        public Task<HookJumpResult> ProbeHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_healthResult);
    }

    private sealed class SequenceHealthProbeHookClient : IHookIpcClient, IHookHealthProbeClient
    {
        private readonly Queue<HookJumpResult> _healthResults;

        public SequenceHealthProbeHookClient(params HookJumpResult[] healthResults)
        {
            _healthResults = new Queue<HookJumpResult>(healthResults);
        }

        public int ProbeCount { get; private set; }

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<HookDialogContext?>(null);

        public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken) =>
            Task.FromResult(new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."));

        public Task<HookJumpResult> ProbeHealthAsync(CancellationToken cancellationToken)
        {
            ProbeCount++;
            return Task.FromResult(_healthResults.Count == 0
                ? new HookJumpResult(HookJumpStatus.HostUnavailable, "No host.")
                : _healthResults.Dequeue());
        }
    }

    private sealed class ShutdownHealthHookClient : IHookIpcClient, IHookHealthProbeClient, IHookShutdownClient
    {
        private readonly Queue<HookJumpResult> _healthResults;

        public ShutdownHealthHookClient(params HookJumpResult[] healthResults)
        {
            _healthResults = new Queue<HookJumpResult>(healthResults);
        }

        public int ShutdownCount { get; private set; }

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<HookDialogContext?>(null);

        public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken) =>
            Task.FromResult(new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."));

        public Task<HookJumpResult> ProbeHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_healthResults.Count == 0
                ? HookJumpResult.Success("Hook host healthy.")
                : _healthResults.Dequeue());

        public Task<HookJumpResult> ShutdownAsync(CancellationToken cancellationToken)
        {
            ShutdownCount++;
            return Task.FromResult(HookJumpResult.Success("Hook host graceful shutdown accepted."));
        }
    }

    private sealed class ActiveDialogResultHookClient : IHookIpcClient, IHookActiveDialogQueryClient
    {
        private readonly HookActiveDialogResult _result;

        public ActiveDialogResultHookClient(HookActiveDialogResult result)
        {
            _result = result;
        }

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_result.Dialog);

        public Task<HookActiveDialogResult> GetActiveDialogResultAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_result);

        public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken) =>
            Task.FromResult(new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."));
    }

    private sealed class BlockingHealthProbeHookClient : IHookIpcClient, IHookHealthProbeClient
    {
        private readonly TaskCompletionSource<HookJumpResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ProbeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
            Task.FromResult<HookDialogContext?>(null);

        public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken) =>
            Task.FromResult(new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."));

        public Task<HookJumpResult> ProbeHealthAsync(CancellationToken cancellationToken)
        {
            ProbeEntered.TrySetResult();
            return _result.Task.WaitAsync(cancellationToken);
        }

        public void Release(HookJumpResult result) => _result.TrySetResult(result);
    }

    private static IReadOnlyDictionary<HookArchitecture, IHookIpcClient> CreateEmptyClients() =>
        new Dictionary<HookArchitecture, IHookIpcClient>();

    private static async Task ServeWithoutReplyOnceAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync(cancellationToken);
        _ = await ReadLineAsync(server, cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var buffer = new byte[256];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            }

            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] == '\n')
                {
                    if (line.Count > 0 && line[^1] == '\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }

                    return Encoding.UTF8.GetString(line.ToArray());
                }

                line.Add(buffer[index]);
            }
        }
    }

    private static async Task StopServerAsync(CancellationTokenSource cancellation, Task task)
    {
        await cancellation.CancelAsync();

        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellation.IsCancellationRequested)
        {
        }
    }

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

    private sealed class ShutdownOrderingProcessFactory : HookHostProcessFactory
    {
        private readonly Func<bool> _shutdownObserved;

        public ShutdownOrderingProcessFactory(Func<bool> shutdownObserved)
        {
            _shutdownObserved = shutdownObserved;
        }

        public bool GracefulWaitCalled { get; private set; }

        public bool ShutdownWasObservedBeforeWait { get; private set; }

        public bool ImmediateTerminateCalled { get; private set; }

        public override Process? Start(ProcessStartInfo startInfo) => new();

        public override void WaitForGracefulExitOrTerminate(Process process)
        {
            GracefulWaitCalled = true;
            ShutdownWasObservedBeforeWait = _shutdownObserved();
        }

        public override void Terminate(Process process)
        {
            ImmediateTerminateCalled = true;
        }
    }

    private sealed class BlockingHookHostProcessFactory : HookHostProcessFactory
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();

        public TaskCompletionSource FirstStartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<RecordedStart> Starts { get; } = new();

        public List<Process> ReturnedProcesses { get; } = new();

        public List<Process> TerminatedProcesses { get; } = new();

        public override Process? Start(ProcessStartInfo startInfo)
        {
            lock (_gate)
            {
                Starts.Add(new RecordedStart(startInfo));
                if (Starts.Count == 1)
                {
                    FirstStartEntered.TrySetResult();
                }
            }

            _release.Task.GetAwaiter().GetResult();
            var process = new Process();
            lock (_gate)
            {
                ReturnedProcesses.Add(process);
            }

            return process;
        }

        public override void Terminate(Process process)
        {
            lock (_gate)
            {
                TerminatedProcesses.Add(process);
            }
        }

        public void Release() => _release.TrySetResult();
    }

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
