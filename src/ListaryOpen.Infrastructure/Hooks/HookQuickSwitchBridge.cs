using System.Diagnostics;
using System.Security.Principal;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookQuickSwitchBridge : IHookQuickSwitchBridge
{
    private static readonly TimeSpan HostStartupProbeInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan HostStartupTimeout = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Bounded graceful shutdown so app exit does not stall on hung UI threads.
    /// Hosts also watch the parent process and finish cleanup after detach.
    /// </summary>
    private static readonly TimeSpan HostShutdownTimeout = TimeSpan.FromSeconds(1.5);
    public const string X64PipeName = "listary-open-hook-x64";
    public const string X86PipeName = "listary-open-hook-x86";

    private readonly IReadOnlyDictionary<HookArchitecture, IHookIpcClient> _clients;
    private readonly HookHostPaths? _hostPaths;
    private readonly HookHostProcessFactory _processFactory;
    private readonly HookHostSession? _session;
    private readonly Func<bool> _requiresElevationToLaunchHosts;
    private readonly SemaphoreSlim _enableGate = new(1, 1);
    private readonly object _hostProcessGate = new();
    private readonly Dictionary<HookArchitecture, Process> _hostProcesses = new();
    private bool _disposed;

    public HookQuickSwitchBridge(
        HookQuickSwitchStatus initialStatus,
        IReadOnlyDictionary<HookArchitecture, IHookIpcClient> clients)
        : this(initialStatus, clients, hostPaths: null, processFactory: null)
    {
    }

    public HookQuickSwitchBridge(
        HookQuickSwitchStatus initialStatus,
        IReadOnlyDictionary<HookArchitecture, IHookIpcClient> clients,
        HookHostPaths? hostPaths,
        HookHostProcessFactory? processFactory,
        HookHostSession? session = null,
        Func<bool>? requiresElevationToLaunchHosts = null)
    {
        ArgumentNullException.ThrowIfNull(initialStatus);
        ArgumentNullException.ThrowIfNull(clients);
        foreach (var client in clients)
        {
            if (client.Value is null)
            {
                throw new ArgumentException("Hook IPC clients cannot contain null values.", nameof(clients));
            }
        }

        Status = initialStatus;
        _clients = clients.ToDictionary(client => client.Key, client => client.Value);
        _hostPaths = hostPaths;
        _processFactory = processFactory ?? new HookHostProcessFactory();
        _session = session;
        // When the UI process is already elevated (e.g. requireAdministrator), child
        // hosts inherit admin rights — a second UAC runas prompt is unnecessary.
        _requiresElevationToLaunchHosts = requiresElevationToLaunchHosts ?? IsElevationRequiredToLaunchHosts;
    }

    public HookQuickSwitchStatus Status { get; private set; }

    public event EventHandler<HookQuickSwitchStatus>? StatusChanged;

    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _enableGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsDisposed())
            {
                return;
            }

            if (_hostPaths is not null)
            {
                var status = await TryEnableBothArchitecturesWithSingleElevatedLaunchAsync(cancellationToken)
                        .ConfigureAwait(false)
                    ?? new HookQuickSwitchStatus(
                        true,
                        await EnableArchitectureAsync(HookArchitecture.X64, cancellationToken).ConfigureAwait(false),
                        await EnableArchitectureAsync(HookArchitecture.X86, cancellationToken).ConfigureAwait(false));

                if (IsDisposed())
                {
                    return;
                }

                Status = status;
            }

            if (!IsDisposed())
            {
                StatusChanged?.Invoke(this, Status);
            }
        }
        finally
        {
            _enableGate.Release();
        }
    }

    public async Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Status.Enabled)
        {
            return null;
        }

        var result = await QueryActiveDialogAsync(cancellationToken).ConfigureAwait(false);
        return result.Dialog;
    }

    public async Task<HookJumpResult> JumpActiveDialogToFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        ValidateFolderPath(folderPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Status.Enabled)
        {
            return HookDisabledResult();
        }

        var activeDialogResult = await QueryActiveDialogAsync(cancellationToken).ConfigureAwait(false);
        var dialog = activeDialogResult.Dialog;
        if (dialog is null)
        {
            return activeDialogResult.Status == HookJumpStatus.NoActiveDialog
                ? new HookJumpResult(HookJumpStatus.NoActiveDialog, activeDialogResult.Message)
                : new HookJumpResult(activeDialogResult.Status, activeDialogResult.Message);
        }

        return await JumpDialogToFolderAsync(dialog, folderPath, cancellationToken).ConfigureAwait(false);
    }

    public Task<HookJumpResult> JumpDialogToFolderAsync(
        HookDialogContext dialog,
        string folderPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ValidateFolderPath(folderPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Status.Enabled)
        {
            return Task.FromResult(HookDisabledResult());
        }

        if (!_clients.TryGetValue(dialog.Architecture, out var client))
        {
            return Task.FromResult(
                new HookJumpResult(
                    HookJumpStatus.HostUnavailable,
                    $"{dialog.Architecture} hook host is not available."));
        }

        return client.JumpDialogToFolderAsync(dialog.DialogId, folderPath, cancellationToken);
    }

    private async Task<HookActiveDialogResult> QueryActiveDialogAsync(CancellationToken cancellationToken)
    {
        HookActiveDialogResult? firstFailure = null;
        HookActiveDialogResult? bestNoActiveDialog = null;

        foreach (var client in _clients)
        {
            var result = await GetActiveDialogResultAsync(client.Value, cancellationToken).ConfigureAwait(false);
            if (result.Dialog is not null)
            {
                return result;
            }

            if (result.Status == HookJumpStatus.NoActiveDialog)
            {
                var noActiveDialog = WithArchitectureMessage(client.Key, result);
                if (bestNoActiveDialog is null || IsGenericNoActiveDialogMessage(bestNoActiveDialog.Message))
                {
                    bestNoActiveDialog = noActiveDialog;
                }

                continue;
            }

            if (result.Status != HookJumpStatus.NoActiveDialog)
            {
                firstFailure ??= result;
            }
        }

        return firstFailure ?? bestNoActiveDialog ?? HookActiveDialogResult.NoActiveDialog("No active hook-controlled dialog.");
    }

    private static HookActiveDialogResult WithArchitectureMessage(
        HookArchitecture architecture,
        HookActiveDialogResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? $"{architecture.ToFolderName()} hook host reported no active dialog."
            : $"{architecture.ToFolderName()} hook host: {result.Message}";
        return new HookActiveDialogResult(result.Status, message, result.Dialog);
    }

    private static bool IsGenericNoActiveDialogMessage(string message) =>
        message.Contains("No active hook-controlled dialog", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("No supported foreground hook dialog", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("does not match", StringComparison.OrdinalIgnoreCase);

    private static async Task<HookActiveDialogResult> GetActiveDialogResultAsync(
        IHookIpcClient client,
        CancellationToken cancellationToken)
    {
        if (client is IHookActiveDialogQueryClient activeDialogQueryClient)
        {
            return await activeDialogQueryClient.GetActiveDialogResultAsync(cancellationToken).ConfigureAwait(false);
        }

        var dialog = await client.GetActiveDialogAsync(cancellationToken).ConfigureAwait(false);
        return dialog is null
            ? HookActiveDialogResult.NoActiveDialog("No active hook-controlled dialog.")
            : HookActiveDialogResult.Active(dialog);
    }

    public void Dispose()
    {
        Process[] hostProcesses;
        lock (_hostProcessGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            hostProcesses = _hostProcesses.Values.Distinct().ToArray();
            _hostProcesses.Clear();
        }

        try
        {
            var gracefulShutdownAccepted = hostProcesses.Length > 0 && RequestGracefulHostShutdown();

            foreach (var client in _clients.Values.OfType<IDisposable>())
            {
                client.Dispose();
            }

            foreach (var process in hostProcesses)
            {
                if (gracefulShutdownAccepted)
                {
                    // Bounded wait (see HookHostProcessFactory.TerminationTimeout). On
                    // timeout the host is detached, not killed, so native cleanup can
                    // finish after the parent process exits.
                    _processFactory.WaitForGracefulExitOrTerminate(process);
                }
                else
                {
                    _processFactory.Terminate(process);
                }
            }
        }
        finally
        {
            _hostPaths?.Dispose();
        }
    }

    private void GracefullyTerminateStartedProcess(Process process)
    {
        if (RequestGracefulHostShutdown())
        {
            _processFactory.WaitForGracefulExitOrTerminate(process);
        }
        else
        {
            _processFactory.Terminate(process);
        }
    }

    private bool RequestGracefulHostShutdown()
    {
        using var cancellation = new CancellationTokenSource(HostShutdownTimeout);
        var accepted = false;
        foreach (var client in _clients.Values.OfType<IHookShutdownClient>().Distinct())
        {
            try
            {
                var result = client.ShutdownAsync(cancellation.Token).GetAwaiter().GetResult();
                accepted |= result.Status == HookJumpStatus.Success;
            }
            catch (Exception exception) when (exception is OperationCanceledException
                                               or System.IO.IOException
                                               or InvalidOperationException)
            {
            }
        }

        return accepted;
    }

    private async Task<HookArchitectureStatus> EnableArchitectureAsync(
        HookArchitecture architecture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var architectureName = architecture.ToFolderName();
        if (IsDisposed())
        {
            return new HookArchitectureStatus(
                architecture,
                true,
                false,
                false,
                $"{architectureName} hook host start was abandoned.");
        }

        var paths = _hostPaths!.ForArchitecture(architecture);
        var snapshot = paths.Snapshot();

        if (TryGetRunningTrackedProcess(architecture, out _))
        {
            return await TryCreateHealthyExistingHostStatusAsync(
                    architecture,
                    snapshot,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? CreateHealthUnconfirmedStatus(
                    architecture,
                    snapshot.HookDllExists,
                    $"{architectureName} hook host is running, but health was not confirmed.");
        }

        var healthyStatus = await TryCreateHealthyExistingHostStatusAsync(
                architecture,
                snapshot,
                cancellationToken)
            .ConfigureAwait(false);
        if (healthyStatus is not null)
        {
            return healthyStatus;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed())
        {
            return new HookArchitectureStatus(
                architecture,
                true,
                false,
                snapshot.HookDllExists,
                $"{architectureName} hook host start was abandoned.");
        }

        if (!snapshot.HostExists || !snapshot.HookDllExists)
        {
            return CreateUnavailableStatus(snapshot);
        }

        try
        {
            var startInfo = _processFactory.CreateStartInfo(
                paths.HostExePath,
                GetPipeName(architecture),
                paths.HookDllPath,
                elevated: _requiresElevationToLaunchHosts(),
                parentProcessId: _session?.ParentProcessId,
                secret: _session?.Secret);
            var process = _processFactory.Start(startInfo);
            if (process is null)
            {
                return new HookArchitectureStatus(
                    architecture,
                    true,
                    false,
                    true,
                    $"{architectureName} hook host start did not return a process.");
            }

            var terminateStartedProcess = false;
            lock (_hostProcessGate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    terminateStartedProcess = true;
                }
                else
                {
                    _hostProcesses[architecture] = process;
                }
            }

            if (terminateStartedProcess)
            {
                GracefullyTerminateStartedProcess(process);
                cancellationToken.ThrowIfCancellationRequested();
                return new HookArchitectureStatus(
                    architecture,
                    true,
                    false,
                    true,
                    $"{architectureName} hook host start was abandoned.");
            }

            return await WaitForHealthyHostStatusAsync(
                    architecture,
                    snapshot,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? CreateHealthUnconfirmedStatus(
                    architecture,
                    hookDllPresent: true,
                    $"{architectureName} hook host started, but health was not confirmed.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HookArchitectureStatus(
                architecture,
                true,
                false,
                true,
                $"{architectureName} hook host failed to start: {exception.Message}");
        }
    }

    private async Task<HookQuickSwitchStatus?> TryEnableBothArchitecturesWithSingleElevatedLaunchAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_hostPaths is null || IsDisposed())
        {
            return null;
        }

        if (TryGetRunningTrackedProcess(HookArchitecture.X64, out _)
            || TryGetRunningTrackedProcess(HookArchitecture.X86, out _))
        {
            return null;
        }

        var x64Paths = _hostPaths.ForArchitecture(HookArchitecture.X64);
        var x86Paths = _hostPaths.ForArchitecture(HookArchitecture.X86);
        var x64Snapshot = x64Paths.Snapshot();
        var x86Snapshot = x86Paths.Snapshot();
        if (!x64Snapshot.HostExists
            || !x64Snapshot.HookDllExists
            || !x86Snapshot.HostExists
            || !x86Snapshot.HookDllExists)
        {
            return null;
        }

        var x64HealthyStatus = await TryCreateHealthyExistingHostStatusAsync(
                HookArchitecture.X64,
                x64Snapshot,
                cancellationToken)
            .ConfigureAwait(false);
        if (x64HealthyStatus is not null)
        {
            return null;
        }

        var x86HealthyStatus = await TryCreateHealthyExistingHostStatusAsync(
                HookArchitecture.X86,
                x86Snapshot,
                cancellationToken)
            .ConfigureAwait(false);
        if (x86HealthyStatus is not null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (IsDisposed())
        {
            return null;
        }

        try
        {
            var startInfo = _processFactory.CreateStartInfo(
                x64Paths.HostExePath,
                GetPipeName(HookArchitecture.X64),
                x64Paths.HookDllPath,
                elevated: _requiresElevationToLaunchHosts(),
                new[]
                {
                    new HookHostLaunchRequest(
                        x86Paths.HostExePath,
                        GetPipeName(HookArchitecture.X86),
                        x86Paths.HookDllPath)
                },
                _session?.ParentProcessId,
                _session?.Secret);
            var process = _processFactory.Start(startInfo);
            if (process is null)
            {
                return new HookQuickSwitchStatus(
                    true,
                    new HookArchitectureStatus(
                        HookArchitecture.X64,
                        true,
                        false,
                        true,
                        "x64 hook host start did not return a process."),
                    new HookArchitectureStatus(
                        HookArchitecture.X86,
                        true,
                        false,
                        true,
                        "x86 hook host launch was skipped because x64 hook host failed to start."));
            }

            var terminateStartedProcess = false;
            lock (_hostProcessGate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    terminateStartedProcess = true;
                }
                else
                {
                    _hostProcesses[HookArchitecture.X64] = process;
                }
            }

            if (terminateStartedProcess)
            {
                GracefullyTerminateStartedProcess(process);
                cancellationToken.ThrowIfCancellationRequested();
                return new HookQuickSwitchStatus(
                    true,
                    new HookArchitectureStatus(
                        HookArchitecture.X64,
                        true,
                        false,
                        true,
                        "x64 hook host start was abandoned."),
                    new HookArchitectureStatus(
                        HookArchitecture.X86,
                        true,
                        false,
                        true,
                        "x86 hook host launch was abandoned."));
            }

            var x64Status = await WaitForHealthyHostStatusAsync(
                    HookArchitecture.X64,
                    x64Snapshot,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? CreateHealthUnconfirmedStatus(
                    HookArchitecture.X64,
                    hookDllPresent: true,
                    "x64 hook host started, but health was not confirmed.");
            var x86Status = await WaitForHealthyHostStatusAsync(
                    HookArchitecture.X86,
                    x86Snapshot,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? CreateHealthUnconfirmedStatus(
                    HookArchitecture.X86,
                    hookDllPresent: true,
                    "x86 hook host launch requested by x64 hook host, but health was not confirmed.");

            return new HookQuickSwitchStatus(
                true,
                x64Status,
                x86Status);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new HookQuickSwitchStatus(
                true,
                new HookArchitectureStatus(
                    HookArchitecture.X64,
                    true,
                    false,
                    true,
                    $"x64 hook host failed to start: {exception.Message}"),
                new HookArchitectureStatus(
                    HookArchitecture.X86,
                    true,
                    false,
                    true,
                    "x86 hook host launch was skipped because x64 hook host failed to start."));
        }
    }

    private async Task<HookArchitectureStatus?> TryCreateHealthyExistingHostStatusAsync(
        HookArchitecture architecture,
        HookArchitecturePathSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(architecture, out var client)
            || client is not IHookHealthProbeClient healthProbeClient)
        {
            return null;
        }

        try
        {
            var result = await healthProbeClient.ProbeHealthAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status != HookJumpStatus.Success)
            {
                return null;
            }

            var architectureName = architecture.ToFolderName();
            return new HookArchitectureStatus(
                architecture,
                true,
                true,
                snapshot.HookDllExists,
                string.IsNullOrWhiteSpace(result.Message)
                    ? $"{architectureName} hook host is healthy."
                    : $"{architectureName} hook host is healthy: {result.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<HookArchitectureStatus?> WaitForHealthyHostStatusAsync(
        HookArchitecture architecture,
        HookArchitecturePathSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(architecture, out var client)
            || client is not IHookHealthProbeClient)
        {
            return null;
        }

        var deadline = DateTime.UtcNow + HostStartupTimeout;
        do
        {
            var status = await TryCreateHealthyExistingHostStatusAsync(
                    architecture,
                    snapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (status is not null)
            {
                return status;
            }

            await Task.Delay(HostStartupProbeInterval, cancellationToken).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    private static HookArchitectureStatus CreateUnavailableStatus(HookArchitecturePathSnapshot snapshot)
    {
        var architectureName = snapshot.Architecture.ToFolderName();
        var missingParts = new List<string>();
        if (!snapshot.HostExists)
        {
            missingParts.Add($"missing hook host '{snapshot.HostExePath}'");
        }

        if (!snapshot.HookDllExists)
        {
            missingParts.Add($"missing hook DLL '{snapshot.HookDllPath}'");
        }

        return new HookArchitectureStatus(
            snapshot.Architecture,
            true,
            false,
            snapshot.HookDllExists,
            $"{architectureName} unavailable: {string.Join(" and ", missingParts)}.");
    }

    private static HookArchitectureStatus CreateHealthUnconfirmedStatus(
        HookArchitecture architecture,
        bool hookDllPresent,
        string message) =>
        new(architecture, true, false, hookDllPresent, message);

    private bool TryGetRunningTrackedProcess(HookArchitecture architecture, out Process? process)
    {
        lock (_hostProcessGate)
        {
            if (!_hostProcesses.TryGetValue(architecture, out process))
            {
                return false;
            }

            if (_processFactory.IsRunning(process))
            {
                return true;
            }

            var staleProcess = process;
            foreach (var staleArchitecture in _hostProcesses
                         .Where(entry => ReferenceEquals(entry.Value, staleProcess))
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _hostProcesses.Remove(staleArchitecture);
            }
        }

        _processFactory.Terminate(process);
        process = null;
        return false;
    }

    internal static bool IsElevationRequiredToLaunchHosts()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return !principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is SystemException or System.Security.SecurityException)
        {
            return true;
        }
    }

    private bool IsDisposed()
    {
        lock (_hostProcessGate)
        {
            return _disposed;
        }
    }

    private static void ValidateFolderPath(string folderPath)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path cannot be empty.", nameof(folderPath));
        }
    }

    private static HookJumpResult HookDisabledResult() =>
        new(HookJumpStatus.HostUnavailable, "Hook quick switch is disabled.");

    private string GetPipeName(HookArchitecture architecture) =>
        architecture switch
        {
            HookArchitecture.X64 => _session?.X64PipeName ?? X64PipeName,
            HookArchitecture.X86 => _session?.X86PipeName ?? X86PipeName,
            _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null)
        };
}
