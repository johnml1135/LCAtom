using System.Collections.Generic;

namespace SIL.Motif.Worker.Projects;

/// <summary>Admits shared project work while giving waiting exclusive work priority.</summary>
public sealed class ProjectOperationGate : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<Waiter> _shared = new();
    private readonly Queue<Waiter> _exclusive = new();
    private int _activeShared;
    private int _waitingExclusive;
    private bool _exclusiveActive;
    private bool _disposed;

    /// <summary>Acquires a shared operation lease.</summary>
    public Task<IDisposable> AcquireOperationAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(exclusive: false, cancellationToken);

    /// <summary>Acquires an exclusive operation lease.</summary>
    public Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(exclusive: true, cancellationToken);

    /// <summary>Releases queued waiters and prevents new work from entering the gate.</summary>
    public void Dispose()
    {
        List<Waiter> waiters;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            waiters = [.. _shared, .. _exclusive];
            _shared.Clear();
            _exclusive.Clear();
            _waitingExclusive = 0;
        }

        foreach (var waiter in waiters)
        {
            waiter.Completed = true;
            waiter.Completion.TrySetException(new ObjectDisposedException(nameof(ProjectOperationGate)));
        }
    }

    private Task<IDisposable> AcquireAsync(bool exclusive, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<IDisposable>(cancellationToken);

        Waiter? waiter = null;
        lock (_sync)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ProjectOperationGate));
            if (CanEnterImmediately(exclusive))
                return Task.FromResult<IDisposable>(Enter(exclusive));

            waiter = new Waiter(exclusive);
            if (exclusive)
            {
                _waitingExclusive++;
                _exclusive.Enqueue(waiter);
            }
            else
            {
                _shared.Enqueue(waiter);
            }
        }

        waiter.RegisterCancellation(cancellationToken, CancelWaiter);
        return waiter.Completion.Task;
    }

    private bool CanEnterImmediately(bool exclusive) =>
        exclusive
            ? !_exclusiveActive && _activeShared == 0
            : !_exclusiveActive && _waitingExclusive == 0 && _exclusive.Count == 0;

    private IDisposable Enter(bool exclusive)
    {
        if (exclusive) _exclusiveActive = true;
        else _activeShared++;
        return new Lease(this, exclusive);
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Completed) return;
            waiter.Cancelled = true;
            if (waiter.Exclusive) _waitingExclusive--;
            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            GrantWaiters();
        }
    }

    private void Release(bool exclusive)
    {
        lock (_sync)
        {
            if (exclusive) _exclusiveActive = false;
            else _activeShared--;
            GrantWaiters();
        }
    }

    private void GrantWaiters()
    {
        if (_exclusiveActive || _activeShared != 0) return;
        while (_exclusive.Count != 0 && _exclusive.Peek().Cancelled)
        {
            _exclusive.Dequeue();
        }
        if (_exclusive.Count != 0)
        {
            var waiter = _exclusive.Dequeue();
            _waitingExclusive--;
            waiter.Completed = true;
            waiter.Completion.TrySetResult(Enter(exclusive: true));
            return;
        }

        while (_shared.Count != 0)
        {
            var waiter = _shared.Dequeue();
            if (waiter.Cancelled) continue;
            waiter.Completed = true;
            waiter.Completion.TrySetResult(Enter(exclusive: false));
        }
    }

    private sealed class Waiter
    {
        private CancellationTokenRegistration _registration;

        public Waiter(bool exclusive)
        {
            Exclusive = exclusive;
            Completion = new TaskCompletionSource<IDisposable>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool Exclusive { get; }
        public bool Cancelled { get; set; }
        public bool Completed { get; set; }
        public CancellationToken CancellationToken { get; private set; }
        public TaskCompletionSource<IDisposable> Completion { get; }

        public void RegisterCancellation(CancellationToken token, Action<Waiter> callback)
        {
            CancellationToken = token;
            _registration = token.Register(static state =>
            {
                var tuple = ((Waiter Waiter, Action<Waiter> Callback))state!;
                tuple.Callback(tuple.Waiter);
            }, (this, callback));
            if (Completion.Task.IsCompleted) _registration.Dispose();
        }
    }

    private sealed class Lease : IDisposable
    {
        private ProjectOperationGate? _owner;
        private readonly bool _exclusive;

        public Lease(ProjectOperationGate owner, bool exclusive)
        {
            _owner = owner;
            _exclusive = exclusive;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_exclusive);
    }
}
