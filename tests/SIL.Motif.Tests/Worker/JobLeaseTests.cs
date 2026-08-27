using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Jobs;
using Xunit;

namespace SIL.Motif.Tests.Worker;

/// <summary>
/// Covers claiming a queued job when more than one process can reach the database.
/// </summary>
/// <remarks>
/// These drive two <see cref="JobRepository"/> instances over one database file rather than two OS
/// processes. The claim's atomicity comes from SQLite's single write lock, which is a property of the
/// file and not of the process, so two connections exercise the same contention two executables would;
/// what they do not cover is a runner killed mid-claim, which needs the process-level harness.
/// </remarks>
public sealed class JobLeaseTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "motif-lease-" + Guid.NewGuid().ToString("N"));
    private readonly MotifDatabase _database;
    private const string Project = "project-key";

    public JobLeaseTests()
    {
        Directory.CreateDirectory(_root);
        var locator = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        _database = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), locator,
            MotifSchema.CurrentSchema, new Version(1, 0));
    }

    [Fact]
    public void ExactlyOneOfTwoClaimantsWinsOneQueuedJob()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "dry-run", "{}", Now());

        var first = new JobRepository(_database).ClaimNext(Project, "runner-a", Now(), Lease());
        var second = new JobRepository(_database).ClaimNext(Project, "runner-b", Now(), Lease());

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal("runner-a", first!.OwnerId);
        Assert.Equal(JobStatus.Running, first.Status);
        Assert.False(string.IsNullOrWhiteSpace(first.ClaimToken));
    }

    [Fact]
    public void AClaimedJobIsInvisibleToOtherClaimantsUntilItsLeaseExpires()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "dry-run", "{}", Now());
        var claimed = jobs.ClaimNext(Project, "runner-a", Now(), Lease())!;

        Assert.Null(jobs.ClaimNext(Project, "runner-b", Now(), Lease()));

        var afterExpiry = jobs.ClaimNext(Project, "runner-b", Stamp(DateTimeOffset.UtcNow.AddMinutes(10)),
            Lease());
        Assert.NotNull(afterExpiry);
        Assert.Equal("runner-b", afterExpiry!.OwnerId);
        // A reclaim is a fresh attempt, so a job that wedges repeatedly exhausts its attempts.
        Assert.Equal(claimed.Attempt + 1, afterExpiry.Attempt);
    }

    [Fact]
    public void AHeartbeatPushesTheLeaseForwardAndKeepsTheJobHeld()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "dry-run", "{}", Now());
        var claimed = jobs.ClaimNext(Project, "runner-a", Now(), Lease())!;
        var later = DateTimeOffset.UtcNow.AddMinutes(10);

        Assert.True(jobs.Heartbeat(claimed.JobId, claimed.ClaimToken!, Stamp(later), Lease()));

        Assert.Null(jobs.ClaimNext(Project, "runner-b", Stamp(later.AddSeconds(1)), Lease()));
    }

    [Fact]
    public void AStaleOwnerCannotHeartbeatOrFinishAJobReassignedAwayFromIt()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", Project, "dry-run", "{}", Now());
        var stale = jobs.ClaimNext(Project, "runner-a", Now(), Lease())!;
        var reclaimed = jobs.ClaimNext(Project, "runner-a", Stamp(DateTimeOffset.UtcNow.AddMinutes(10)),
            Lease())!;

        // Same process, same OwnerId — only the token distinguishes the life that lost the row.
        Assert.False(jobs.Heartbeat(stale.JobId, stale.ClaimToken!, Now(), Lease()));
        Assert.NotEqual(stale.ClaimToken, reclaimed.ClaimToken);
        Assert.True(jobs.Heartbeat(reclaimed.JobId, reclaimed.ClaimToken!, Now(), Lease()));
    }

    [Fact]
    public void CreatingAQueuedJobWithARetryScheduleIsRefused()
    {
        var jobs = new JobRepository(_database);

        // A not-before belongs to the retry path, not to whoever writes the row.
        Assert.Throws<ArgumentException>(() => jobs.Create(new JobRecord("job-1", Project, "dry-run",
            JobStatus.Queued, 1, "{}", null, Now(), Now(),
            NotBeforeUtc: Stamp(DateTimeOffset.UtcNow.AddMinutes(10)))));
    }

    [Fact]
    public void ClaimingIgnoresQueuedWorkBelongingToAnotherProject()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("job-1", "another-project", "dry-run", "{}", Now());

        Assert.Null(jobs.ClaimNext(Project, "runner-a", Now(), Lease()));
    }

    [Fact]
    public void TheStartupSweepLeavesAnotherRunnersJobsAlone()
    {
        var jobs = new JobRepository(_database);
        jobs.Create("mine", Project, "dry-run", "{}", Now());
        var mine = jobs.ClaimNext(Project, "runner-a", Now(), Lease())!;
        jobs.Create("theirs", Project, "dry-run", "{}", Now());
        var theirs = jobs.ClaimNext(Project, "runner-b", Now(), Lease())!;

        var swept = jobs.MarkRunningInterrupted(DateTimeOffset.UtcNow, ownerId: "runner-a");

        Assert.Equal(new[] { mine.JobId }, swept.Select(job => job.JobId));
        // A live runner's row is not a starting process's business; lease expiry handles a dead one.
        Assert.Equal(JobStatus.Running, jobs.Get(theirs.JobId)!.Status);
    }

    private static TimeSpan Lease() => TimeSpan.FromMinutes(5);

    private static string Now() => Stamp(DateTimeOffset.UtcNow);

    private static string Stamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
