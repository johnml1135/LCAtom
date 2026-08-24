using SIL.Motif.Contract.Baselines;
using SIL.Motif.Worker.Scheduling;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class ProjectLaneTests
{
    [Fact]
    public async Task RefreshIsABarrierThatAssignsOldAndNewBaselinesByQueueOrder()
    {
        using var lane = new ProjectLane(Token('a'));
        var firstStarted = Signal();
        var releaseFirst = Signal();
        var first = lane.EnqueueAsync(ProjectWorkItem.DryRun(async (token, cancellationToken) =>
        {
            Assert.Equal(Token('a'), token);
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
        }), CancellationToken.None);
        await firstStarted.Task;

        var refresh = lane.EnqueueAsync(ProjectWorkItem.Refresh(async cancellationToken =>
        {
            await Task.Yield();
            return Token('b');
        }), CancellationToken.None);
        var later = lane.EnqueueAsync(ProjectWorkItem.DryRun((token, _) =>
        {
            Assert.Equal(Token('b'), token);
            return Task.CompletedTask;
        }), CancellationToken.None);

        releaseFirst.SetResult();
        await Task.WhenAll(first, refresh, later);
    }

    [Fact]
    public async Task FailedRefreshKeepsLaterWorkWaitingForABaseline()
    {
        using var lane = new ProjectLane(Token('a'));
        var refresh = lane.EnqueueAsync(ProjectWorkItem.Refresh(
            _ => Task.FromException<BaselineToken>(new IOException("capture failed"))), CancellationToken.None);
        var laterStarted = false;
        var later = lane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) =>
        {
            laterStarted = true;
            return Task.CompletedTask;
        }), CancellationToken.None);

        var failure = await Assert.ThrowsAsync<IOException>(() => refresh);
        Assert.Equal("capture failed", failure.Message);
        await Task.Delay(50);
        Assert.False(laterStarted);
        Assert.False(later.IsCompleted);
    }

    [Fact]
    public async Task ApplyOutranksRefreshThatHasNotStartedButNotAnIsolatedDryRun()
    {
        using var lane = new ProjectLane(Token('a'));
        var dryRunStarted = Signal();
        var releaseDryRun = Signal();
        var dryRun = lane.EnqueueAsync(ProjectWorkItem.DryRun(async (_, cancellationToken) =>
        {
            dryRunStarted.SetResult();
            await releaseDryRun.Task.WaitAsync(cancellationToken);
        }), CancellationToken.None);
        await dryRunStarted.Task;

        var refreshStarted = false;
        var refresh = lane.EnqueueAsync(ProjectWorkItem.Refresh(_ =>
        {
            refreshStarted = true;
            return Task.FromResult(Token('b'));
        }), CancellationToken.None);
        using var apply = await lane.TryAcquireApplyGateAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.NotNull(apply);
        Assert.False(refreshStarted);
        releaseDryRun.SetResult();
        await dryRun;
        apply.Dispose();
        await refresh;
    }

    [Fact]
    public async Task AlreadyStartedRefreshCaptureFinishesBeforeWaitingApplyAcquiresTheGate()
    {
        using var lane = new ProjectLane(Token('a'));
        var captureStarted = Signal();
        var releaseCapture = Signal();
        var refresh = lane.EnqueueAsync(ProjectWorkItem.Refresh(async cancellationToken =>
        {
            captureStarted.SetResult();
            await releaseCapture.Task.WaitAsync(cancellationToken);
            return Token('b');
        }), CancellationToken.None);
        await captureStarted.Task;

        var apply = lane.TryAcquireApplyGateAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(apply.IsCompleted);
        Assert.False(refresh.IsCompleted);
        releaseCapture.SetResult();

        await refresh;
        using var lease = await apply;
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task DifferentProjectLanesRunConcurrentlyWhileOneLaneRemainsSerial()
    {
        using var firstLane = new ProjectLane(Token('a'));
        using var secondLane = new ProjectLane(Token('b'));
        var release = Signal();
        var firstStarted = Signal();
        var secondStarted = Signal();
        var sameLaneSecondStarted = false;

        var first = firstLane.EnqueueAsync(ProjectWorkItem.DryRun(async (_, cancellationToken) =>
        {
            firstStarted.SetResult();
            await release.Task.WaitAsync(cancellationToken);
        }), CancellationToken.None);
        var sameLaneSecond = firstLane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) =>
        {
            sameLaneSecondStarted = true;
            return Task.CompletedTask;
        }), CancellationToken.None);
        var other = secondLane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) =>
        {
            secondStarted.SetResult();
            return Task.CompletedTask;
        }), CancellationToken.None);

        await Task.WhenAll(firstStarted.Task, secondStarted.Task, other);
        Assert.False(sameLaneSecondStarted);
        release.SetResult();
        await Task.WhenAll(first, sameLaneSecond);
    }

    [Fact]
    public async Task CandidateExportIsSerializedInTheProjectLane()
    {
        using var lane = new ProjectLane(Token('a'));
        var dryRunStarted = Signal();
        var releaseDryRun = Signal();
        var exportStarted = false;
        var dryRun = lane.EnqueueAsync(ProjectWorkItem.DryRun(async (_, cancellationToken) =>
        {
            dryRunStarted.SetResult();
            await releaseDryRun.Task.WaitAsync(cancellationToken);
        }), CancellationToken.None);
        await dryRunStarted.Task;
        var export = lane.EnqueueAsync(ProjectWorkItem.CandidateExport((token, _) =>
        {
            exportStarted = true;
            Assert.Equal(Token('a'), token);
            return Task.CompletedTask;
        }), CancellationToken.None);

        await Task.Delay(50);
        Assert.False(exportStarted);
        releaseDryRun.SetResult();
        await Task.WhenAll(dryRun, export);
    }

    [Fact]
    public void RegistryReturnsOneLanePerCanonicalProjectKey()
    {
        using var registry = new ProjectLaneRegistry(_ => Token('a'));

        Assert.Same(registry.GetOrCreate("project-key"), registry.GetOrCreate("project-key"));
        Assert.NotSame(registry.GetOrCreate("project-key"), registry.GetOrCreate("other-key"));
    }

    [Fact]
    public async Task DisposeCancelsActiveLaneWorkAndWaitsForItToExit()
    {
        var lane = new ProjectLane(Token('a'));
        var started = Signal();
        var cancelled = Signal();
        _ = lane.EnqueueAsync(ProjectWorkItem.DryRun(async (_, cancellationToken) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.SetResult();
            }
        }), CancellationToken.None);
        await started.Task;

        await Task.Run(lane.Dispose).WaitAsync(TimeSpan.FromSeconds(2));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DisposeStopsARefreshThatIsHoldingBackForAWaitingApply()
    {
        var lane = new ProjectLane(Token('a'));
        var held = await lane.TryAcquireApplyGateAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(held);
        var waiting = lane.TryAcquireApplyGateAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
        var refreshStarted = false;
        var refresh = lane.EnqueueAsync(ProjectWorkItem.Refresh(_ =>
        {
            refreshStarted = true;
            return Task.FromResult(Token('b'));
        }), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(refreshStarted);

        await Task.Run(lane.Dispose).WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        held.Dispose();
    }

    [Fact]
    public async Task ExplicitOldBaselineResubmissionCanRunWhileFailedBarrierRemainsClosed()
    {
        using var lane = new ProjectLane(Token('a'));
        await Assert.ThrowsAsync<IOException>(() => lane.EnqueueAsync(ProjectWorkItem.Refresh(
            _ => Task.FromException<BaselineToken>(new IOException("capture failed"))),
            CancellationToken.None));
        var ordinary = lane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) => Task.CompletedTask),
            CancellationToken.None);

        var explicitResult = await lane.EnqueueAgainstBaselineAsync(ProjectWorkItem.CandidateExport(
            (baseline, _) =>
            {
                Assert.Equal(Token('a'), baseline);
                return Task.CompletedTask;
            }), Token('a'), true, CancellationToken.None);

        Assert.Equal(Token('a'), explicitResult.Baseline);
        Assert.False(ordinary.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = lane.EnqueueAgainstBaselineAsync(
                ProjectWorkItem.DryRun((_, _) => Task.CompletedTask), Token('a'), false,
                CancellationToken.None);
        });
    }

    [Fact]
    public async Task QueuedWorkBehindAClosedBarrierCancelsPromptlyWithoutRunning()
    {
        using var lane = new ProjectLane(Token('a'));
        await Assert.ThrowsAsync<IOException>(() => lane.EnqueueAsync(ProjectWorkItem.Refresh(
            _ => Task.FromException<BaselineToken>(new IOException("capture failed"))),
            CancellationToken.None));

        using var cts = new CancellationTokenSource();
        var dryRunStarted = false;
        var queued = lane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) =>
        {
            dryRunStarted = true;
            return Task.CompletedTask;
        }), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(dryRunStarted);
    }

    [Fact]
    public async Task QueuedWorkBehindOrdinaryRunningWorkCancelsPromptlyAndLaneContinues()
    {
        using var lane = new ProjectLane(Token('a'));
        var runningStarted = Signal();
        var releaseRunning = Signal();
        var running = lane.EnqueueAsync(ProjectWorkItem.DryRun(async (_, cancellationToken) =>
        {
            runningStarted.SetResult();
            await releaseRunning.Task.WaitAsync(cancellationToken);
        }), CancellationToken.None);
        await runningStarted.Task;

        using var cts = new CancellationTokenSource();
        var queuedStarted = false;
        var queued = lane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) =>
        {
            queuedStarted = true;
            return Task.CompletedTask;
        }), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(queuedStarted);

        releaseRunning.SetResult();
        await running;

        var afterStarted = false;
        var after = lane.EnqueueAsync(ProjectWorkItem.DryRun((_, _) =>
        {
            afterStarted = true;
            return Task.CompletedTask;
        }), CancellationToken.None);
        await after;
        Assert.True(afterStarted);
    }

    private static BaselineToken Token(char value) => new(
        "project", "sha256:" + new string(value, 64), "1", "2026-08-24T00:00:00Z",
        "sha256:" + new string(value, 64));

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
