namespace ListaryOpen.App;

internal interface ISingleInstanceMutex : IDisposable
{
    bool WaitOne(TimeSpan timeout);

    void ReleaseMutex();
}

internal sealed class SingleInstanceGuard : IDisposable
{
    internal const string MutexName = @"Local\ListaryOpen.SingleInstance";

    private readonly ISingleInstanceMutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(ISingleInstanceMutex mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    public bool IsOwner { get; private set; }

    public static SingleInstanceGuard TryAcquire()
    {
        return TryAcquire(() => new SystemSingleInstanceMutex(MutexName));
    }

    internal static SingleInstanceGuard TryAcquire(Func<ISingleInstanceMutex> mutexFactory)
    {
        ArgumentNullException.ThrowIfNull(mutexFactory);

        var mutex = mutexFactory();
        var isOwner = TryWait(mutex);
        return new SingleInstanceGuard(mutex, isOwner);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (IsOwner)
            {
                _mutex.ReleaseMutex();
                IsOwner = false;
            }
        }
        finally
        {
            _mutex.Dispose();
            _disposed = true;
        }
    }

    private static bool TryWait(ISingleInstanceMutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private sealed class SystemSingleInstanceMutex : ISingleInstanceMutex
    {
        private readonly Mutex _mutex;

        public SystemSingleInstanceMutex(string name)
        {
            _mutex = new Mutex(initiallyOwned: false, name);
        }

        public bool WaitOne(TimeSpan timeout)
        {
            return _mutex.WaitOne(timeout);
        }

        public void ReleaseMutex()
        {
            _mutex.ReleaseMutex();
        }

        public void Dispose()
        {
            _mutex.Dispose();
        }
    }
}
