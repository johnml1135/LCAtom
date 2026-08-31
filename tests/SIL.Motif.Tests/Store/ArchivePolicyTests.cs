using SIL.Motif.Worker.Store;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using Xunit;

namespace SIL.Motif.Tests.Store;

public sealed class ArchivePolicyTests
{
    [Fact]
    public void DefaultPolicyPurgesOnlyAfterThirtyDaysFromArchiveTimestamp()
    {
        var policy = TimeRetentionPolicy.Default;
        var archived = DateTimeOffset.Parse("2026-07-23T12:00:00Z");
        Assert.False(policy.ShouldPurge(archived, DateTimeOffset.Parse("2026-08-22T11:59:59Z")));
        Assert.True(policy.ShouldPurge(archived, DateTimeOffset.Parse("2026-08-22T12:00:00Z")));
    }

    [Fact]
    public void ForeverAndNonterminalStateNeverPurges()
    {
        Assert.False(new TimeRetentionPolicy(TimeSpan.Zero, true).ShouldPurge(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.False(TimeRetentionPolicy.Default.ShouldPurge(null, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void TerminalProposalArchivesImmediatelyAndAppliedIndexSurvivesManualPurge()
    {
        var root = Path.Combine(Path.GetTempPath(), "motif-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var locator = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
            using var database = MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), locator,
                MotifSchema.CurrentSchema, new Version(1, 0));
            var id = CanonicalId.Mint("proposal/");
            var repository = new ProposalRepository(database,
                new FixedClock("2026-08-23T12:00:00Z"));
            repository.SaveRevision(new ProposalRevisionRecord(id, "sha256:archive",
                "{\"proposalId\":\"" + id.Value + "\"}", "applied", null, null, null));
            Assert.Equal("2026-08-23T12:00:00.0000000+00:00", repository.Get(id).ArchivedUtc);
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO AppliedIndex (ProposalId, IntentDigest, AppliedUtc) " +
                    "VALUES ($id, $digest, $utc);";
                command.Parameters.AddWithValue("$id", id.Value);
                command.Parameters.AddWithValue("$digest", "sha256:archive");
                command.Parameters.AddWithValue("$utc", "2026-08-23T12:00:00Z");
                command.ExecuteNonQuery();
            }
            repository.DeleteArchived(id);
            using var reopened = database.OpenConnection();
            using var count = reopened.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM AppliedIndex WHERE ProposalId = $id;";
            count.Parameters.AddWithValue("$id", id.Value);
            Assert.Equal(1L, count.ExecuteScalar());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void AllTerminalProposalStatesLeaveActiveListsImmediatelyAndRespectRetention()
    {
        var root = Path.Combine(Path.GetTempPath(), "motif-proposal-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var locator = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
            using var database = MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), locator,
                MotifSchema.CurrentSchema, new Version(1, 0));
            var now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
            var repository = new ProposalRepository(database, new FixedClock("2026-08-23T12:00:00Z"));
            var archived = new List<CanonicalId>();
            foreach (var status in new[] { "applied", "rejected", "superseded", "withdrawn" })
            {
                var id = CanonicalId.Mint("proposal/");
                archived.Add(id);
                repository.SaveRevision(new ProposalRevisionRecord(id, "sha256:" + status,
                    "{}", status, null, null, null));
            }
            var stale = CanonicalId.Mint("proposal/");
            repository.SaveRevision(new ProposalRevisionRecord(stale, "sha256:stale", "{}", "proposed",
                null, null, null, "2020-01-01T00:00:00Z"));

            var active = repository.List(new ProposalListFilter());
            Assert.Single(active);
            Assert.Equal(stale, active[0].ProposalId);
            Assert.Equal(5, repository.List(new ProposalListFilter(IncludeArchived: true)).Count);
            Assert.Equal(4, repository.ListArchived(now, new TimeRetentionPolicy(TimeSpan.Zero)).Count);
            Assert.Empty(repository.ListArchived(now, TimeRetentionPolicy.Default));
            Assert.Throws<InvalidOperationException>(() => repository.DeleteArchived(stale));
            foreach (var id in archived) repository.DeleteArchived(id);
            Assert.Single(repository.List(new ProposalListFilter(IncludeArchived: true)));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public void ReadyRetryComparisonUsesUtcInstantsAndNotBeforeFollowsCreatedUtc()
    {
        var root = Path.Combine(Path.GetTempPath(), "motif-job-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var locator = new ProjectLocator(Path.Combine(root, "project.fwdata"), "project");
            using var database = MotifDatabase.OpenOwned(Path.Combine(root, "project.motif.db"), locator,
                MotifSchema.CurrentSchema, new Version(1, 0));
            var clock = new FixedClock("2026-08-23T12:00:00Z");
            var jobs = new JobRepository(database, clock);
            var queued = jobs.Create(new JobRecord("ready", "project", "dry-run", JobStatus.Queued, 1,
                "{}", null, "2026-08-23T11:00:00Z", "2026-08-23T11:00:00Z"));
            var running = jobs.Transition(queued.JobId, JobStatus.Running, queued.Version);
            var failed = jobs.Transition(running.JobId, JobStatus.Failed, running.Version,
                JobFailureCategory.Infrastructure, "{\"error\":true}");
            var retry = jobs.RetryInfrastructure(failed.JobId, failed.Version,
                DateTimeOffset.Parse("2026-08-23T10:00:00Z"), "ready-retry");
            Assert.True(DateTimeOffset.Parse(retry.NotBeforeUtc!) >= DateTimeOffset.Parse(retry.CreatedUtc));
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE Jobs SET NotBeforeUtc = '2026-08-23T12:00:00+00:00' WHERE JobId = 'ready-retry';";
                command.ExecuteNonQuery();
            }
            Assert.Contains(retry.JobId, jobs.ListAttemptsReady(DateTimeOffset.Parse("2026-08-23T12:00:00Z"))
                .Select(x => x.JobId));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class FixedClock(string value) : SIL.Motif.Contract.Jobs.IJobClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse(value);
    }
}
