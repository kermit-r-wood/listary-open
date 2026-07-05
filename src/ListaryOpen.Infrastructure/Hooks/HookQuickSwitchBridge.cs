namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookQuickSwitchBridge : IHookQuickSwitchBridge
{
    private readonly IReadOnlyDictionary<HookArchitecture, IHookIpcClient> _clients;

    public HookQuickSwitchBridge(
        HookQuickSwitchStatus initialStatus,
        IReadOnlyDictionary<HookArchitecture, IHookIpcClient> clients)
    {
        Status = initialStatus;
        _clients = clients;
    }

    public HookQuickSwitchStatus Status { get; private set; }

    public event EventHandler<HookQuickSwitchStatus>? StatusChanged;

    public Task EnableAsync(CancellationToken cancellationToken)
    {
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
    }
}
