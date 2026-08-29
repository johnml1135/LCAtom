using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SIL.Motif.Worker;

/// <summary>Provides a monotonic-enough wall-clock seam for worker lifetime decisions.</summary>
public interface IWorkerClock
{
    /// <summary>The current UTC instant used for deadline comparisons.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>The monotonic elapsed time used for deadline enforcement.</summary>
    TimeSpan MonotonicNow { get; }

    /// <summary>Waits without coupling deadline logic to wall-clock polling.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Waits until the worker has been inactive for its configured idle period.</summary>
public sealed class WorkerLifetime
{
    private readonly IWorkerClock _clock;

    /// <summary>Creates a lifetime monitor using the system UTC clock when no clock is supplied.</summary>
    public WorkerLifetime(IWorkerClock? clock = null) => _clock = clock ?? SystemWorkerClock.Instance;

    /// <summary>Completes when shutdown is requested or an idle period elapses.</summary>
    /// <param name="hasActiveWork">
    /// Answers "is there queued, running, or waiting work right now", freshly, each time it is called —
    /// callers must not cache the result across calls.
    /// </param>
    public async Task RunUntilIdleAsync(
        TimeSpan idleTimeout, Func<bool> hasActiveWork, CancellationToken shutdown)
    {
        if (idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        if (hasActiveWork is null)
            throw new ArgumentNullException(nameof(hasActiveWork));

        var idleSince = _clock.MonotonicNow;
        var wasBusy = hasActiveWork();
        while (!shutdown.IsCancellationRequested)
        {
            var now = _clock.MonotonicNow;
            if (hasActiveWork())
            {
                idleSince = now;
                wasBusy = true;
            }
            else if (wasBusy)
            {
                idleSince = now;
                wasBusy = false;
            }
            else if (now - idleSince >= idleTimeout)
            {
                return;
            }

            var remaining = idleTimeout - (now - idleSince);
            try { await _clock.DelayAsync(remaining, shutdown).ConfigureAwait(false); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private sealed class SystemWorkerClock : IWorkerClock
    {
        public static readonly SystemWorkerClock Instance = new SystemWorkerClock();

        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public TimeSpan MonotonicNow =>
            TimeSpan.FromSeconds((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(delay, cancellationToken);
    }
}
