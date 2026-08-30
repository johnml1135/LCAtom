using SIL.Motif.Contract.Jobs;

namespace SIL.Motif.Worker.Jobs;

/// <summary>
/// One claimed job row, handed to a <see cref="JobRunnerLoop.Handler"/> so it can drive the row through
/// its in-flight states without ever carrying a version number to do it.
/// </summary>
/// <remarks>
/// <see cref="JobClaims"/> mints this around the row its own <see cref="JobClaims.Claim"/> already read,
/// the same way it mints a claim token: by owning the number every write must supply, this closes an
/// interface gap a caller-carried version left open — transitioning twice from one stale
/// <see cref="JobRecord"/> used to fail with no help from the type telling a caller why. Every write here
/// re-reads the row immediately before writing it, exactly as the idiom it replaces did, so this changes
/// who carries the number, not whether a write that lands on a row somebody else already moved is refused.
/// </remarks>
public sealed class ClaimedJob
{
    private readonly JobRepository _jobs;

    internal ClaimedJob(JobRepository jobs, JobRecord claimed)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        Job = claimed ?? throw new ArgumentNullException(nameof(claimed));
    }

    /// <summary>This job's id, stable for the life of the claim.</summary>
    public string JobId => Job.JobId;

    /// <summary>The row's state as of the last read this handle itself performed.</summary>
    public JobRecord Job { get; private set; }

    /// <summary>Moves this job to a non-terminal state, against whatever version the row now holds.</summary>
    public JobRecord Transition(JobStatus next, string? resultJson = null)
    {
        Job = _jobs.Transition(JobId, next, resultJson);
        return Job;
    }

    /// <summary>Moves this job to a terminal state carrying a failure category, against whatever version the row now holds.</summary>
    public JobRecord Transition(JobStatus next, JobFailureCategory category, string? resultJson = null)
    {
        var before = _jobs.Get(JobId) ?? throw new InvalidOperationException("The claimed job no longer exists.");
        Job = _jobs.Transition(JobId, next, before.Version, category, resultJson);
        return Job;
    }

    /// <summary>Records this job's Dry Run, against whatever version the row now holds.</summary>
    public JobRecord PublishDryRun(string dryRunJson)
    {
        var before = _jobs.Get(JobId) ?? throw new InvalidOperationException("The claimed job no longer exists.");
        Job = _jobs.PublishDryRun(JobId, dryRunJson, before.Version);
        return Job;
    }

    /// <summary>Wraps the job named by <paramref name="jobId"/> as it reads right now.</summary>
    /// <remarks>For a caller that only has an id, not an already-read row — a real claim always has the row.</remarks>
    internal static ClaimedJob Of(JobRepository jobs, string jobId) =>
        new(jobs, jobs.Get(jobId) ?? throw new KeyNotFoundException($"Job '{jobId}' was not found."));
}
