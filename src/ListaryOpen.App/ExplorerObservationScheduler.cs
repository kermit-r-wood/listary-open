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
    private IExplorerObservationTimer? _timer;

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
        if (_timer is not null)
        {
            return;
        }

        _timer = _timerFactory();
        _timer.Tick += OnTick;
        _timer.Start(_interval);
    }

    public void Dispose()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Tick -= OnTick;
        _timer.Stop();
        _timer.Dispose();
        _timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            _observe();
        }
        catch (Exception exception)
        {
            Trace.TraceError(exception.ToString());
        }
    }
}
