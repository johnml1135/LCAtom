using SIL.Motif.Contract.Jobs;

namespace SIL.Motif.Worker.Jobs;

/// <summary>Performs transactional crash recovery and schedules safe infrastructure retries.</summary>
public sealed class WorkerRecovery
{
    private readonly JobRepository _jobs;
    private readonly IJobClock _clock;

    public WorkerRecovery(JobRepository jobs, IJobClock? clock = null)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _clock = clock ?? new SystemJobClock();
    }

    public RecoveryResult RecoverInterruptedJobs(DateTimeOffset now)
    {
        var interrupted = _jobs.MarkRunningInterrupted(now).ToList();
        foreach (var prior in _jobs.ListRetryableInterrupted())
        {
            if (interrupted.All(job => job.JobId != prior.JobId)) interrupted.Add(prior);
        }
        var retries = new List<JobRecord>();
        foreach (var job in interrupted)
        {
            if (job.FailureCategory != JobFailureCategory.Infrastructure || job.Attempt >= 3 ||
                _jobs.HasLaterAttempt(job.LogicalJobId, job.Attempt))
                continue;
            try
            {
                retries.Add(_jobs.RetryInfrastructure(job.JobId, job.Version, now));
            }
            catch (InvalidOperationException)
            {
                // A stale writer or a concurrently-created attempt is safe to observe on restart.
            }
            catch (Microsoft.Data.Sqlite.SqliteException) when (_jobs.HasLaterAttempt(job.LogicalJobId, job.Attempt))
            {
                // A concurrent worker may win the unique lineage-attempt insert first.
            }
        }
        return new RecoveryResult(interrupted.Select(job => job.JobId).ToArray(), retries);
    }

    public RecoveryResult Recover() => RecoverInterruptedJobs(_clock.UtcNow);
}

/// <summary>Reports durable interruption and retry outcomes from one startup pass.</summary>
public sealed record RecoveryResult(IReadOnlyList<string> InterruptedJobIds, IReadOnlyList<JobRecord> RetryJobs);
