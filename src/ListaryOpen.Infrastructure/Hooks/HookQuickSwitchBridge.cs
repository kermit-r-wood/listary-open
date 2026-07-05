using System.ComponentModel;
using System.Diagnostics;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookQuickSwitchBridge : IHookQuickSwitchBridge
{
    public const string X64PipeName = "listary-open-hook-x64";
    public const string X86PipeName = "listary-open-hook-x86";

    private readonly IReadOnlyDictionary<HookArchitecture, IHookIpcClient> _clients;
    private readonly HookHostPaths? _hostPaths;
    private readonly HookHostProcessFactory _processFactory;
    private readonly object _hostProcessGate = new();
    private readonly Dictionary<HookArchitecture, Process> _hostProcesses = new();

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

    public Task EnableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_hostPaths is not null)
        {
            Status = new HookQuickSwitchStatus(
                true,
                EnableArchitecture(HookArchitecture.X64, cancellationToken),
                EnableArchitecture(HookArchitecture.X86, cancellationToken));
        }

        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
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
        foreach (var client in _clients.Values.OfType<IDisposable>())
        {
            client.Dispose();
        }

        Process[] hostProcesses;
        lock (_hostProcessGate)
        {
            hostProcesses = _hostProcesses.Values.Distinct().ToArray();
            _hostProcesses.Clear();
        }

        foreach (var process in hostProcesses)
        {
            TerminateAndDispose(process);
        }
    }

    private HookArchitectureStatus EnableArchitecture(
        HookArchitecture architecture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var paths = _hostPaths!.ForArchitecture(architecture);
        var snapshot = paths.Snapshot();
        var architectureName = architecture.ToFolderName();

        if (TryGetRunningTrackedProcess(architecture, out _))
        {
            return new HookArchitectureStatus(
                architecture,
                true,
                true,
                snapshot.HookDllExists,
                $"{architectureName} hook host is already running.");
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

            lock (_hostProcessGate)
            {
                _hostProcesses[architecture] = process;
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

            if (IsProcessRunning(process))
            {
                return true;
            }

            _hostProcesses.Remove(architecture);
        }

        TerminateAndDispose(process);
        process = null;
        return false;
    }

    private static bool IsProcessRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static void TerminateAndDispose(Process process)
    {
        try
        {
            if (IsProcessRunning(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
        finally
        {
            process.Dispose();
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
