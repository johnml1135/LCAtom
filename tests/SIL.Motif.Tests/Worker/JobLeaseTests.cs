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
/// These drive two <see cref="JobClaims"/> instances over one database file rather than two OS
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

        var first = new JobClaims(_database).Claim(Project, "runner-a", Now(), Lease());
        var second = new JobClaims(_database).Claim(Project, "runner-b", Now(), Lease());

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
        var claims = new JobClaims(_database);
        jobs.Create("job-1", Project, "dry-run", "{}", Now());
        var claimed = claims.Claim(Project, "runner-a", Now(), Lease())!;

        Assert.Null(claims.Claim(Project, "runner-b", Now(), Lease()));

        var afterExpiry = claims.Claim(Project, "runner-b", Stamp(DateTimeOffset.UtcNow.AddMinutes(10)),
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
        var claims = new JobClaims(_database);
        jobs.Create("job-1", Project, "dry-run", "{}", Now());
        var claimed = claims.Claim(Project, "runner-a", Now(), Lease())!;
        var later = DateTimeOffset.UtcNow.AddMinutes(10);

        Assert.True(claims.Renew(claimed.JobId, claimed.ClaimToken!, Stamp(later), Lease()));

        Assert.Null(claims.Claim(Project, "runner-b", Stamp(later.AddSeconds(1)), Lease()));
    }

    [Fact]
    public void AStaleOwnerCannotHeartbeatOrFinishAJobReassignedAwayFromIt()
    {
        var jobs = new JobRepository(_database);
        var claims = new JobClaims(_database);
        jobs.Create("job-1", Project, "dry-run", "{}", Now());
        var stale = claims.Claim(Project, "runner-a", Now(), Lease())!;
        var reclaimed = claims.Claim(Project, "runner-a", Stamp(DateTimeOffset.UtcNow.AddMinutes(10)),
            Lease())!;

        // Same process, same OwnerId — only the token distinguishes the life that lost the row.
        Assert.False(claims.Renew(stale.JobId, stale.ClaimToken!, Now(), Lease()));
        Assert.NotEqual(stale.ClaimToken, reclaimed.ClaimToken);
        Assert.True(claims.Renew(reclaimed.JobId, reclaimed.ClaimToken!, Now(), Lease()));

        // The row the stale runner is about to report on is running for somebody else.
        Assert.False(claims.Finish(stale.JobId, stale.ClaimToken!, JobStatus.Completed,
            JobFailureCategory.None, null));
        Assert.Equal(JobStatus.Running, jobs.Get(stale.JobId)!.Status);
        Assert.True(claims.Finish(reclaimed.JobId, reclaimed.ClaimToken!, JobStatus.Completed,
            JobFailureCategory.None, null));
        Assert.Equal(JobStatus.Completed, jobs.Get(reclaimed.JobId)!.Status);
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
        var claims = new JobClaims(_database);
        jobs.Create("job-1", "another-project", "dry-run", "{}", Now());

        Assert.Null(claims.Claim(Project, "runner-a", Now(), Lease()));
    }

    [Fact]
    public void TheStartupSweepLeavesAnotherRunnersJobsAlone()
    {
        var jobs = new JobRepository(_database);
        var claims = new JobClaims(_database);
        jobs.Create("mine", Project, "dry-run", "{}", Now());
        var mine = claims.Claim(Project, "runner-a", Now(), Lease())!;
        jobs.Create("theirs", Project, "dry-run", "{}", Now());
        var theirs = claims.Claim(Project, "runner-b", Now(), Lease())!;

        var swept = jobs.MarkRunningInterrupted(DateTimeOffset.UtcNow, ownerId: "runner-a");

        Assert.Equal(new[] { mine.JobId }, swept.Select(job => job.JobId));
        // A live runner's row is not a starting process's business; lease expiry handles a dead one.
        Assert.Equal(JobStatus.Running, jobs.Get(theirs.JobId)!.Status);
    }

    [Fact]
    public void EveryCreatedJobCarriesAQueueOrderThatNeverInvertsCreationOrder()
    {
        var jobs = new JobRepository(_database);
        var created = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var id = "job-" + i;
            jobs.Create(id, Project, "dry-run", "{}", Now());
            created.Add(id);
        }

        var orders = QueueOrders();
        // A tie is possible and harmless; an inversion is not, because the claim reads this column.
        Assert.All(orders.Values, order => Assert.True(order > 0, "QueueOrder was not populated."));
        for (var i = 1; i < created.Count; i++)
            Assert.True(orders[created[i]] >= orders[created[i - 1]],
                $"{created[i]} sorts before {created[i - 1]} despite being created after it.");
    }

    private Dictionary<string, double> QueueOrders()
    {
        var orders = new Dictionary<string, double>(StringComparer.Ordinal);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT JobId, QueueOrder FROM Jobs;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) orders[reader.GetString(0)] = reader.GetDouble(1);
        return orders;
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
