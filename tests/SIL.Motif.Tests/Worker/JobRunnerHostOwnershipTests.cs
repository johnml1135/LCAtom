using SIL.Motif.Worker;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>
/// Covers <see cref="Program.TryAcquireOwnershipWithRetryAsync"/>: the reason a runner the CLI kicks does
/// not need a protocol to defer to one already alive, and the reason it does not give up the instant a
/// live owner is mid-shutdown rather than gone.
/// </summary>
public sealed class JobRunnerHostOwnershipTests
{
    [Fact]
    public async Task KickingWhenARunnerIsAlreadyAliveDoesNotStartASecondOwner()
    {
        var ns = "kick-alive-" + Guid.NewGuid().ToString("N");
        using var alive = JobRunnerHost.ForNamespace(ns);
        Assert.True(alive.TryAcquireOwnership());

        using var kicked = JobRunnerHost.ForNamespace(ns);
        var acquired = await SIL.Motif.Worker.Program.TryAcquireOwnershipWithRetryAsync(kicked);

        Assert.False(acquired);
        Assert.False(kicked.IsOwner);
        Assert.True(alive.IsOwner);
    }

    [Fact]
    public async Task RetryingAcquiresOwnershipOnceTheExitingRunnerReleasesItWithinTheWindow()
    {
        var ns = "kick-race-" + Guid.NewGuid().ToString("N");
        using var exiting = JobRunnerHost.ForNamespace(ns);
        Assert.True(exiting.TryAcquireOwnership());

        using var kicked = JobRunnerHost.ForNamespace(ns);
        var retrying = SIL.Motif.Worker.Program.TryAcquireOwnershipWithRetryAsync(kicked);

        // Simulates the exiting runner reaching the end of its final idle tick and releasing mid-retry.
        await Task.Delay(300);
        exiting.Dispose();

        Assert.True(await retrying);
        Assert.True(kicked.IsOwner);
    }
}
