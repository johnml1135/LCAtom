using SIL.Motif.Contract.Jobs;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Jobs;

/// <summary>Performs transactional crash recovery and schedules safe infrastructure retries.</summary>
public sealed class WorkerRecovery
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MotifDatabase, SemaphoreSlim>
        RecoveryGates = new();
    private readonly JobRepository _jobs;
    private readonly IJobClock _clock;

    public WorkerRecovery(JobRepository jobs, IJobClock? clock = null)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _clock = clock ?? new SystemJobClock();
    }

    public RecoveryResult RecoverInterruptedJobs(DateTimeOffset now)
    {
        var gate = RecoveryGates.GetValue(_jobs.Database, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            return RecoverInterruptedJobsCore(now);
        }
        finally
        {
            gate.Release();
        }
    }

    private RecoveryResult RecoverInterruptedJobsCore(DateTimeOffset now)
    {
        var interrupted = _jobs.MarkRunningInterrupted(now).ToList();
        foreach (var prior in _jobs.ListInterruptedInfrastructure())
        {
            if (interrupted.All(job => job.JobId != prior.JobId)) interrupted.Add(prior);
        }
        var retries = new List<JobRecord>();
        var exhausted = new List<string>();
        foreach (var job in interrupted)
        {
            if (job.FailureCategory != JobFailureCategory.Infrastructure || _jobs.HasLaterAttempt(job.LogicalJobId, job.Attempt))
                continue;
            if (job.Attempt >= 3)
            {
                try
                {
                    _ = _jobs.ExhaustInterruptedInfrastructure(job.JobId, job.Version, now, out var finalized);
                    if (finalized) exhausted.Add(job.JobId);
                }
                catch (InvalidOperationException) when
                    (_jobs.IsAlreadyExhaustedInfrastructure(job.JobId, job.Attempt) ||
                     _jobs.HasLaterAttempt(job.LogicalJobId, job.Attempt)) { }
                continue;
            }
            try
            {
                retries.Add(_jobs.RetryInfrastructure(job.JobId, job.Version, now));
            }
            catch (InvalidOperationException) when (_jobs.HasLaterAttempt(job.LogicalJobId, job.Attempt)) { }
            catch (Microsoft.Data.Sqlite.SqliteException exception) when
                (exception.SqliteErrorCode == 19 && _jobs.HasLaterAttempt(job.LogicalJobId, job.Attempt))
            {
                // A concurrent worker may win the unique lineage-attempt insert first.
            }
        }
        return new RecoveryResult(interrupted.Select(job => job.JobId).ToArray(), retries, exhausted);
    }

    public RecoveryResult Recover() => RecoverInterruptedJobs(_clock.UtcNow);
}

/// <summary>Reports durable interruption and retry outcomes from one startup pass.</summary>
public sealed record RecoveryResult(
    IReadOnlyList<string> InterruptedJobIds,
    IReadOnlyList<JobRecord> RetryJobs,
    IReadOnlyList<string>? ExhaustedJobIds = null);
