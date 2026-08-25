using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace SIL.Motif.Worker.PanGloss;

/// <summary>
/// Admits one user worker's PanGloss jobs in submission order, while every
/// <see cref="MachinePanGlossQueue"/> on the machine competes for the same two capacity slots.
/// </summary>
/// <remarks>
/// A job is admitted strictly in the order it was submitted to THIS queue: the queue does not start
/// acquiring a slot for job N+1 until job N has already acquired one, though N and N+1 may then run
/// concurrently (pinned by `RunAsync_AdmitsThreeProjectsInSubmissionOrder`). The two slots are
/// machine-global rather than per-user, so independent queues never hold more than two between them,
/// but which queue wins a given free slot is unspecified and never asserted (pinned by
/// `RunAsync_AcrossTwoUserNamespaces_NeverExceedsMachineCapacity`).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class MachinePanGlossQueue : IDisposable
{
    private static readonly string[] DefaultSlotNames =
    {
        "Global\\MotifPanGlossSlot-0",
        "Global\\MotifPanGlossSlot-1",
    };

    // Machine leases are OS mutexes, not events, so a short poll is how a freed slot is noticed.
    private static readonly TimeSpan SlotPollInterval = TimeSpan.FromMilliseconds(10);

    private readonly IReadOnlyList<string> _slotNames;
    private readonly ConcurrentDictionary<int, string> _slotOwnership = new();
    private readonly object _gate = new();
    private readonly LinkedList<QueuedJob> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _runner;
    private bool _disposed;

    /// <summary>Creates a queue that competes for the machine's two fixed, well-known PanGloss slots.</summary>
    public MachinePanGlossQueue() : this(DefaultSlotNames)
    {
    }

    /// <summary>Creates a queue against explicit slot names, so callers can isolate their own leases.</summary>
    internal MachinePanGlossQueue(IReadOnlyList<string> slotNames)
    {
        if (slotNames is null || slotNames.Count == 0)
            throw new ArgumentException("At least one machine slot name is required.", nameof(slotNames));
        _slotNames = slotNames;
        _runner = RunAsync();
    }

    /// <summary>The job id currently recorded against each held slot, for diagnosing contention.</summary>
    internal IReadOnlyDictionary<int, string> SlotOwnership => _slotOwnership;

    /// <summary>
    /// Queues <paramref name="work"/> under <paramref name="jobId"/> and returns its result once the
    /// job has been admitted to a machine slot and has run to completion.
    /// </summary>
    public Task<T> RunAsync<T>(string jobId, Func<WindowsCpuJob, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Required.", nameof(jobId));
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new QueuedJob<T>(jobId, work, cancellationToken, completion);
        lock (_gate)
        {
            ThrowIfDisposed();
            job.Node = _queue.AddLast(job);
            RegisterCancellation(job);
        }
        _signal.Release();
        return completion.Task;
    }

    public void Dispose()
    {
        QueuedJob[] queued;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            queued = _queue.ToArray();
            _queue.Clear();
        }
        _shutdown.Cancel();
        _signal.Release();
        foreach (var job in queued)
        {
            job.Registration.Dispose();
            job.Cancel(CancellationToken.None);
        }
        try { _runner.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
        _signal.Dispose();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            QueuedJob? job;
            lock (_gate)
            {
                if (_disposed) return;
                var node = _queue.First;
                job = node?.Value;
                if (node is not null) _queue.Remove(node);
            }
            if (job is null) continue;
            job.Registration.Dispose();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(job.CancellationToken, _shutdown.Token);
            if (linked.Token.IsCancellationRequested)
            {
                job.Cancel(linked.Token);
                linked.Dispose();
                continue;
            }
            MachineSlotLease lease;
            try
            {
                lease = await AcquireSlotAsync(job.JobId, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                job.Cancel(linked.Token);
                linked.Dispose();
                continue;
            }
            _ = RunAdmittedJobAsync(job, lease, linked);
        }
    }

    private static async Task RunAdmittedJobAsync(QueuedJob job, MachineSlotLease lease,
        CancellationTokenSource linked)
    {
        try
        {
            using var cpuJob = new WindowsCpuJob();
            await job.ExecuteAsync(cpuJob, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lease.Dispose();
            linked.Dispose();
        }
    }

    private async Task<MachineSlotLease> AcquireSlotAsync(string jobId, CancellationToken cancellationToken)
    {
        // One owner per slot per wait: ownership is per-thread, and per-poll owners would churn threads.
        var owners = new WorkerMutexOwner[_slotNames.Count];
        for (var i = 0; i < owners.Length; i++) owners[i] = new WorkerMutexOwner(_slotNames[i]);
        var winner = -1;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < owners.Length; i++)
                {
                    if (!owners[i].TryAcquire()) continue;
                    winner = i;
                    _slotOwnership[i] = jobId;
                    return new MachineSlotLease(owners[i], i, jobId,
                        released => _slotOwnership.TryRemove(released, out _));
                }
                await Task.Delay(SlotPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            for (var i = 0; i < owners.Length; i++)
                if (i != winner) owners[i].Dispose();
        }
    }

    private void RegisterCancellation(QueuedJob job)
    {
        if (!job.CancellationToken.CanBeCanceled) return;
        job.Registration = job.CancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (job.Node is { List: not null } node) _queue.Remove(node);
            }
            job.Cancel(job.CancellationToken);
        });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MachinePanGlossQueue));
    }

    private abstract class QueuedJob
    {
        protected QueuedJob(string jobId, CancellationToken cancellationToken)
        {
            JobId = jobId;
            CancellationToken = cancellationToken;
        }

        public string JobId { get; }
        public CancellationToken CancellationToken { get; }
        public LinkedListNode<QueuedJob>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }

        public abstract Task ExecuteAsync(WindowsCpuJob cpuJob, CancellationToken linkedToken);
        public abstract void Cancel(CancellationToken token);
    }

    private sealed class QueuedJob<T> : QueuedJob
    {
        private readonly Func<WindowsCpuJob, CancellationToken, Task<T>> _work;
        private readonly TaskCompletionSource<T> _completion;

        public QueuedJob(string jobId, Func<WindowsCpuJob, CancellationToken, Task<T>> work,
            CancellationToken cancellationToken, TaskCompletionSource<T> completion)
            : base(jobId, cancellationToken)
        {
            _work = work;
            _completion = completion;
        }

        public override async Task ExecuteAsync(WindowsCpuJob cpuJob, CancellationToken linkedToken)
        {
            try
            {
                var result = await _work(cpuJob, linkedToken).ConfigureAwait(false);
                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(linkedToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public override void Cancel(CancellationToken token) => _completion.TrySetCanceled(token);
    }
}
