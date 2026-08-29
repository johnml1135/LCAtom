using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>
/// Covers the two inherited findings Task 8 answers: a Dry Run parked at
/// <see cref="JobStatus.WaitingForBaseline"/> must ask for the Baseline it is waiting on, and it must
/// never wait forever. <see cref="SIL.Motif.Worker.Program.ReconcileParkedDryRuns"/> is what a sweep tick runs before it
/// peeks any project's queue head.
/// </summary>
public sealed class ParkedDryRunReconciliationTests : IDisposable
{
    private const string BaselineRefreshKind = "baseline-refresh";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-parked-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    public ParkedDryRunReconciliationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ABaselineRefreshIsEnqueuedForAProjectWithAParkedDryRunAndNoRefreshInFlight()
    {
        var runtime = OpenRuntime("no-refresh-yet");
        var parked = ParkDryRun(runtime, "dry-1");

        SIL.Motif.Worker.Program.ReconcileParkedDryRuns(runtime, _now);

        var refreshes = runtime.Jobs.ListByProjectAndKind(runtime.WorkspaceKey, BaselineRefreshKind);
        var refresh = Assert.Single(refreshes);
        Assert.Equal(JobStatus.Queued, refresh.Status);
        // Untouched: the parked row itself still waits on the refresh this call just started.
        Assert.Equal(JobStatus.WaitingForBaseline, runtime.Jobs.Get(parked)!.Status);
    }

    [Fact]
    public void NoSecondBaselineRefreshIsEnqueuedWhileOneIsAlreadyQueued()
    {
        var runtime = OpenRuntime("already-queued");
        ParkDryRun(runtime, "dry-1");
        runtime.Jobs.Create("existing-refresh", runtime.WorkspaceKey, BaselineRefreshKind, "{}", Stamp(_now));

        SIL.Motif.Worker.Program.ReconcileParkedDryRuns(runtime, _now);

        var refresh = Assert.Single(runtime.Jobs.ListByProjectAndKind(runtime.WorkspaceKey, BaselineRefreshKind));
        Assert.Equal("existing-refresh", refresh.JobId);
    }

    [Fact]
    public void NoSecondBaselineRefreshIsEnqueuedWhileOneIsAlreadyRunning()
    {
        var runtime = OpenRuntime("already-running");
        ParkDryRun(runtime, "dry-1");
        runtime.Jobs.Create("existing-refresh", runtime.WorkspaceKey, BaselineRefreshKind, "{}", Stamp(_now));
        runtime.Jobs.Transition("existing-refresh", JobStatus.Running);

        SIL.Motif.Worker.Program.ReconcileParkedDryRuns(runtime, _now);

        var refresh = Assert.Single(runtime.Jobs.ListByProjectAndKind(runtime.WorkspaceKey, BaselineRefreshKind));
        Assert.Equal(JobStatus.Running, refresh.Status);
    }

    [Fact]
    public void AParkedDryRunBecomesClaimableAgainOncePublishedBaselineExists()
    {
        var runtime = OpenRuntime("baseline-exists");
        var parked = ParkDryRun(runtime, "dry-1");
        RecordBaseline(runtime);

        SIL.Motif.Worker.Program.ReconcileParkedDryRuns(runtime, _now);

        Assert.Equal(JobStatus.Queued, runtime.Jobs.Get(parked)!.Status);
        // A Baseline settles it directly; no refresh job is needed once one already exists.
        Assert.Empty(runtime.Jobs.ListByProjectAndKind(runtime.WorkspaceKey, BaselineRefreshKind));
    }

    [Fact]
    public void AFailedRefreshBelowTheBoundGetsAnAutomaticRetryRatherThanFailingTheParkedRow()
    {
        var runtime = OpenRuntime("retry-below-bound");
        var parked = ParkDryRun(runtime, "dry-1");
        FailInfrastructure(runtime, "refresh-1", attempt: 1);

        SIL.Motif.Worker.Program.ReconcileParkedDryRuns(runtime, _now);

        var all = runtime.Jobs.ListByProjectAndKind(runtime.WorkspaceKey, BaselineRefreshKind);
        Assert.Equal(2, all.Count);
        var retried = Assert.Single(all, job => job.Attempt == 2);
        Assert.Equal(JobStatus.Queued, retried.Status);
        Assert.Equal(JobStatus.WaitingForBaseline, runtime.Jobs.Get(parked)!.Status);
    }

    [Fact]
    public void AParkedDryRunFailsRatherThanWaitingForeverOnceBaselineRefreshExhaustsItsBoundedAttempts()
    {
        var runtime = OpenRuntime("exhausted");
        var firstParked = ParkDryRun(runtime, "dry-1");
        var secondParked = ParkDryRun(runtime, "dry-2");
        SeedExhaustedBaselineRefresh(runtime);

        SIL.Motif.Worker.Program.ReconcileParkedDryRuns(runtime, _now);

        var first = runtime.Jobs.Get(firstParked)!;
        var second = runtime.Jobs.Get(secondParked)!;
        Assert.Equal(JobStatus.Failed, first.Status);
        Assert.Equal(JobStatus.Failed, second.Status);
        Assert.Equal(JobFailureCategory.Infrastructure, first.FailureCategory);
        Assert.Equal(JobFailureCategory.Infrastructure, second.FailureCategory);
        // No further attempt is started: the lineage is done, not merely paused.
        Assert.Equal(3, runtime.Jobs.ListByProjectAndKind(runtime.WorkspaceKey, BaselineRefreshKind).Count);
    }

    private ProjectRuntime OpenRuntime(string identity)
    {
        var project = new ProjectLocator(Path.Combine(_root, identity + ".fwdata"), identity);
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(_root, identity, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        var registry = new ProjectRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs), new WorkspaceCleaner(ownership)),
            new ProjectRuntimeActivity(), () => _now);
        return registry.GetOrOpen(project);
    }

    private string ParkDryRun(ProjectRuntime runtime, string jobId)
    {
        runtime.Jobs.Create(jobId, runtime.WorkspaceKey, "dry-run", "{}", Stamp(_now));
        runtime.Jobs.Transition(jobId, JobStatus.WaitingForBaseline);
        return jobId;
    }

    private void RecordBaseline(ProjectRuntime runtime)
    {
        var token = new BaselineToken("project-identity", "sha256:" + new string('a', 64), "1",
            _now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), "sha256:" + new string('b', 64));
        runtime.Baselines.Record(runtime.WorkspaceKey,
            new BaselinePublication(_root, Path.Combine(_root, "baseline.zip"), token), _now);
    }

    /// Drives one baseline-refresh attempt straight to a bounded infrastructure failure.
    private void FailInfrastructure(ProjectRuntime runtime, string jobId, int attempt)
    {
        var record = runtime.Jobs.Get(jobId);
        if (record is null)
        {
            runtime.Jobs.Create(jobId, runtime.WorkspaceKey, BaselineRefreshKind, "{}", Stamp(_now));
            record = runtime.Jobs.Get(jobId);
        }
        runtime.Jobs.Transition(jobId, JobStatus.Running);
        record = runtime.Jobs.Get(jobId)!;
        runtime.Jobs.Transition(jobId, JobStatus.Failed, record.Version, JobFailureCategory.Infrastructure, "{}");
        Assert.Equal(attempt, runtime.Jobs.Get(jobId)!.Attempt);
    }

    /// Three real attempts, each an infrastructure failure, reaching the bound's own attempt-3 ceiling.
    private void SeedExhaustedBaselineRefresh(ProjectRuntime runtime)
    {
        FailInfrastructure(runtime, "refresh-1", attempt: 1);
        var first = runtime.Jobs.Get("refresh-1")!;
        var retry2 = runtime.Jobs.RetryInfrastructure("refresh-1", first.Version, _now);
        FailInfrastructure(runtime, retry2.JobId, attempt: 2);
        var second = runtime.Jobs.Get(retry2.JobId)!;
        var retry3 = runtime.Jobs.RetryInfrastructure(retry2.JobId, second.Version, _now);
        FailInfrastructure(runtime, retry3.JobId, attempt: 3);
    }

    private static string Stamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
