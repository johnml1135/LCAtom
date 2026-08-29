using SIL.Motif.Worker.Store;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class JobArchiveCapTests
{
    [Fact]
    public void FiveHundredOneFinishedJobsLeaveFiveHundredOldestDropped()
    {
        using var fixture = new Fixture();
        var ids = new List<string>();
        for (var i = 0; i < 501; i++)
        {
            var id = "job-" + i.ToString("D4");
            ids.Add(id);
            Complete(fixture.Jobs, id);
        }
        // Give every row a distinct, ordered ArchivedUtc so "oldest" is unambiguous.
        for (var i = 0; i < ids.Count; i++)
            SetArchive(fixture.Database, ids[i], "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z",
                DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddMinutes(i).ToString("O"));

        var purged = fixture.Jobs.PurgeArchived(ArchivePolicy.Default);

        Assert.Equal(1, purged);
        var remaining = fixture.Jobs.ListArchived().Select(job => job.JobId).ToArray();
        Assert.Equal(500, remaining.Length);
        Assert.DoesNotContain(ids[0], remaining);
        Assert.All(ids.Skip(1), id => Assert.Contains(id, remaining));
    }

    [Fact]
    public void ActiveJobsAreNeverPurgedRegardlessOfCount()
    {
        using var fixture = new Fixture();
        var active = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            var id = "active-" + i.ToString("D3");
            active.Add(id);
            fixture.Jobs.Create(new JobRecord(id, "project", "dry-run", JobStatus.Queued, 1,
                "{}", null, "2026-08-23T11:00:00Z", "2026-08-23T11:00:00Z"));
        }
        var running = fixture.Jobs.Create(new JobRecord("running-1", "project", "dry-run", JobStatus.Queued, 1,
            "{}", null, "2026-08-23T11:00:00Z", "2026-08-23T11:00:00Z"));
        fixture.Jobs.Transition(running.JobId, JobStatus.Running, running.Version);

        var purged = fixture.Jobs.PurgeArchived(new ArchivePolicy(0));

        Assert.Equal(0, purged);
        Assert.All(active, id => Assert.NotNull(fixture.Jobs.Get(id)));
        Assert.NotNull(fixture.Jobs.Get("running-1"));
    }

    [Fact]
    public void LiveLaterAttemptKeepsEarlier()
    {
        using var fixture = new Fixture();
        var failed = Fail(fixture.Jobs, "pinned");
        SetArchive(fixture.Database, failed.JobId, "2026-07-24T12:00:00+00:00", "2026-07-24T12:00:00+00:00",
            "2026-07-24T12:00:00+00:00");
        var retry = fixture.Jobs.RetryInfrastructure(failed.JobId, failed.Version,
            DateTimeOffset.Parse("2026-08-23T12:00:00Z"), "pinned-retry");
        var ordinary = Complete(fixture.Jobs, "ordinary");
        SetArchive(fixture.Database, ordinary.JobId, "2026-07-24T12:00:00Z", "2026-07-24T12:00:00Z",
            "2026-07-24T12:00:00Z");
        _ = retry;

        // A retained count of zero would purge every finished row except one pinned by a live later attempt.
        var eligible = fixture.Jobs.ListEligibleArchived(new ArchivePolicy(0)).Select(job => job.JobId).ToArray();

        Assert.Contains(ordinary.JobId, eligible);
        Assert.DoesNotContain(failed.JobId, eligible);
        Assert.Equal(1, fixture.Jobs.PurgeArchived(new ArchivePolicy(0)));
        Assert.Null(fixture.Jobs.Get(ordinary.JobId));
        Assert.NotNull(fixture.Jobs.Get(failed.JobId));
        Assert.NotNull(fixture.Jobs.Get(retry.JobId));
    }

    private static JobRecord Complete(JobRepository jobs, string id)
    {
        var queued = jobs.Create(new JobRecord(id, "project", "dry-run", JobStatus.Queued, 1,
            "{}", null, "2026-08-23T11:00:00Z", "2026-08-23T11:00:00Z"));
        var running = jobs.Transition(queued.JobId, JobStatus.Running, queued.Version);
        return jobs.Transition(running.JobId, JobStatus.Completed, running.Version);
    }

    private static JobRecord Fail(JobRepository jobs, string id)
    {
        var queued = jobs.Create(new JobRecord(id, "project", "dry-run", JobStatus.Queued, 1,
            "{}", null, "2026-08-23T11:00:00Z", "2026-08-23T11:00:00Z"));
        var running = jobs.Transition(queued.JobId, JobStatus.Running, queued.Version);
        return jobs.Transition(running.JobId, JobStatus.Failed, running.Version,
            JobFailureCategory.Infrastructure, "{\"error\":true}");
    }

    // Mixed "+00:00"/"Z" spellings, matched against ValidateUtc's own acceptance of both.
    private static void SetArchive(MotifDatabase database, string jobId, string createdUtc, string updatedUtc,
        string archivedUtc)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Jobs SET CreatedUtc = $created, UpdatedUtc = $updated, ArchivedUtc = $archived " +
            "WHERE JobId = $id;";
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$created", createdUtc);
        command.Parameters.AddWithValue("$updated", updatedUtc);
        command.Parameters.AddWithValue("$archived", archivedUtc);
        command.ExecuteNonQuery();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-job-cap-" + Guid.NewGuid().ToString("N"));
        public MotifDatabase Database { get; }
        public JobRepository Jobs { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var locator = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
            Database = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), locator,
                MotifSchema.CurrentSchema, new Version(1, 0));
            Jobs = new JobRepository(Database);
        }

        public void Dispose()
        {
            Database.Dispose();
            try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
        }
    }
}
