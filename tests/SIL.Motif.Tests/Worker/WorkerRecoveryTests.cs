using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-recovery-" + Guid.NewGuid().ToString("N"));

    public WorkerRecoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void StartupMarksRunningAttemptInterruptedAndSchedulesOnlyInfrastructureRetry()
    {
        var project = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        var dbPath = Path.Combine(_root, "project.motif.db");
        using var database = MotifDatabase.OpenOwned(dbPath, project, MotifSchema.CurrentSchema, new Version(1, 0));
        var clock = new FixedClock("2026-08-23T12:00:00Z");
        var jobs = new JobRepository(database, clock);
        var running = jobs.Transition(jobs.Create(NewJob()), JobStatus.Running);
        var recovery = new WorkerRecovery(jobs, clock);

        var result = recovery.RecoverInterruptedJobs(clock.UtcNow);

        Assert.Contains(running.JobId, result.InterruptedJobIds);
        Assert.Equal(JobStatus.Interrupted, jobs.Get(running.JobId)!.Status);
        var retry = Assert.Single(jobs.ListAttempts(running.LogicalJobId).Where(x => x.JobId != running.JobId));
        Assert.Equal(JobStatus.Queued, retry.Status);
        Assert.Equal(JobFailureCategory.None, retry.FailureCategory);
        Assert.True(DateTimeOffset.Parse(retry.NotBeforeUtc!) > clock.UtcNow);
    }

    [Fact]
    public void CancellationRequestedRunningAttemptIsInterruptedWithoutRetry()
    {
        var project = new ProjectLocator(Path.Combine(_root, "cancel.fwdata"), "cancel");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "cancel.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var clock = new FixedClock("2026-08-23T12:00:00Z");
        var jobs = new JobRepository(database, clock);
        var running = jobs.Transition(jobs.Create(NewJob() with { JobId = "cancel" }), JobStatus.Running);
        jobs.RequestCancellation(running.JobId, running.Version);

        new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(clock.UtcNow);

        var attempts = jobs.ListAttempts(running.LogicalJobId);
        Assert.Single(attempts);
        Assert.Equal(JobFailureCategory.Cancellation, attempts[0].FailureCategory);
        Assert.True(attempts[0].CancellationRequested);
    }

    [Fact]
    public void RetryDelaysIncreaseAndTerminalHistoryRemainsImmutable()
    {
        var project = new ProjectLocator(Path.Combine(_root, "retry.fwdata"), "retry");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "retry.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var clock = new FixedClock("2026-08-23T12:00:00Z");
        var jobs = new JobRepository(database, clock);
        var first = jobs.Transition(jobs.Create(NewJob() with { JobId = "first" }), JobStatus.Running);
        new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(clock.UtcNow);
        var second = jobs.ListAttempts(first.LogicalJobId).Single(x => x.Attempt == 2);
        var secondRunning = jobs.Transition(second.JobId, JobStatus.Running);
        new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(clock.UtcNow);
        var third = jobs.ListAttempts(first.LogicalJobId).Single(x => x.Attempt == 3);

        Assert.True(DateTimeOffset.Parse(third.NotBeforeUtc!) > DateTimeOffset.Parse(second.NotBeforeUtc!));
        Assert.Equal(JobStatus.Interrupted, jobs.Get(first.JobId)!.Status);
        Assert.Equal(JobStatus.Interrupted, jobs.Get(secondRunning.JobId)!.Status);
        Assert.Equal(3, jobs.ListAttempts(first.LogicalJobId).Count);
    }

    private static JobRecord NewJob() => new("job", "project", "dry-run", JobStatus.Queued, 1,
        "{\"proposal\":[]}", null, "2026-08-23T11:00:00Z", "2026-08-23T11:00:00Z");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class FixedClock(string value) : IJobClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse(value);
    }
}
