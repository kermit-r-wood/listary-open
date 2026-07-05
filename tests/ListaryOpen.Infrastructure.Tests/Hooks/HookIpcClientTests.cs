using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookIpcClientTests
{
    [Fact]
    public async Task MissingPipeReturnsHostUnavailable()
    {
        var client = new HookIpcClient("listary-open-missing-" + Guid.NewGuid(), TimeSpan.FromMilliseconds(50));

        var result = await client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.HostUnavailable, result.Status);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var client = new HookIpcClient("listary-open-missing-" + Guid.NewGuid(), TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", cancellation.Token));
    }
}
