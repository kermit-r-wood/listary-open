using System.Diagnostics;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookQuickSwitchBridge : IHookQuickSwitchBridge
{
    public const string X64PipeName = "listary-open-hook-x64";
    public const string X86PipeName = "listary-open-hook-x86";

    private readonly IReadOnlyDictionary<HookArchitecture, IHookIpcClient> _clients;
    private readonly HookHostPaths? _hostPaths;
    private readonly HookHostProcessFactory _processFactory;
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
        HookHostProcessFactory? processFactory)
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
                var status = new HookQuickSwitchStatus(
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
        foreach (var client in _clients.Values)
        {
            var dialog = await client.GetActiveDialogAsync(cancellationToken).ConfigureAwait(false);
            if (dialog is not null)
            {
                return dialog;
            }
        }

        return null;
    }

    public async Task<HookJumpResult> JumpActiveDialogToFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path cannot be empty.", nameof(folderPath));
        }

        var dialog = await GetActiveDialogAsync(cancellationToken).ConfigureAwait(false);
        if (dialog is null)
        {
            return new HookJumpResult(HookJumpStatus.NoActiveDialog, "No active hook-controlled dialog.");
        }

        if (!_clients.TryGetValue(dialog.Architecture, out var client))
        {
            return new HookJumpResult(HookJumpStatus.HostUnavailable, $"{dialog.Architecture} hook host is not available.");
        }

        return await client.JumpDialogToFolderAsync(dialog.DialogId, folderPath, cancellationToken).ConfigureAwait(false);
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

        foreach (var client in _clients.Values.OfType<IDisposable>())
        {
            client.Dispose();
        }

        foreach (var process in hostProcesses)
        {
            _processFactory.Terminate(process);
        }
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
            return new HookArchitectureStatus(
                architecture,
                true,
                true,
                snapshot.HookDllExists,
                $"{architectureName} hook host is already running.");
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
                elevated: true);
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
                _processFactory.Terminate(process);
                cancellationToken.ThrowIfCancellationRequested();
                return new HookArchitectureStatus(
                    architecture,
                    true,
                    false,
                    true,
                    $"{architectureName} hook host start was abandoned.");
            }

            return new HookArchitectureStatus(
                architecture,
                true,
                true,
                true,
                $"{architectureName} hook host started.");
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

            _hostProcesses.Remove(architecture);
        }

        _processFactory.Terminate(process);
        process = null;
        return false;
    }

    private bool IsDisposed()
    {
        lock (_hostProcessGate)
        {
            return _disposed;
        }
    }

    private static string GetPipeName(HookArchitecture architecture) =>
        architecture switch
        {
            HookArchitecture.X64 => X64PipeName,
            HookArchitecture.X86 => X86PipeName,
            _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, null)
        };
}
