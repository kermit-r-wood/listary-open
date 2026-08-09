using System.Runtime.InteropServices;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class ExplorerNavigationServiceTests
{
    [Fact]
    public async Task ExistingFolderIsNavigatedInRequestedWindowOnStaThread()
    {
        using var folder = TemporaryDirectory.Create();
        var provider = new RecordingNavigationProvider();
        var service = new ExplorerNavigationService(provider);

        var navigated = await service.NavigateToFolderAsync(new IntPtr(42), folder.Path);

        Assert.True(navigated);
        Assert.Equal(new IntPtr(42), provider.ExplorerWindow);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path)),
            provider.FolderPath,
            ignoreCase: true);
        Assert.Equal(ApartmentState.STA, provider.ApartmentState);
    }

    [Fact]
    public async Task InvalidWindowOrMissingFolderIsNotSentToExplorer()
    {
        var provider = new RecordingNavigationProvider();
        var service = new ExplorerNavigationService(provider);

        Assert.False(await service.NavigateToFolderAsync(IntPtr.Zero, "C:\\Projects"));
        Assert.False(await service.NavigateToFolderAsync(
            new IntPtr(42),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ExpectedShellFailureReturnsFalse()
    {
        using var folder = TemporaryDirectory.Create();
        var service = new ExplorerNavigationService(
            new ThrowingNavigationProvider(new COMException("Explorer unavailable.")));

        Assert.False(await service.NavigateToFolderAsync(new IntPtr(42), folder.Path));
    }

    [Fact]
    public void TryFocusFolderViewRejectsInvalidWindowsWithoutThrowing()
    {
        var service = new ExplorerNavigationService(new RecordingNavigationProvider());
        Assert.False(service.TryFocusFolderView(IntPtr.Zero));
        Assert.False(service.TryFocusFolderView(new IntPtr(1)));
    }

    private sealed class RecordingNavigationProvider : IExplorerShellNavigationProvider
    {
        public int CallCount { get; private set; }
        public IntPtr ExplorerWindow { get; private set; }
        public string? FolderPath { get; private set; }
        public ApartmentState ApartmentState { get; private set; }

        public bool TryNavigateToFolder(IntPtr explorerWindow, string folderPath)
        {
            CallCount++;
            ExplorerWindow = explorerWindow;
            FolderPath = folderPath;
            ApartmentState = Thread.CurrentThread.GetApartmentState();
            return true;
        }
    }

    private sealed class ThrowingNavigationProvider(Exception exception) : IExplorerShellNavigationProvider
    {
        public bool TryNavigateToFolder(IntPtr explorerWindow, string folderPath) => throw exception;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "listary-open-explorer-navigation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
