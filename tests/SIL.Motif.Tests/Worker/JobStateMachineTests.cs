using System.Text.Json;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Jobs;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class JobStateMachineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-jobs-" + Guid.NewGuid().ToString("N"));

    public JobStateMachineTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void StatusesHaveClosedWireSpellingAndRejectScalarJson()
    {
        Assert.Equal("\"waiting-for-baseline\"", JsonSerializer.Serialize(JobStatus.WaitingForBaseline));
        Assert.Equal(JobStatus.WaitingForProjectHost,
            JsonSerializer.Deserialize<JobStatus>("\"waiting-for-project-host\""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JobStatus>("\"future\""));
        Assert.Throws<ArgumentException>(() => JobJson.ValidateStructured("1", "input"));
    }

    [Fact]
    public void WaitingStatesMustReturnToQueueBeforeRunningAndTerminalsDoNotReopen()
    {
        var machine = new JobStateMachine(new FixedClock("2026-08-22T12:00:00Z"));
        var job = NewJob();
        job = machine.Transition(job, JobStatus.WaitingForBaseline);
        Assert.Throws<InvalidOperationException>(() => machine.Transition(job, JobStatus.Running));
        job = machine.Transition(job, JobStatus.Queued);
        job = machine.Transition(job, JobStatus.Running);
        job = machine.Transition(job, JobStatus.Completed, "{\"ok\":true}");
        Assert.Throws<InvalidOperationException>(() => machine.Transition(job, JobStatus.Queued));
        Assert.Throws<InvalidOperationException>(() => machine.Transition(job, JobStatus.Completed));
    }

    [Fact]
    public void EveryStatusPairMatchesTheClosedTransitionTable()
    {
        var machine = new JobStateMachine(new FixedClock("2026-08-22T12:00:00Z"));
        foreach (var from in Enum.GetValues<JobStatus>())
        foreach (var to in Enum.GetValues<JobStatus>())
        {
            var assessmentOutcome = from == JobStatus.Running &&
                (to is JobStatus.CompletedDryRunOnly or JobStatus.CompletedWithAssessmentFailure);
            var current = NewJob() with { Status = from, Version = 4, DryRunPublished = assessmentOutcome,
                DryRunJson = assessmentOutcome ? "{\"dryRun\":true}" : null };
            var legal = JobStateMachine.LegalNextStatuses(from).Contains(to);
            if (from == to || !legal)
                Assert.Throws<InvalidOperationException>(() => machine.Transition(current, to));
            else
                Assert.Equal(to, machine.Transition(current, to).Status);
        }
    }

    [Fact]
    public void CancellationRequestIsIdempotentAndPublishedDryRunResultSurvivesCancellation()
    {
        var machine = new JobStateMachine(new FixedClock("2026-08-22T12:00:00Z"));
        var job = NewJob() with { Status = JobStatus.Running };
        job = machine.PublishDryRun(job, "{\"dryRun\":true}");
        job = machine.RequestCancellation(job);
        job = machine.RequestCancellation(job);
        Assert.True(job.CancellationRequested);
        Assert.Throws<InvalidOperationException>(() => machine.Transition(job, JobStatus.Completed));
        job = machine.UpdateProgress(job, "{\"assessmentProgress\":50}");
        Assert.Equal("{\"assessmentProgress\":50}", job.ProgressJson);
        job = machine.Transition(job, JobStatus.Cancelled);
        Assert.Equal("{\"assessmentDisposition\":\"cancelled\"}", job.ResultJson);
        Assert.Equal("{\"dryRun\":true}", job.DryRunJson);
        Assert.True(job.DryRunPublished);
        Assert.Throws<InvalidOperationException>(() => machine.RequestCancellation(job));
        Assert.Throws<InvalidOperationException>(() => machine.Transition(
            NewJob() with { Status = JobStatus.Running, DryRunPublished = true, DryRunJson = "{\"dryRun\":true}" },
            JobStatus.Cancelled, "{\"assessmentDisposition\":\"wrong\"}"));
    }

    [Fact]
    public void AssessmentOutcomeStatusesRequirePublishedDryRun()
    {
        var machine = new JobStateMachine(new FixedClock("2026-08-22T12:00:00Z"));
        var running = NewJob() with { Status = JobStatus.Running };
        Assert.Throws<InvalidOperationException>(() => machine.Transition(running, JobStatus.CompletedDryRunOnly));
        Assert.Throws<InvalidOperationException>(() => machine.Transition(running, JobStatus.CompletedWithAssessmentFailure));
        var published = machine.PublishDryRun(running, "{\"dryRun\":true}");
        Assert.Equal(JobStatus.CompletedDryRunOnly, machine.Transition(published, JobStatus.CompletedDryRunOnly).Status);
    }

    [Fact]
    public void DryRunPublicationCannotFollowCancellationOrOverwriteExistingPublication()
    {
        var machine = new JobStateMachine(new FixedClock("2026-08-22T12:00:00Z"));
        var cancelled = machine.RequestCancellation(NewJob() with { Status = JobStatus.Running });
        Assert.Throws<InvalidOperationException>(() => machine.PublishDryRun(cancelled, "{\"dryRun\":true}"));
        var published = machine.PublishDryRun(NewJob() with { Status = JobStatus.Running }, "{\"first\":true}");
        Assert.Throws<InvalidOperationException>(() => machine.PublishDryRun(published, "{\"second\":true}"));
    }

    [Fact]
    public void RetryCreatesNewLineageAttemptWithoutChangingTerminalHistory()
    {
        var machine = new JobStateMachine(new FixedClock("2026-08-22T12:00:00Z"));
        var terminal = NewJob() with { Status = JobStatus.Failed, Attempt = 2, LineageId = "lineage" };
        var retry = machine.Retry(terminal, "retry-id");
        Assert.Equal("retry-id", retry.JobId);
        Assert.Equal("lineage", retry.LineageId);
        Assert.Equal(3, retry.Attempt);
        Assert.Equal(JobStatus.Queued, retry.Status);
        Assert.Equal(JobStatus.Failed, terminal.Status);
        Assert.Equal("{\"proposal\":[]}", retry.InputJson);
        Assert.Null(retry.ResultJson);
    }

    [Fact]
    public void RepositoryPersistsTransitionsAndRejectsStaleWritersAfterReopen()
    {
        var project = new ProjectLocator(Path.Combine(_root, "project.fwdata"), "project");
        var path = Path.Combine(_root, "project.motif.db");
        JobRecord created;
        using (var database = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0)))
        {
            var repository = new JobRepository(database, new FixedClock("2026-08-22T12:00:00Z"));
            created = repository.Create(NewJob());
            var first = repository.Transition(created.JobId, JobStatus.Running, created.Version);
            var cancellation = repository.RequestCancellation(first.JobId, first.Version);
            Assert.True(cancellation.CancellationRequested);
            Assert.Throws<InvalidOperationException>(() => repository.Transition(created.JobId, JobStatus.Cancelled, created.Version));
            Assert.Equal(2, cancellation.Version);
        }

        using var reopened = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0));
        var reopenedJob = new JobRepository(reopened, new FixedClock("2026-08-22T12:01:00Z")).Get(created.JobId);
        Assert.NotNull(reopenedJob);
        Assert.Equal(JobStatus.Running, reopenedJob!.Status);
        Assert.True(reopenedJob.CancellationRequested);
        Assert.Equal(2, reopenedJob.Version);
    }

    [Fact]
    public void RepositoryKeepsPublishedDryRunImmutableWhileProgressAndCancellationChange()
    {
        var project = new ProjectLocator(Path.Combine(_root, "immutable.fwdata"), "immutable");
        var path = Path.Combine(_root, "immutable.motif.db");
        using (var database = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0)))
        {
            var repository = new JobRepository(database, new FixedClock("2026-08-22T12:00:00Z"));
            var running = repository.Transition(repository.Create(NewJob()).JobId, JobStatus.Running);
            var published = repository.PublishDryRun(running.JobId, "{\"dryRun\":true}", running.Version);
            var progressed = repository.UpdateProgress(published.JobId, "{\"assessmentProgress\":50}", published.Version);
            Assert.Equal("{\"dryRun\":true}", progressed.DryRunJson);
            Assert.Equal("{\"assessmentProgress\":50}", progressed.ProgressJson);
            var cancelled = repository.RequestCancellation(progressed.JobId, progressed.Version);
            var completed = repository.Transition(cancelled.JobId, JobStatus.Cancelled, cancelled.Version);
            Assert.Equal("{\"dryRun\":true}", completed.DryRunJson);
            Assert.Equal("{\"assessmentProgress\":50}", completed.ProgressJson);
            Assert.Equal("{\"assessmentDisposition\":\"cancelled\"}", completed.ResultJson);
        }
        using var reopened = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0));
        var durable = new JobRepository(reopened).Get("job");
        Assert.Equal("{\"dryRun\":true}", durable!.DryRunJson);
        Assert.Equal("{\"assessmentProgress\":50}", durable.ProgressJson);
        Assert.Equal("{\"assessmentDisposition\":\"cancelled\"}", durable.ResultJson);
    }

    [Fact]
    public void QueuedCancellationRequestSurvivesReopen()
    {
        var project = new ProjectLocator(Path.Combine(_root, "queued-cancel.fwdata"), "queued-cancel");
        var path = Path.Combine(_root, "queued-cancel.motif.db");
        using (var database = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0)))
        {
            var repository = new JobRepository(database, new FixedClock("2026-08-22T12:00:00Z"));
            var created = repository.Create(NewJob());
            repository.RequestCancellation(created.JobId, created.Version);
        }
        using var reopened = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0));
        var job = new JobRepository(reopened).Get("job");
        Assert.True(job!.CancellationRequested);
        Assert.Equal(JobStatus.Queued, job.Status);
    }

    [Fact]
    public void MalformedPersistedJsonAndCrossFieldRowsAreRejected()
    {
        var project = new ProjectLocator(Path.Combine(_root, "malformed-job.fwdata"), "malformed-job");
        var path = Path.Combine(_root, "malformed-job.motif.db");
        using var database = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0));
        var repository = new JobRepository(database);
        repository.Create(NewJob());
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Jobs SET InputJson = 'true';";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => repository.Get("job"));
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Jobs SET InputJson = '{\"proposal\":[]}', CancellationRequested = 1, Status = 'completed';";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => repository.Get("job"));
    }

    [Fact]
    public void GenerationFourValidationRejectsMissingUniqueJobIndexAndRollsBackFailedMigration()
    {
        var project = new ProjectLocator(Path.Combine(_root, "migration-check.fwdata"), "migration-check");
        var path = Path.Combine(_root, "migration-check.motif.db");
        using (MotifDatabase.OpenOwned(path, project, 3, new Version(1, 0))) { }
        Assert.Throws<InvalidOperationException>(() => MotifDatabase.OpenOwnedForTesting(path, project,
            MotifSchema.CurrentSchema, new Version(1, 0), generation =>
            {
                if (generation == 4) throw new InvalidOperationException("injected migration failure");
            }));
        using (MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0))) { }
        using (var database = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0)))
        using (var connection = database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP INDEX IX_Jobs_Lineage_Attempt;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0)));
    }

    [Fact]
    public void RejectsApplyJobsAndPinsJsonBoundaries()
    {
        var project = new ProjectLocator(Path.Combine(_root, "apply.fwdata"), "apply");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "apply.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var repository = new JobRepository(database, new FixedClock("2026-08-22T12:00:00Z"));
        Assert.Throws<InvalidOperationException>(() => repository.Create(NewJob() with { Kind = JobKinds.Apply }));
        Assert.Throws<ArgumentException>(() => repository.Create(NewJob() with { InputJson = "true" }));
        Assert.Throws<ArgumentException>(() => repository.Create(NewJob() with { DryRunJson = "{\"dryRun\":true}" }));
        var oversized = "{\"x\":\"" + new string('a', JobJson.MaxStructuredJsonUtf8Bytes) + "\"}";
        Assert.Throws<ArgumentException>(() => repository.Create(NewJob() with { InputJson = oversized }));
    }

    [Fact]
    public void TimestampsRemainUtcAndUpdatedIsMonotonic()
    {
        var machine = new JobStateMachine(new FixedClock("2026-08-22T10:00:00Z"));
        var current = NewJob() with { UpdatedUtc = "2026-08-22T11:00:00Z" };
        var changed = machine.Transition(current, JobStatus.Running);
        Assert.Equal(DateTimeOffset.Parse(current.UpdatedUtc), DateTimeOffset.Parse(changed.UpdatedUtc));
        Assert.Throws<ArgumentException>(() => machine.Transition(current with { CreatedUtc = "2026-08-22T11:00:00-04:00" }, JobStatus.Running));
        Assert.Throws<ArgumentException>(() => machine.Transition(current with { CreatedUtc = "2026-08-22T11:00:00" }, JobStatus.Running));
    }

    [Fact]
    public void JobsSchemaIsMigratedWithExpectedColumnsAndIndexes()
    {
        var project = new ProjectLocator(Path.Combine(_root, "schema.fwdata"), "schema");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "schema.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        using var connection = database.OpenConnection();
        using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('Jobs') ORDER BY cid;";
        using var reader = columns.ExecuteReader();
        var shapes = new List<string>();
        while (reader.Read()) shapes.Add(string.Join("|", reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            reader.IsDBNull(3) ? "" : reader.GetString(3), reader.GetInt32(4)));
        Assert.Equal(new[] { "JobId|TEXT|0||1", "ProjectKey|TEXT|1||0", "Kind|TEXT|1||0", "Status|TEXT|1||0",
            "Attempt|INTEGER|1|1|0", "LineageId|TEXT|1||0", "InputJson|TEXT|1||0", "ResultJson|TEXT|0||0",
            "ProgressJson|TEXT|0||0", "DryRunJson|TEXT|0||0", "CancellationRequested|INTEGER|1|0|0", "CreatedUtc|TEXT|1||0",
            "UpdatedUtc|TEXT|1||0", "Version|INTEGER|1|0|0", "DryRunPublished|INTEGER|1|0|0" }, shapes);
        using var indexes = connection.CreateCommand();
        indexes.CommandText = "SELECT name, \"unique\" FROM pragma_index_list('Jobs') WHERE origin = 'c' ORDER BY name;";
        using var indexReader = indexes.ExecuteReader();
        var indexShapes = new List<string>();
        while (indexReader.Read()) indexShapes.Add(indexReader.GetString(0) + "|" + indexReader.GetInt32(1));
        Assert.Equal(new[] { "IX_Jobs_Lineage_Attempt|1", "IX_Jobs_Status_Updated|0" }, indexShapes);
        using var indexColumns = connection.CreateCommand();
        indexColumns.CommandText = "SELECT name, group_concat(column_name, ',') FROM (" +
            "SELECT i.name, ii.seqno, ii.name AS column_name FROM pragma_index_list('Jobs') i " +
            "JOIN pragma_index_info(i.name) ii WHERE i.origin = 'c' GROUP BY i.name, ii.seqno, ii.name) " +
            "GROUP BY name ORDER BY name;";
        using var indexColumnReader = indexColumns.ExecuteReader();
        var columnShapes = new List<string>();
        while (indexColumnReader.Read()) columnShapes.Add(indexColumnReader.GetString(0) + "|" + indexColumnReader.GetString(1));
        Assert.Equal(new[] { "IX_Jobs_Lineage_Attempt|LineageId,Attempt", "IX_Jobs_Status_Updated|Status,UpdatedUtc" }, columnShapes);
    }

    [Fact]
    public void SchemaThreeUpgradesToFourAndRetainsJobsAfterReopen()
    {
        var project = new ProjectLocator(Path.Combine(_root, "upgrade.fwdata"), "upgrade");
        var path = Path.Combine(_root, "upgrade.motif.db");
        using (MotifDatabase.OpenOwned(path, project, 3, new Version(1, 0))) { }
        using (var upgraded = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0)))
        {
            var repository = new JobRepository(upgraded, new FixedClock("2026-08-22T12:00:00Z"));
            repository.Create(NewJob() with { JobId = "upgrade-job" });
        }
        using var reopened = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0));
        Assert.NotNull(new JobRepository(reopened).Get("upgrade-job"));
    }

    [Fact]
    public void RepositoryRetryKeepsTerminalHistoryAndNewAttemptDurable()
    {
        var project = new ProjectLocator(Path.Combine(_root, "retry.fwdata"), "retry");
        var path = Path.Combine(_root, "retry.motif.db");
        string lineage;
        using (var database = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0)))
        {
            var repository = new JobRepository(database, new FixedClock("2026-08-22T12:00:00Z"));
            var created = repository.Create(NewJob() with { JobId = "attempt-1" });
            var failed = repository.Transition(created.JobId, JobStatus.Running, created.Version);
            failed = repository.Transition(failed.JobId, JobStatus.Failed, failed.Version, "{\"error\":true}");
            lineage = failed.LogicalJobId;
            var retry = repository.Retry(failed.JobId, failed.Version, "attempt-2");
            Assert.Equal(JobStatus.Failed, repository.Get("attempt-1")!.Status);
            Assert.Equal(2, retry.Attempt);
            Assert.Equal(2, repository.ListAttempts(lineage).Count);
        }
        using var reopened = MotifDatabase.OpenOwned(path, project, MotifSchema.CurrentSchema, new Version(1, 0));
        var attempts = new JobRepository(reopened).ListAttempts(lineage);
        Assert.Equal(new[] { "attempt-1", "attempt-2" }, attempts.Select(attempt => attempt.JobId));
    }

    private static JobRecord NewJob() => new("job", "project", "dry-run", JobStatus.Queued, 1, "{\"proposal\":[]}", null,
        "2026-08-22T11:00:00Z", "2026-08-22T11:00:00Z");

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
    }

    private sealed class FixedClock(string value) : IJobClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse(value);
    }
}
