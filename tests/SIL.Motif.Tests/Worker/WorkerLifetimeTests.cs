using SIL.Motif.Worker;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>
/// Covers <see cref="WorkerLifetime"/> in isolation: idleness is a predicate the caller supplies — the
/// sweep's own finding, in production — rather than a cached lease, so these drive that predicate
/// directly instead of standing up a runner.
/// </summary>
public sealed class WorkerLifetimeTests
{
    [Fact]
    public async Task IdleWithNothingQueuedExitsWithinItsTimeout()
    {
        using var shutdown = new CancellationTokenSource();

        var running = new WorkerLifetime().RunUntilIdleAsync(
            TimeSpan.FromMilliseconds(100), () => false, shutdown.Token);

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(running.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task StaysAliveWhileWorkIsActiveAndDoesNotExitMidJob()
    {
        using var shutdown = new CancellationTokenSource();
        var busyUntil = DateTime.UtcNow.AddMilliseconds(400);

        var running = new WorkerLifetime().RunUntilIdleAsync(
            TimeSpan.FromMilliseconds(100), () => DateTime.UtcNow < busyUntil, shutdown.Token);

        // Already past what an idle-only run would have taken to exit; still busy keeps it alive.
        await Task.Delay(250);
        Assert.False(running.IsCompleted);

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(running.IsCompletedSuccessfully);
    }
}
