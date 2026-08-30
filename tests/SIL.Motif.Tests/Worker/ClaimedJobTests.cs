using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Jobs;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>Covers the handle <see cref="JobRunnerLoop"/> hands a handler in place of a version number.</summary>
public sealed class ClaimedJobTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-claimed-job-" + Guid.NewGuid().ToString("N"));
    private readonly MotifDatabase _database;
    private const string Project = "project-key";

    public ClaimedJobTests()
    {
        Directory.CreateDirectory(_root);
        var locator = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        _database = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), locator,
            MotifSchema.CurrentSchema, new Version(1, 0));
    }

    [Fact]
    public void ATransitionCanFollowAnotherWithoutTheCallerEverSupplyingAVersion()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "dry-run", "{}", Now());
        var claim = ClaimedJob.Of(jobs, "job-1");

        claim.Transition(JobStatus.WaitingForBaseline);
        claim.Transition(JobStatus.Queued);

        Assert.Equal(JobStatus.Queued, jobs.Get("job-1")!.Status);
    }

    [Fact]
    public void AHandleStillLosesToAnotherWriterEvenThoughItNeverAsksForAVersion()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "dry-run", "{}", Now());
        var running = jobs.Transition(jobs.Get("job-1")!, JobStatus.Running);

        // The handle's own snapshot is the moment it was built; it never refreshes itself.
        var claim = ClaimedJob.Of(jobs, "job-1");
        Assert.Equal(running, claim.Job);

        // Another writer finishes the job while this handle still believes it is running.
        jobs.Transition("job-1", JobStatus.Cancelled, running.Version, JobFailureCategory.Cancellation);

        // The handle must still lose: it cannot publish a Dry Run over a job somebody else already finished.
        Assert.Throws<InvalidOperationException>(() => claim.PublishDryRun("{}"));
    }

    private static string Now() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
