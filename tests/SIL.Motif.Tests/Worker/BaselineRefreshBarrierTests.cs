using SIL.LCModel;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Worker.Baselines;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>Covers whether one Baseline refresh is allowed to start, and what it says when it is not.</summary>
/// <remarks>
/// The open seam is faked, so these cover the decision and the wording rather than the cache lifetime an
/// opened project brings with it. Whether a real capture produces a valid bundle is
/// <c>BaselineBundleTests</c>' subject, not this one's.
/// </remarks>
public sealed class BaselineRefreshBarrierTests
{
    private static readonly ProjectLocator Project =
        new(@"C:\projects\Sena 3\Sena 3.fwdata", "Sena 3");

    [Fact]
    public async Task AProjectHeldElsewhereIsRefusedWithoutRunningTheCapture()
    {
        var captured = false;
        var barrier = new BaselineRefreshBarrier(_ => throw new LcmFileLockedException("held"));

        var result = await barrier.RefreshAsync(Project, (_, _) =>
        {
            captured = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(BaselineRefreshOutcome.ProjectInUse, result.Outcome);
        Assert.False(captured);
        // The message has to say what to do, because this is the one refusal an ordinary user will meet.
        Assert.Contains("Close it and run this again", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCaptureIsReportedSeparatelyFromAHeldProject()
    {
        var barrier = new BaselineRefreshBarrier(_ => null!);

        var result = await barrier.RefreshAsync(Project,
            (_, _) => throw new InvalidOperationException("the writing system store is missing"),
            CancellationToken.None);

        // Distinct outcomes because they are distinct remedies: one is "close FieldWorks", one is not.
        Assert.Equal(BaselineRefreshOutcome.CaptureFailed, result.Outcome);
        Assert.Contains("writing system store", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledCaptureIsNotReportedAsAFailedOne()
    {
        var barrier = new BaselineRefreshBarrier(_ => null!);

        await Assert.ThrowsAsync<OperationCanceledException>(() => barrier.RefreshAsync(Project,
            (_, token) => throw new OperationCanceledException(token), new CancellationToken(true)));
    }

    [Fact]
    public async Task AReachableProjectRunsTheCaptureAndReportsSuccess()
    {
        var captured = false;
        var barrier = new BaselineRefreshBarrier(_ => null!);

        var result = await barrier.RefreshAsync(Project, (_, _) =>
        {
            captured = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(captured);
    }
}
