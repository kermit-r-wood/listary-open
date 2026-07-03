using ListaryOpen.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void MutexNameIsStable()
    {
        Assert.Equal(@"Local\ListaryOpen.SingleInstance", SingleInstanceGuard.MutexName);
    }

    [Fact]
    public void TryAcquireReportsOwnershipForFirstInstance()
    {
        var mutex = new ControllableSingleInstanceMutex(waitResult: true);

        using var guard = SingleInstanceGuard.TryAcquire(() => mutex);

        Assert.True(guard.IsOwner);
        Assert.Equal(TimeSpan.Zero, mutex.WaitTimeout);
    }

    [Fact]
    public void TryAcquireReportsAlreadyRunningForSecondInstance()
    {
        var state = new SharedMutexState();

        using var first = SingleInstanceGuard.TryAcquire(() => new SharedSingleInstanceMutex(state));
        using var second = SingleInstanceGuard.TryAcquire(() => new SharedSingleInstanceMutex(state));

        Assert.True(first.IsOwner);
        Assert.False(second.IsOwner);
    }

    [Fact]
    public void TryAcquireTreatsAbandonedMutexAsOwnership()
    {
        var mutex = new AbandonedSingleInstanceMutex();

        using var guard = SingleInstanceGuard.TryAcquire(() => mutex);

        Assert.True(guard.IsOwner);
    }

    [Fact]
    public void DisposeReleasesOwnedMutex()
    {
        var mutex = new ControllableSingleInstanceMutex(waitResult: true);
        var guard = SingleInstanceGuard.TryAcquire(() => mutex);

        guard.Dispose();

        Assert.Equal(1, mutex.ReleaseCount);
        Assert.True(mutex.Disposed);
    }

    [Fact]
    public void DisposeDoesNotReleaseUnownedMutex()
    {
        var mutex = new ControllableSingleInstanceMutex(waitResult: false);
        var guard = SingleInstanceGuard.TryAcquire(() => mutex);

        guard.Dispose();

        Assert.Equal(0, mutex.ReleaseCount);
        Assert.True(mutex.Disposed);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var mutex = new ControllableSingleInstanceMutex(waitResult: true);
        var guard = SingleInstanceGuard.TryAcquire(() => mutex);

        guard.Dispose();
        guard.Dispose();

        Assert.Equal(1, mutex.ReleaseCount);
        Assert.True(mutex.Disposed);
    }

    private sealed class ControllableSingleInstanceMutex(bool waitResult) : ISingleInstanceMutex
    {
        public bool Disposed { get; private set; }
        public int ReleaseCount { get; private set; }
        public TimeSpan? WaitTimeout { get; private set; }

        public bool WaitOne(TimeSpan timeout)
        {
            WaitTimeout = timeout;
            return waitResult;
        }

        public void ReleaseMutex()
        {
            ReleaseCount++;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class AbandonedSingleInstanceMutex : ISingleInstanceMutex
    {
        public bool WaitOne(TimeSpan timeout)
        {
            throw new AbandonedMutexException();
        }

        public void ReleaseMutex()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class SharedMutexState
    {
        public bool IsOwned { get; set; }
    }

    private sealed class SharedSingleInstanceMutex(SharedMutexState state) : ISingleInstanceMutex
    {
        private bool _isOwner;

        public bool WaitOne(TimeSpan timeout)
        {
            if (state.IsOwned)
            {
                return false;
            }

            state.IsOwned = true;
            _isOwner = true;
            return true;
        }

        public void ReleaseMutex()
        {
            if (_isOwner)
            {
                state.IsOwned = false;
                _isOwner = false;
            }
        }

        public void Dispose()
        {
        }
    }
}
