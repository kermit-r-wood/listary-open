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

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("REPORT.DOCX")]
    [InlineData("slides.pptx")]
    [InlineData("sheet.xlsx")]
    [InlineData("music.opus")]
    [InlineData("camera.cr3")]
    [InlineData("design.psd")]
    [InlineData("archive.zip")]
    public void RichAndCodecDependentFormatsPreferWindowsPreview(string fileName)
    {
        var context = new PreviewContext(
            Path.Combine(@"C:\Preview", fileName),
            fileName,
            Path.GetExtension(fileName),
            1,
            DateTimeOffset.UtcNow);

        Assert.True(PreviewCoordinator.ShouldPreferSystemPreview(context));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("source.cs")]
    [InlineData("settings.json")]
    [InlineData("vector.svg")]
    [InlineData("photo.png")]
    public void DeterministicBuiltInFormatsDoNotRequireWindowsPreview(string fileName)
    {
        var context = new PreviewContext(
            Path.Combine(@"C:\Preview", fileName),
            fileName,
            Path.GetExtension(fileName),
            1,
            DateTimeOffset.UtcNow);

        Assert.False(PreviewCoordinator.ShouldPreferSystemPreview(context));
    }
}
