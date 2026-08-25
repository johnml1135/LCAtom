using System.Collections.Generic;

namespace SIL.Motif.Worker.Projects;

/// <summary>Admits shared project work while giving waiting exclusive work priority.</summary>
public sealed class ProjectOperationGate : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<Waiter> _shared = new();
    private readonly Queue<Waiter> _exclusive = new();
    private readonly TaskCompletionSource<bool> _quiesced =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeShared;
    private int _waitingExclusive;
    private bool _exclusiveActive;
    private bool _disposed;

    /// <summary>Acquires a shared operation lease.</summary>
    public Task<IDisposable> AcquireOperationAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(false, cancellationToken);

    /// <summary>Acquires an exclusive operation lease.</summary>
    public Task<IDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken = default) =>
        AcquireAsync(true, cancellationToken);

    /// <summary>Tries to acquire a shared operation lease immediately, without waiting or queuing.</summary>
    public bool TryAcquireOperation(out IDisposable lease)
    {
        lock (_sync)
        {
            if (_disposed || !CanEnterImmediately(false))
            {
                lease = null!;
                return false;
            }
            lease = Enter(false);
            return true;
        }
    }

    /// <summary>Stops admission, faults waiters, and waits for granted leases to leave.</summary>
    public void Dispose()
    {
        List<CancellationTokenRegistration> registrations;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            registrations = CompleteQueuedWaitersLocked(new ObjectDisposedException(nameof(ProjectOperationGate)));
            CompleteQuiescedLocked();
        }
        DisposeRegistrations(registrations);
        _quiesced.Task.GetAwaiter().GetResult();
    }

    private Task<IDisposable> AcquireAsync(bool exclusive, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<IDisposable>(cancellationToken);
        Waiter waiter;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ProjectOperationGate));
            if (CanEnterImmediately(exclusive)) return Task.FromResult<IDisposable>(Enter(exclusive));
            waiter = new Waiter(exclusive, cancellationToken);
            if (exclusive)
            {
                _waitingExclusive++;
                _exclusive.Enqueue(waiter);
            }
            else _shared.Enqueue(waiter);
        }
        waiter.RegisterCancellation(this);
        return waiter.Completion.Task;
    }

    private bool CanEnterImmediately(bool exclusive) => exclusive
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
        List<CancellationTokenRegistration> registrations;
        lock (_sync)
        {
            if (waiter.Completed) return;
            waiter.Completed = true;
            waiter.Cancelled = true;
            if (waiter.Exclusive && _waitingExclusive > 0) _waitingExclusive--;
            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
            registrations = GrantWaitersLocked();
            registrations.Add(waiter.TakeRegistration());
        }
        DisposeRegistrations(registrations);
    }

    private void Release(bool exclusive)
    {
        List<CancellationTokenRegistration> registrations;
        lock (_sync)
        {
            if (exclusive) _exclusiveActive = false;
            else if (_activeShared > 0) _activeShared--;
            registrations = _disposed ? [] : GrantWaitersLocked();
            CompleteQuiescedLocked();
        }
        DisposeRegistrations(registrations);
    }

    private List<CancellationTokenRegistration> GrantWaitersLocked()
    {
        var registrations = new List<CancellationTokenRegistration>();
        if (_exclusiveActive || _activeShared != 0) return registrations;
        while (_exclusive.Count != 0)
        {
            var waiter = _exclusive.Dequeue();
            if (waiter.Cancelled || waiter.Completed)
            {
                registrations.Add(waiter.TakeRegistration());
                continue;
            }
            if (_waitingExclusive > 0) _waitingExclusive--;
            waiter.Completed = true;
            waiter.Completion.TrySetResult(Enter(true));
            registrations.Add(waiter.TakeRegistration());
            return registrations;
        }
        while (_shared.Count != 0)
        {
            var waiter = _shared.Dequeue();
            if (waiter.Cancelled || waiter.Completed)
            {
                registrations.Add(waiter.TakeRegistration());
                continue;
            }
            waiter.Completed = true;
            waiter.Completion.TrySetResult(Enter(false));
            registrations.Add(waiter.TakeRegistration());
        }
        return registrations;
    }

    private List<CancellationTokenRegistration> CompleteQueuedWaitersLocked(Exception exception)
    {
        var registrations = new List<CancellationTokenRegistration>();
        while (_shared.Count != 0)
        {
            var waiter = _shared.Dequeue();
            waiter.Completed = true;
            waiter.Completion.TrySetException(exception);
            registrations.Add(waiter.TakeRegistration());
        }
        while (_exclusive.Count != 0)
        {
            var waiter = _exclusive.Dequeue();
            waiter.Completed = true;
            waiter.Completion.TrySetException(exception);
            registrations.Add(waiter.TakeRegistration());
        }
        _waitingExclusive = 0;
        return registrations;
    }

    private void CompleteQuiescedLocked()
    {
        if (_disposed && _activeShared == 0 && !_exclusiveActive) _quiesced.TrySetResult(true);
    }

    private static void DisposeRegistrations(List<CancellationTokenRegistration> registrations)
    {
        foreach (var registration in registrations) registration.Dispose();
    }

    private sealed class Waiter
    {
        private CancellationTokenRegistration? _registration;

        public Waiter(bool exclusive, CancellationToken cancellationToken)
        {
            Exclusive = exclusive;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource<IDisposable>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool Exclusive { get; }
        public bool Cancelled { get; set; }
        public bool Completed { get; set; }
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource<IDisposable> Completion { get; }

        public void RegisterCancellation(ProjectOperationGate owner)
        {
            if (!CancellationToken.CanBeCanceled) return;
            var registration = CancellationToken.Register(static state =>
            {
                var value = ((ProjectOperationGate Owner, Waiter Waiter))state!;
                value.Owner.CancelWaiter(value.Waiter);
            }, (owner, this));
            var dispose = false;
            lock (owner._sync)
            {
                if (Completed) dispose = true;
                else _registration = registration;
            }
            if (dispose) registration.Dispose();
        }

        public CancellationTokenRegistration TakeRegistration()
        {
            var registration = _registration;
            _registration = null;
            return registration ?? default;
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
