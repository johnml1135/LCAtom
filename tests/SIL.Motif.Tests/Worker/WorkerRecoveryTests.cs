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

        Assert.Equal(clock.UtcNow.AddMinutes(1), DateTimeOffset.Parse(second.NotBeforeUtc!));
        Assert.Equal(clock.UtcNow.AddMinutes(2), DateTimeOffset.Parse(third.NotBeforeUtc!));
        Assert.Equal(JobStatus.Interrupted, jobs.Get(first.JobId)!.Status);
        Assert.Equal(JobStatus.Interrupted, jobs.Get(secondRunning.JobId)!.Status);
        Assert.Equal(3, jobs.ListAttempts(first.LogicalJobId).Count);
        Assert.Empty(new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(clock.UtcNow).ExhaustedJobIds!);
    }

    [Theory]
    [InlineData(JobFailureCategory.ParserRefusal)]
    [InlineData(JobFailureCategory.Semantic)]
    [InlineData(JobFailureCategory.Unknown)]
    public void NonInfrastructureFailuresAreNeverRetried(JobFailureCategory category)
    {
        var project = new ProjectLocator(Path.Combine(_root, "refusal.fwdata"), "refusal");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, Guid.NewGuid() + ".motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var jobs = new JobRepository(database, new FixedClock("2026-08-23T12:00:00Z"));
        var running = jobs.Transition(jobs.Create(NewJob() with { JobId = Guid.NewGuid().ToString("N") }), JobStatus.Running);
        jobs.Transition(running.JobId, JobStatus.Failed, running.Version, category, "{\"error\":true}");

        var result = new WorkerRecovery(jobs).RecoverInterruptedJobs(DateTimeOffset.Parse("2026-08-23T12:00:00Z"));

        Assert.Empty(result.RetryJobs);
        Assert.Single(jobs.ListAttempts(running.LogicalJobId));
    }

    [Fact]
    public void RecoveryDoesNotRegressDurableTimestampsWhenSuppliedTimeIsOlder()
    {
        var project = new ProjectLocator(Path.Combine(_root, "monotonic.fwdata"), "monotonic");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "monotonic.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var clock = new FixedClock("2026-08-23T12:00:00Z");
        var jobs = new JobRepository(database, clock);
        var running = jobs.Transition(jobs.Create(NewJob() with
        {
            JobId = "monotonic",
            CreatedUtc = "2026-08-23T13:00:00Z",
            UpdatedUtc = "2026-08-23T13:00:00Z"
        }), JobStatus.Running);

        new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(DateTimeOffset.Parse("2026-08-23T11:00:00Z"));

        var interrupted = jobs.Get(running.JobId)!;
        Assert.Equal(DateTimeOffset.Parse("2026-08-23T13:00:00Z"), DateTimeOffset.Parse(interrupted.UpdatedUtc));
        Assert.Equal(DateTimeOffset.Parse("2026-08-23T13:00:00Z"), DateTimeOffset.Parse(interrupted.ArchivedUtc!));
    }

    [Fact]
    public void ApplyCannotBeQueuedForAutomaticRecovery()
    {
        var project = new ProjectLocator(Path.Combine(_root, "apply.fwdata"), "apply");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "apply.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var jobs = new JobRepository(database);

        Assert.Throws<InvalidOperationException>(() => jobs.Create(NewJob() with { JobId = "apply", Kind = JobKinds.Apply }));
    }

    [Fact]
    public void ThirdInterruptedAttemptIsExhaustedWithoutLosingPublishedDryRun()
    {
        var project = new ProjectLocator(Path.Combine(_root, "exhaust.fwdata"), "exhaust");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "exhaust.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var clock = new FixedClock("2026-08-23T12:00:00Z");
        var jobs = new JobRepository(database, clock);
        var first = jobs.Transition(jobs.Create(NewJob() with { JobId = "exhaust" }), JobStatus.Running);
        new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(clock.UtcNow);
        var second = jobs.Transition(jobs.ListAttempts(first.LogicalJobId).Single(x => x.Attempt == 2).JobId,
            JobStatus.Running);
        new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(clock.UtcNow);
        var third = jobs.Transition(jobs.ListAttempts(first.LogicalJobId).Single(x => x.Attempt == 3).JobId,
            JobStatus.Running);
        third = jobs.PublishDryRun(third.JobId, "{\"dryRun\":true}", third.Version);

        var result = new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(clock.UtcNow);
        var exhausted = jobs.Get(third.JobId)!;

        Assert.Contains(third.JobId, result.ExhaustedJobIds!);
        Assert.Equal(JobStatus.Failed, exhausted.Status);
        Assert.Equal(JobFailureCategory.Infrastructure, exhausted.FailureCategory);
        Assert.Equal("{\"dryRun\":true}", exhausted.DryRunJson);
        Assert.Equal(3, jobs.ListAttempts(first.LogicalJobId).Count);
        Assert.Empty(new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(clock.UtcNow).RetryJobs);
        new WorkerRecovery(jobs, clock).RecoverInterruptedJobs(clock.UtcNow);
        _ = second;
    }

    [Fact]
    public async Task CompetingRecoveriesExhaustAttemptThreeOnceWithoutCreatingAttemptFour()
    {
        var project = new ProjectLocator(Path.Combine(_root, "competing.fwdata"), "competing");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "competing.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var clock = new FixedClock("2026-08-23T12:00:00Z");
        var jobs = new JobRepository(database, clock);
        var first = jobs.Transition(jobs.Create(NewJob() with { JobId = "competing" }), JobStatus.Running);
        new WorkerRecovery(jobs, clock).Recover();
        var second = jobs.Transition(jobs.ListAttempts(first.LogicalJobId).Single(x => x.Attempt == 2).JobId,
            JobStatus.Running);
        new WorkerRecovery(jobs, clock).Recover();
        var third = jobs.Transition(jobs.ListAttempts(first.LogicalJobId).Single(x => x.Attempt == 3).JobId,
            JobStatus.Running);
        jobs.MarkRunningInterrupted(clock.UtcNow);

        var left = new WorkerRecovery(new JobRepository(database, clock), clock);
        var right = new WorkerRecovery(new JobRepository(database, clock), clock);
        var results = await Task.WhenAll(Task.Run(() => left.Recover()), Task.Run(() => right.Recover()));

        var attempts = jobs.ListAttempts(first.LogicalJobId);
        Assert.Equal(3, attempts.Count);
        Assert.Equal(JobStatus.Failed, jobs.Get(third.JobId)!.Status);
        Assert.Equal(1, results.Sum(result => result.ExhaustedJobIds!.Count));
        Assert.Equal(JobStatus.Failed, jobs.ExhaustInterruptedInfrastructure(third.JobId, third.Version, clock.UtcNow).Status);
        _ = second;
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
