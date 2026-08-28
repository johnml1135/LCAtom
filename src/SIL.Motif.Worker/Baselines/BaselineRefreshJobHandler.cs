using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.LCModel;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Worker.Jobs;

namespace SIL.Motif.Worker.Baselines;

/// <summary>Runs one queued Baseline refresh, translating the barrier's answer into a job outcome.</summary>
/// <remarks>
/// The handler decides nothing about whether the refresh may proceed — <see cref="BaselineRefreshBarrier"/>
/// owns that, and owns it by attempting the project open rather than by asking anyone. What this adds is
/// the translation: both of the barrier's failure answers become a <see cref="JobFailureCategory.Infrastructure"/>
/// failure, so a parked Dry Run waiting on this project's Baseline can bound its own retries through
/// <see cref="JobRepository.RetryInfrastructure"/> the same way a crash-interrupted job does.
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

    /// <summary>Refreshes the project's Baseline, or reports an infrastructure failure carrying why.</summary>
    public async Task<JobOutcome?> RunAsync(ProjectLocator project, CancellationToken cancellationToken)
    {
        var result = await _barrier.RefreshAsync(project, _capture, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var detail = JsonSerializer.Serialize(new { detail = result.Message, outcome = WireOf(result.Outcome) });
            return new JobOutcome(JobStatus.Failed, JobFailureCategory.Infrastructure, detail);
        }
        return JobOutcome.Completed;
    }

    private static string WireOf(BaselineRefreshOutcome outcome) => outcome switch
    {
        BaselineRefreshOutcome.ProjectInUse => "project-in-use",
        BaselineRefreshOutcome.CaptureFailed => "capture-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };
}
