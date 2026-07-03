using System.Windows.Threading;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppDispatcherStartupTests
{
    [Fact]
    public async Task InvokeOnDispatcherAsyncRunsActionOnDispatcherThreadWhenCalledFromWorkerThread()
    {
        using var dispatcherThread = await TestDispatcherThread.StartAsync();

        var actionThreadId = await Task.Run(() =>
            ListaryOpen.App.App.InvokeOnDispatcherAsync(
                dispatcherThread.Dispatcher,
                () => Environment.CurrentManagedThreadId));

        Assert.Equal(dispatcherThread.ManagedThreadId, actionThreadId);
    }

    private sealed class TestDispatcherThread : IDisposable
    {
        private readonly Thread _thread;
        private bool _disposed;

        private TestDispatcherThread(Thread thread, Dispatcher dispatcher, int managedThreadId)
        {
            _thread = thread;
            Dispatcher = dispatcher;
            ManagedThreadId = managedThreadId;
        }

        public Dispatcher Dispatcher { get; }

        public int ManagedThreadId { get; }

        public static async Task<TestDispatcherThread> StartAsync()
        {
            var ready = new TaskCompletionSource<TestDispatcherThread>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                ready.SetResult(new TestDispatcherThread(Thread.CurrentThread, dispatcher, Environment.CurrentManagedThreadId));
                Dispatcher.Run();
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
