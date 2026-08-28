using System;
using System.Threading;
using System.Threading.Tasks;
using SIL.LCModel;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Worker.Jobs;

namespace SIL.Motif.Worker.Baselines;

/// <summary>Runs one queued Baseline refresh, translating the barrier's answer into a job outcome.</summary>
/// <remarks>
/// The handler decides nothing about whether the refresh may proceed — <see cref="BaselineRefreshBarrier"/>
/// owns that, and owns it by attempting the project open rather than by asking anyone. What this adds is
/// the translation: a project held elsewhere is a job that failed in a way worth retrying, while a capture
/// that broke is a job that failed for a reason the caller has to read.
/// </remarks>
public sealed class BaselineRefreshJobHandler
{
    private readonly BaselineRefreshBarrier _barrier;
    private readonly Func<LcmCache, CancellationToken, Task> _capture;

    /// <summary>Creates a handler over the barrier and the capture it guards.</summary>
    public BaselineRefreshJobHandler(BaselineRefreshBarrier barrier,
        Func<LcmCache, CancellationToken, Task> capture)
    {
        _barrier = barrier ?? throw new ArgumentNullException(nameof(barrier));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    /// <summary>Refreshes the project's Baseline, throwing when the job should be recorded as failed.</summary>
    public async Task<JobOutcome?> RunAsync(ProjectLocator project, CancellationToken cancellationToken)
    {
        var result = await _barrier.RefreshAsync(project, _capture, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded) throw new BaselineRefreshFailedException(result.Outcome, result.Message);
        return JobOutcome.Completed;
    }
}

/// <summary>A refresh that did not happen, carrying which of the barrier's answers explains it.</summary>
public sealed class BaselineRefreshFailedException : Exception
{
    public BaselineRefreshFailedException(BaselineRefreshOutcome outcome, string message)
        : base(message) => Outcome = outcome;

    /// <summary>Whether the project was held elsewhere, or the capture itself broke.</summary>
    public BaselineRefreshOutcome Outcome { get; }
}
