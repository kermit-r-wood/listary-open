using ListaryOpen.Infrastructure.Windows;

namespace ListaryOpen.Infrastructure.Tests.Windows;

public sealed class TaskManagerAutomationServiceTests
{
    [Fact]
    public void PreviewSelectionReachesTheStaProviderWithoutActivatingTaskManager()
    {
        using var provider = new RecordingTaskManagerProvider();
        using var service = new TaskManagerAutomationService(provider, TimeSpan.Zero);
        var item = new TaskManagerItem("row-preview", "Firefox", string.Empty);

        service.QueueSelectItem(new IntPtr(42), item, activate: false);

        Assert.True(provider.SelectionReceived.Wait(TimeSpan.FromSeconds(2)));
        Assert.Same(item, provider.Item);
        Assert.False(provider.Activate);
        Assert.Equal(ApartmentState.STA, provider.ApartmentState);
    }

    [Fact]
    public void ConfirmedSelectionReachesTheStaProviderWithActivationPreserved()
    {
        using var provider = new RecordingTaskManagerProvider();
        using var service = new TaskManagerAutomationService(provider, TimeSpan.Zero);
        var item = new TaskManagerItem("row-1", "Process: 1Password", string.Empty);

        service.QueueSelectItem(new IntPtr(42), item, activate: true);

        Assert.True(provider.SelectionReceived.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(new IntPtr(42), provider.Window);
        Assert.Same(item, provider.Item);
        Assert.True(provider.Activate);
        Assert.Equal(ApartmentState.STA, provider.ApartmentState);
    }

    [Fact]
    public void TransientTaskManagerSelectionFailureIsRetried()
    {
        using var provider = new RecordingTaskManagerProvider(failuresBeforeSuccess: 2);
        using var service = new TaskManagerAutomationService(provider, TimeSpan.Zero);
        var item = new TaskManagerItem("row-transient", "Windows Explorer", string.Empty);

        service.QueueSelectItem(new IntPtr(42), item, activate: false);

        Assert.True(provider.SelectionReceived.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(3, provider.AttemptCount);
        Assert.Same(item, provider.Item);
    }

    [Fact]
    public void ConfirmedSelectionSurvivesTaskManagerRerenderingAfterActivation()
    {
        using var provider = new RecordingTaskManagerProvider(failuresBeforeSuccess: 5);
        using var service = new TaskManagerAutomationService(provider, TimeSpan.Zero);
        var item = new TaskManagerItem("row-confirmed", "Windows Explorer", string.Empty);

        service.QueueSelectItem(new IntPtr(42), item, activate: true);

        Assert.True(provider.SelectionReceived.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(6, provider.AttemptCount);
        Assert.True(provider.Activate);
        Assert.Same(item, provider.Item);
    }

    private sealed class RecordingTaskManagerProvider(int failuresBeforeSuccess = 0) : ITaskManagerAutomationProvider, IDisposable
    {
        public ManualResetEventSlim SelectionReceived { get; } = new();

        public IntPtr Window { get; private set; }

        public TaskManagerItem? Item { get; private set; }

        public bool Activate { get; private set; }

        public ApartmentState ApartmentState { get; private set; }

        public int AttemptCount { get; private set; }

        public IReadOnlyList<TaskManagerItem> GetItems(IntPtr taskManagerWindow) => [];

        public bool TrySelectItem(IntPtr taskManagerWindow, TaskManagerItem item, bool activate)
        {
            AttemptCount++;
            Window = taskManagerWindow;
            Item = item;
            Activate = activate;
            ApartmentState = Thread.CurrentThread.GetApartmentState();
            var succeeded = AttemptCount > failuresBeforeSuccess;
            if (succeeded)
            {
                SelectionReceived.Set();
            }
            return succeeded;
        }

        public void Dispose() => SelectionReceived.Dispose();
    }
}
