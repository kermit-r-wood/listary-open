using System.Runtime.InteropServices;
using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class ExplorerSelectionServiceTests
{
    [Fact]
    public void DirectChildIsSelectedInRequestedExplorerWindow()
    {
        var provider = new RecordingSelectionProvider();
        var service = new ExplorerSelectionService(provider);

        var selected = service.TrySelectItem(
            new IntPtr(42),
            "C:\\Projects\\",
            "c:\\projects\\Report.txt");

        Assert.True(selected);
        Assert.Equal(new IntPtr(42), provider.ExplorerWindow);
        Assert.Equal("C:\\Projects", provider.CurrentFolder, ignoreCase: true);
        Assert.Equal("Report.txt", provider.ChildName);
    }

    [Theory]
    [InlineData("C:\\Projects", "C:\\Projects\\Nested\\Report.txt")]
    [InlineData("C:\\Projects", "C:\\Elsewhere\\Report.txt")]
    [InlineData("", "C:\\Projects\\Report.txt")]
    public void ItemOutsideCurrentFolderIsNotSentToExplorer(string currentFolder, string itemPath)
    {
        var provider = new RecordingSelectionProvider();
        var service = new ExplorerSelectionService(provider);

        Assert.False(service.TrySelectItem(new IntPtr(42), currentFolder, itemPath));
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void ExpectedShellFailureDoesNotEscapeSelectionSync()
    {
        var service = new ExplorerSelectionService(
            new ThrowingSelectionProvider(new COMException("Explorer unavailable.")));

        Assert.False(service.TrySelectItem(
            new IntPtr(42),
            "C:\\Projects",
            "C:\\Projects\\Report.txt"));
    }

    [Fact]
    public void QueuedSelectionIsLatestWinsDebouncedAndRunsOnStaWorker()
    {
        var provider = new ConcurrentSelectionProvider();
        using var service = new ExplorerSelectionService(provider, TimeSpan.FromMilliseconds(50));

        service.QueueSelectItem(new IntPtr(42), "C:\\Projects", "C:\\Projects\\One.txt");
        service.QueueSelectItem(new IntPtr(42), "C:\\Projects", "C:\\Projects\\Two.txt");
        service.QueueSelectItem(new IntPtr(42), "C:\\Projects", "C:\\Projects\\Three.txt");

        Assert.True(provider.Selected.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("Three.txt", provider.ChildName);
        Assert.Equal(ApartmentState.STA, provider.ApartmentState);
    }

    [Fact]
    public async Task CancelPendingPreventsSelectionAfterOverlayDismisses()
    {
        var provider = new ConcurrentSelectionProvider();
        using var service = new ExplorerSelectionService(provider, TimeSpan.FromMilliseconds(100));

        service.QueueSelectItem(new IntPtr(42), "C:\\Projects", "C:\\Projects\\Stale.txt");
        service.CancelPending();
        await Task.Delay(200);

        Assert.Equal(0, provider.CallCount);
    }

    private sealed class RecordingSelectionProvider : IExplorerShellSelectionProvider
    {
        public int CallCount { get; private set; }

        public IntPtr ExplorerWindow { get; private set; }

        public string? CurrentFolder { get; private set; }

        public string? ChildName { get; private set; }

        public bool TrySelectItem(IntPtr explorerWindow, string currentFolder, string childName)
        {
            CallCount++;
            ExplorerWindow = explorerWindow;
            CurrentFolder = currentFolder;
            ChildName = childName;
            return true;
        }
    }

    private sealed class ThrowingSelectionProvider(Exception exception) : IExplorerShellSelectionProvider
    {
        public bool TrySelectItem(IntPtr explorerWindow, string currentFolder, string childName) =>
            throw exception;
    }

    private sealed class ConcurrentSelectionProvider : IExplorerShellSelectionProvider
    {
        private int _callCount;

        public ManualResetEventSlim Selected { get; } = new();

        public int CallCount => Volatile.Read(ref _callCount);

        public string? ChildName { get; private set; }

        public ApartmentState ApartmentState { get; private set; }

        public bool TrySelectItem(IntPtr explorerWindow, string currentFolder, string childName)
        {
            ChildName = childName;
            ApartmentState = Thread.CurrentThread.GetApartmentState();
            Interlocked.Increment(ref _callCount);
            Selected.Set();
            return true;
        }
    }
}
