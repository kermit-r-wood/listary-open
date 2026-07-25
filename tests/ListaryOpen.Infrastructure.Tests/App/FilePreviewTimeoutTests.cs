using ListaryOpen.App;
using ListaryOpen.App.Previewing;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class FilePreviewTimeoutTests
{
    [Fact]
    public void PreviewTimeoutIsFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), FilePreviewPane.PreviewTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), PreviewCoordinator.DefaultLoadTimeout);
    }

    [Fact]
    public async Task WaitForSelectionRespectsCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PreviewCoordinator.WaitForSelectionAsync(cts.Token));
    }

    [Fact]
    public async Task LoadAsyncHonorsAlreadyCancelledToken()
    {
        var coordinator = new PreviewCoordinator(Array.Empty<IFilePreviewProvider>());
        var context = new PreviewContext(
            @"C:\Windows\notepad.exe",
            "notepad.exe",
            ".exe",
            1,
            DateTimeOffset.UtcNow);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.LoadAsync(context, cts.Token));
    }
}
