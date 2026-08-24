using SIL.Motif.Contract.Baselines;

namespace SIL.Motif.Worker.Scheduling;

/// <summary>Serializes Baseline-dependent work while allowing Apply beside already isolated Dry Runs.</summary>
public sealed class ProjectLane : IDisposable
{
    // A refresh holds back for waiting Apply work, so it polls rather than spinning a thread pool slot.
    private static readonly TimeSpan ApplyHoldBackInterval = TimeSpan.FromMilliseconds(2);
    private readonly object _gate = new();
    private readonly LinkedList<QueuedWork> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _liveGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _runner;
    private BaselineToken _baseline;
    private bool _barrierClosed;
    private int _applyWaiters;
    private bool _disposed;

    public ProjectLane(BaselineToken baseline)
    {
        _baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        _runner = RunAsync();
    }

    public Task<ProjectWorkResult> EnqueueAsync(ProjectWorkItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var completion = new TaskCompletionSource<ProjectWorkResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new QueuedWork(item, null, cancellationToken, completion);
        lock (_gate)
        {
            ThrowIfDisposed();
            work.Node = _queue.AddLast(work);
            RegisterCancellation(work);
        }
        _signal.Release();
        return completion.Task;
    }

    public Task<ProjectWorkResult> EnqueueAgainstBaselineAsync(ProjectWorkItem item,
        BaselineToken baseline, bool acceptKnownOld, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(baseline);
        if (item.Kind == ProjectWorkKind.Refresh)
            throw new ArgumentException("A refresh cannot be resubmitted against an old Baseline.", nameof(item));
        if (!acceptKnownOld)
            throw new InvalidOperationException("Explicit acceptance of the known-old Baseline is required.");
        var completion = new TaskCompletionSource<ProjectWorkResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new QueuedWork(item, baseline, cancellationToken, completion);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_barrierClosed || !Equals(_baseline, baseline))
                throw new InvalidOperationException("The named Baseline is not the lane's blocked Baseline.");
            work.Node = _queue.AddLast(work);
            RegisterCancellation(work);
        }
        _signal.Release();
        return completion.Task;
    }

    public async Task<IDisposable?> TryAcquireApplyGateAsync(TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate) ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        Interlocked.Increment(ref _applyWaiters);
        try
        {
            if (!await _liveGate.WaitAsync(timeout, linked.Token).ConfigureAwait(false)) return null;
            return new GateLease(_liveGate);
        }
        finally
        {
            Interlocked.Decrement(ref _applyWaiters);
        }
    }

    public void Dispose()
    {
        QueuedWork[] queued;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            queued = _queue.ToArray();
            _queue.Clear();
        }
        _shutdown.Cancel();
        _signal.Release();
        foreach (var work in queued)
        {
            work.Registration.Dispose();
            work.Completion.TrySetException(new ObjectDisposedException(nameof(ProjectLane)));
        }
        try { _runner.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
        _signal.Dispose();
        _liveGate.Dispose();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            QueuedWork? work;
            lock (_gate)
            {
                if (_disposed) return;
                var node = NextRunnableNode();
                work = node?.Value;
                if (node is not null) _queue.Remove(node);
            }
            if (work is null) continue;
            work.Registration.Dispose();
            await ExecuteAsync(work).ConfigureAwait(false);
            lock (_gate)
                if (_queue.Count != 0) _signal.Release();
        }
    }

    private LinkedListNode<QueuedWork>? NextRunnableNode()
    {
        if (!_barrierClosed) return _queue.First;
        var node = _queue.First;
        while (node is not null && node.Value.Item.Kind != ProjectWorkKind.Refresh &&
               node.Value.ExplicitBaseline is null) node = node.Next;
        return node;
    }

    private async Task ExecuteAsync(QueuedWork work)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            work.CancellationToken, _shutdown.Token);
        var cancellationToken = linked.Token;
        if (cancellationToken.IsCancellationRequested)
        {
            work.Completion.TrySetCanceled(cancellationToken);
            return;
        }
        try
        {
            BaselineToken outcome;
            if (work.Item.Kind == ProjectWorkKind.Refresh)
            {
                while (Volatile.Read(ref _applyWaiters) != 0)
                    await Task.Delay(ApplyHoldBackInterval, cancellationToken).ConfigureAwait(false);
                await _liveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    CloseBarrier();
                    var replacement = await work.Item.RefreshWork!(cancellationToken).ConfigureAwait(false);
                    outcome = replacement ?? throw new InvalidDataException("A refresh returned no Baseline.");
                    OpenBarrier(outcome);
                }
                finally { _liveGate.Release(); }
            }
            else
            {
                outcome = work.ExplicitBaseline ?? CurrentBaseline();
                await work.Item.BaselineWork!(outcome, cancellationToken).ConfigureAwait(false);
            }
            work.Completion.TrySetResult(new ProjectWorkResult(work.Item.Kind, outcome));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (work.Item.Kind == ProjectWorkKind.Refresh) CloseBarrier();
            work.Completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            if (work.Item.Kind == ProjectWorkKind.Refresh) CloseBarrier();
            work.Completion.TrySetException(exception);
        }
    }

    private void CloseBarrier()
    {
        lock (_gate) _barrierClosed = true;
    }

    private void OpenBarrier(BaselineToken replacement)
    {
        lock (_gate)
        {
            _baseline = replacement;
            _barrierClosed = false;
        }
    }

    private BaselineToken CurrentBaseline()
    {
        lock (_gate) return _baseline;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProjectLane));
    }

    // Lets a queued item cancel promptly instead of waiting for a turn that may never come.
    private void RegisterCancellation(QueuedWork work)
    {
        if (!work.CancellationToken.CanBeCanceled) return;
        work.Registration = work.CancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (work.Node is { List: not null } node) _queue.Remove(node);
            }
            work.Completion.TrySetCanceled(work.CancellationToken);
        });
    }

    private sealed record QueuedWork(ProjectWorkItem Item, BaselineToken? ExplicitBaseline,
        CancellationToken CancellationToken, TaskCompletionSource<ProjectWorkResult> Completion)
    {
        public LinkedListNode<QueuedWork>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class GateLease : IDisposable
    {
        private SemaphoreSlim? _gate;
        public GateLease(SemaphoreSlim gate) => _gate = gate;
        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is null) return;
            try { gate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }
}
