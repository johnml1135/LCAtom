using System;
using System.Threading;

namespace SIL.Motif.Worker;

/// <summary>Owns the live leases that keep a worker process alive.</summary>
public sealed class WorkerWorkTracker : IWorkerWorkTracker, IDisposable
{
    private int _leases;
    private int _disposed;

    /// <summary>Whether at least one queued, running, or waiting lease is active.</summary>
    public bool HasQueuedRunningOrWaitingWork => Volatile.Read(ref _leases) != 0;

    /// <summary>Acquires one lease until the returned disposable is released.</summary>
    public IDisposable AcquireLease()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WorkerWorkTracker));
        Interlocked.Increment(ref _leases);
        return new Lease(this);
    }

    /// <summary>Releases all tracker resources after leases have been disposed.</summary>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private void Release()
    {
        if (Interlocked.Decrement(ref _leases) < 0)
            throw new InvalidOperationException("A worker lease was released more than once.");
    }

    private sealed class Lease : IDisposable
    {
        private WorkerWorkTracker? _owner;

        public Lease(WorkerWorkTracker owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
