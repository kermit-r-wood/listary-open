using System.Diagnostics;
using System.Windows.Threading;

namespace ListaryOpen.App;

internal interface IExplorerObservationTimer : IDisposable
{
    event EventHandler? Tick;

    void Start(TimeSpan interval);

    void Stop();
}

internal sealed class DispatcherExplorerObservationTimer : IExplorerObservationTimer
{
    private readonly DispatcherTimer _timer;

    public DispatcherExplorerObservationTimer()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += OnTick;
    }

    public event EventHandler? Tick;

    public void Start(TimeSpan interval)
    {
        _timer.Interval = interval;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void Dispose()
    {
        _timer.Tick -= OnTick;
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Tick?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class ExplorerObservationScheduler : IDisposable
{
    private readonly Action _observe;
    private readonly TimeSpan _interval;
    private readonly Func<IExplorerObservationTimer> _timerFactory;
    private readonly AutoResetEvent _observationSignal = new(initialState: false);
    private readonly object _waiterGate = new();
    private readonly List<TaskCompletionSource> _observationWaiters = new();
    private IExplorerObservationTimer? _timer;
    private Thread? _observationThread;
    private int _observationPending;
    private int _disposed;

    public ExplorerObservationScheduler(
        Action observe,
        TimeSpan interval,
        Func<IExplorerObservationTimer> timerFactory)
    {
        _observe = observe ?? throw new ArgumentNullException(nameof(observe));
        _timerFactory = timerFactory ?? throw new ArgumentNullException(nameof(timerFactory));

        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Observation interval must be positive.");
        }

        _interval = interval;
    }

    public void Start()
    {
        if (_timer is not null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _observationThread = new Thread(ProcessObservations)
        {
            IsBackground = true,
            Name = "ListaryOpen Explorer observation"
        };
        _observationThread.SetApartmentState(ApartmentState.STA);
        _observationThread.Start();
        _timer = _timerFactory();
        _timer.Tick += OnTick;
        _timer.Start(_interval);
        RequestObservation();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer.Dispose();
            _timer = null;
        }

        _observationSignal.Set();
        if (_observationThread is null ||
            _observationThread.Join(TimeSpan.FromSeconds(2)))
        {
            _observationSignal.Dispose();
        }

        TaskCompletionSource[] waiters;
        lock (_waiterGate)
        {
            waiters = _observationWaiters.ToArray();
            _observationWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetCanceled();
        }
    }

    public Task RequestObservationAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return Task.FromCanceled(new CancellationToken(canceled: true));
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiterGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                completion.TrySetCanceled();
                return completion.Task;
            }

            _observationWaiters.Add(completion);
        }

        RequestObservation();
        return completion.Task;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        RequestObservation();
    }

    private void RequestObservation()
    {
        if (Volatile.Read(ref _disposed) == 0 &&
            Interlocked.Exchange(ref _observationPending, 1) == 0)
        {
            _observationSignal.Set();
        }
    }

    private void ProcessObservations()
    {
        while (true)
        {
            _observationSignal.WaitOne();
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _observationPending, 0);
            TaskCompletionSource[] waiters;
            lock (_waiterGate)
            {
                waiters = _observationWaiters.ToArray();
                _observationWaiters.Clear();
            }

            try
            {
                _observe();
                foreach (var waiter in waiters)
                {
                    waiter.TrySetResult();
                }
            }
            catch (Exception exception)
            {
                Trace.TraceError(exception.ToString());
                foreach (var waiter in waiters)
                {
                    waiter.TrySetException(exception);
                }
            }
        }
    }
}
