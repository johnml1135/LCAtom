using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Jobs;

/// <summary>Persists job attempts with transactional optimistic concurrency.</summary>
public sealed class JobRepository
{
    private readonly MotifDatabase _database;
    private readonly JobStateMachine _stateMachine;
    private readonly IJobClock _clock;

    public JobRepository(MotifDatabase database, IJobClock? clock = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _clock = clock ?? new SystemJobClock();
        _stateMachine = new JobStateMachine(_clock);
    }

    public JobRecord Create(JobRecord requested)
    {
        var record = Normalize(requested);
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            Insert(connection, transaction, record);
            transaction.Commit();
            return record;
        }
        catch
        {
            try { transaction.Rollback(); } catch (SqliteException) { }
            throw;
        }
    }

    public JobRecord Create(string jobId, string projectKey, string kind, string inputJson, string createdUtc,
        string? lineageId = null) => Create(new JobRecord(jobId, projectKey, kind, JobStatus.Queued, 1, inputJson,
            null, createdUtc, createdUtc, null, lineageId));

    public JobRecord? Get(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("A job id is required.", nameof(jobId));
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE JobId = $id;";
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<JobRecord> ListAttempts(string lineageId)
    {
        if (string.IsNullOrWhiteSpace(lineageId)) throw new ArgumentException("A lineage id is required.", nameof(lineageId));
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE LineageId = $lineage ORDER BY Attempt;";
        command.Parameters.AddWithValue("$lineage", lineageId);
        using var reader = command.ExecuteReader();
        var records = new List<JobRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public bool HasLaterAttempt(string lineageId, int attempt)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM Jobs WHERE LineageId = $lineage AND Attempt > $attempt LIMIT 1;";
        command.Parameters.AddWithValue("$lineage", lineageId);
        command.Parameters.AddWithValue("$attempt", attempt);
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<JobRecord> ListActive(string? projectKey = null)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE Status NOT IN ('completed','completed-dry-run-only'," +
            "'completed-with-assessment-failure','failed','cancelled','interrupted')" +
            (projectKey is null ? "" : " AND ProjectKey = $project") + " ORDER BY UpdatedUtc;";
        if (projectKey is not null) command.Parameters.AddWithValue("$project", projectKey);
        using var reader = command.ExecuteReader();
        var records = new List<JobRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public IReadOnlyList<JobRecord> ListAttemptsReady(DateTimeOffset now)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE Status = 'queued' AND " +
            "(NotBeforeUtc IS NULL OR NotBeforeUtc <= $now) ORDER BY Attempt;";
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var records = new List<JobRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public JobRecord Transition(string jobId, JobStatus next, long expectedVersion, string? resultJson = null)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadRequired(connection, transaction, jobId);
        EnsureVersion(current, expectedVersion);
        var changed = PrepareTransition(current, _stateMachine.Transition(current, next, resultJson));
        UpdateTransition(connection, transaction, changed);
        transaction.Commit();
        return changed;
    }

    public JobRecord Transition(string jobId, JobStatus next, long expectedVersion,
        JobFailureCategory failureCategory, string? resultJson = null)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadRequired(connection, transaction, jobId);
        EnsureVersion(current, expectedVersion);
        var changed = PrepareTransition(current, _stateMachine.Transition(current, next, resultJson)) with
        {
            FailureCategory = failureCategory
        };
        ValidateFailureCategory(changed);
        UpdateTransition(connection, transaction, changed);
        transaction.Commit();
        return changed;
    }

    public JobRecord Transition(JobRecord current, JobStatus next, string? resultJson = null) =>
        Transition(current.JobId, next, current.Version, resultJson);

    public JobRecord Transition(string jobId, JobStatus next, string? resultJson = null)
    {
        var current = GetRequired(jobId);
        return Transition(jobId, next, current.Version, resultJson);
    }

    public JobRecord RequestCancellation(string jobId, long expectedVersion)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadRequired(connection, transaction, jobId);
        EnsureVersion(current, expectedVersion);
        var changed = _stateMachine.RequestCancellation(current);
        if (changed == current) return current;
        UpdateCancellation(connection, transaction, changed);
        transaction.Commit();
        return changed;
    }

    public JobRecord RequestCancellation(string jobId)
    {
        var current = GetRequired(jobId);
        return RequestCancellation(jobId, current.Version);
    }

    public JobRecord UpdateProgress(string jobId, string progressJson, long expectedVersion)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadRequired(connection, transaction, jobId);
        EnsureVersion(current, expectedVersion);
        var changed = _stateMachine.UpdateProgress(current, progressJson);
        UpdateProgressRow(connection, transaction, changed);
        transaction.Commit();
        return changed;
    }

    public JobRecord PublishDryRun(string jobId, string dryRunJson, long expectedVersion)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadRequired(connection, transaction, jobId);
        EnsureVersion(current, expectedVersion);
        var changed = _stateMachine.PublishDryRun(current, dryRunJson);
        UpdateDryRunRow(connection, transaction, changed);
        transaction.Commit();
        return changed;
    }

    public JobRecord Retry(string terminalJobId, long expectedVersion, string? newJobId = null)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var terminal = ReadRequired(connection, transaction, terminalJobId);
        EnsureVersion(terminal, expectedVersion);
        var retry = _stateMachine.Retry(terminal, newJobId ?? Guid.NewGuid().ToString("N"));
        Insert(connection, transaction, Normalize(retry));
        transaction.Commit();
        return retry;
    }

    public JobRecord RetryInfrastructure(string terminalJobId, long expectedVersion, DateTimeOffset now,
        string? newJobId = null)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var terminal = ReadRequired(connection, transaction, terminalJobId);
        EnsureVersion(terminal, expectedVersion);
        if (terminal.FailureCategory != JobFailureCategory.Infrastructure)
            throw new InvalidOperationException("Only infrastructure interruptions may be retried automatically.");
        if (terminal.CancellationRequested || terminal.Attempt >= 3)
            throw new InvalidOperationException("This job lineage is not eligible for automatic retry.");
        var retry = _stateMachine.Retry(terminal, newJobId ?? Guid.NewGuid().ToString("N"));
        var delay = TimeSpan.FromMinutes(terminal.Attempt);
        retry = retry with
        {
            FailureCategory = JobFailureCategory.None,
            NotBeforeUtc = now.ToUniversalTime().Add(delay).ToString("O", CultureInfo.InvariantCulture)
        };
        Insert(connection, transaction, Normalize(retry));
        transaction.Commit();
        return retry;
    }

    public IReadOnlyList<JobRecord> MarkRunningInterrupted(DateTimeOffset now)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectSql + " WHERE Status = 'running' ORDER BY JobId;";
        using var reader = command.ExecuteReader();
        var current = new List<JobRecord>();
        while (reader.Read()) current.Add(Read(reader));
        reader.Close();
        var changed = new List<JobRecord>(current.Count);
        foreach (var job in current)
        {
            var interrupted = PrepareTransition(job, _stateMachine.Transition(job, JobStatus.Interrupted));
            interrupted = interrupted with
            {
                UpdatedUtc = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ArchivedUtc = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                FailureCategory = job.CancellationRequested ? JobFailureCategory.Cancellation : JobFailureCategory.Infrastructure
            };
            UpdateTransition(connection, transaction, interrupted);
            changed.Add(interrupted);
        }
        transaction.Commit();
        return changed;
    }

    public IReadOnlyList<JobRecord> ListRetryableInterrupted()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE Status = 'interrupted' AND FailureCategory = 'infrastructure' " +
            "AND Attempt < 3 ORDER BY Attempt, JobId;";
        using var reader = command.ExecuteReader();
        var records = new List<JobRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public IReadOnlyList<JobRecord> ListArchived(DateTimeOffset? before = null)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE ArchivedUtc IS NOT NULL" +
            (before is null ? "" : " AND ArchivedUtc <= $before") + " ORDER BY ArchivedUtc;";
        if (before is not null) command.Parameters.AddWithValue("$before", before.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var records = new List<JobRecord>();
        while (reader.Read()) records.Add(Read(reader));
        return records;
    }

    public JobRecord Retry(string terminalJobId, string? newJobId = null)
    {
        var current = GetRequired(terminalJobId);
        return Retry(terminalJobId, current.Version, newJobId);
    }

    private JobRecord Normalize(JobRecord requested)
    {
        if (string.IsNullOrWhiteSpace(requested.JobId)) throw new ArgumentException("A job id is required.");
        if (string.IsNullOrWhiteSpace(requested.ProjectKey)) throw new ArgumentException("A project key is required.");
        var kind = requested.Kind.Trim();
        if (kind.Length == 0) throw new ArgumentException("A job kind is required.");
        if (string.Equals(kind, JobKinds.Apply, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Apply is synchronous and cannot be represented as a queued job.");
        if (requested.Status != JobStatus.Queued) throw new ArgumentException("New jobs must start queued.");
        if (requested.Attempt < 1) throw new ArgumentOutOfRangeException(nameof(requested.Attempt));
        if (requested.Version != 0) throw new ArgumentException("New jobs must start at version zero.");
        if (requested.ResultJson is not null || requested.ProgressJson is not null || requested.CancellationRequested ||
            requested.DryRunPublished || requested.DryRunJson is not null)
            throw new ArgumentException("A new queued job cannot contain execution state.");
        if (requested.FailureCategory != JobFailureCategory.None || requested.ArchivedUtc is not null)
            throw new ArgumentException("A new queued job cannot contain terminal state.");
        JobJson.ValidateStructured(requested.InputJson, nameof(requested.InputJson));
        var created = ValidateUtc(requested.CreatedUtc, nameof(requested.CreatedUtc));
        var updated = ValidateUtc(requested.UpdatedUtc, nameof(requested.UpdatedUtc));
        if (created > updated) throw new ArgumentException("CreatedUtc must not be later than UpdatedUtc.");
        var lineage = string.IsNullOrWhiteSpace(requested.LineageId) ? requested.JobId : requested.LineageId;
        return requested with { Kind = kind, LineageId = lineage };
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, JobRecord record)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Jobs
                (JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson, ProgressJson, DryRunJson,
                 CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished, FailureCategory,
                 NotBeforeUtc, ArchivedUtc)
            VALUES ($id, $project, $kind, $status, $attempt, $lineage, $input, $result, $progress,
                    $dryrun, $cancel, $created, $updated, $version, $published, $failure, $notBefore, $archived);
            """;
        AddParameters(command, record);
        command.ExecuteNonQuery();
    }

    private static void UpdateTransition(SqliteConnection connection, SqliteTransaction transaction, JobRecord record)
    {
        ExecuteConcurrencyUpdate(connection, transaction, record,
            "Status = $status, ResultJson = $result, FailureCategory = $failure, ArchivedUtc = $archived, " +
            "UpdatedUtc = $updated, Version = $version");
    }

    private static void UpdateCancellation(SqliteConnection connection, SqliteTransaction transaction, JobRecord record) =>
        ExecuteConcurrencyUpdate(connection, transaction, record,
            "CancellationRequested = $cancel, UpdatedUtc = $updated, Version = $version");

    private static void UpdateProgressRow(SqliteConnection connection, SqliteTransaction transaction, JobRecord record) =>
        ExecuteConcurrencyUpdate(connection, transaction, record,
            "ProgressJson = $progress, UpdatedUtc = $updated, Version = $version");

    private static void UpdateDryRunRow(SqliteConnection connection, SqliteTransaction transaction, JobRecord record) =>
        ExecuteConcurrencyUpdate(connection, transaction, record,
            "DryRunJson = $dryrun, DryRunPublished = $published, UpdatedUtc = $updated, Version = $version");

    private static void ExecuteConcurrencyUpdate(SqliteConnection connection, SqliteTransaction transaction,
        JobRecord record, string assignments)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE Jobs SET {assignments} WHERE JobId = $id AND Version = $expected;";
        AddParameters(command, record);
        command.Parameters.AddWithValue("$expected", record.Version - 1);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("The job changed concurrently; reload it before writing.");
    }

    private static readonly string SelectSql = "SELECT JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson, ProgressJson, DryRunJson, " +
        "CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished, FailureCategory, NotBeforeUtc, ArchivedUtc FROM Jobs";

    private static JobRecord ReadRequired(SqliteConnection connection, SqliteTransaction transaction, string jobId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectSql + " WHERE JobId = $id;";
        command.Parameters.AddWithValue("$id", jobId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException($"Job '{jobId}' was not found.");
        return Read(reader);
    }

    private JobRecord GetRequired(string jobId) => Get(jobId) ?? throw new KeyNotFoundException($"Job '{jobId}' was not found.");

    private static JobRecord Read(SqliteDataReader reader)
    {
        try
        {
            var jobId = reader.GetString(0);
            var projectKey = reader.GetString(1);
            var kind = reader.GetString(2);
            var status = JobStatusJson.Parse(reader.GetString(3));
            var attempt = reader.GetInt32(4);
            var lineage = reader.GetString(5);
            var input = reader.GetString(6);
            var result = reader.IsDBNull(7) ? null : reader.GetString(7);
            var progress = reader.IsDBNull(8) ? null : reader.GetString(8);
            var dryRun = reader.IsDBNull(9) ? null : reader.GetString(9);
            var cancellation = ReadBoolean(reader, 10, "CancellationRequested");
            var created = reader.GetString(11);
            var updated = reader.GetString(12);
            var version = reader.GetInt64(13);
            var published = ReadBoolean(reader, 14, "DryRunPublished");
            var failure = JobFailureCategoryJson.Parse(reader.GetString(15));
            var notBefore = reader.IsDBNull(16) ? null : reader.GetString(16);
            var archived = reader.IsDBNull(17) ? null : reader.GetString(17);
            ValidatePersisted(jobId, projectKey, kind, status, attempt, lineage, input, result, progress, dryRun,
                cancellation, created, updated, version, published, failure, notBefore, archived);
            return new JobRecord(jobId, projectKey, kind, status, attempt, input, result, created, updated,
                progress, lineage, cancellation, version, published, dryRun, failure, notBefore, archived);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException or
            InvalidCastException or OverflowException or JsonException)
        {
            throw new InvalidDataException("The persisted job row is malformed.", exception);
        }
    }

    private static bool ReadBoolean(SqliteDataReader reader, int ordinal, string name)
    {
        var value = reader.GetInt64(ordinal);
        if (value is not 0 and not 1) throw new InvalidDataException($"Job {name} must be 0 or 1.");
        return value == 1;
    }

    private static void ValidatePersisted(string jobId, string projectKey, string kind, JobStatus status, int attempt,
        string lineage, string input, string? result, string? progress, string? dryRun, bool cancellation,
        string created, string updated, long version, bool published, JobFailureCategory failure,
        string? notBefore, string? archived)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(projectKey) || string.IsNullOrWhiteSpace(lineage) ||
            string.IsNullOrWhiteSpace(kind) || !string.Equals(kind, kind.Trim(), StringComparison.Ordinal) ||
            string.Equals(kind, JobKinds.Apply, StringComparison.OrdinalIgnoreCase) || attempt < 1 || version < 0)
            throw new InvalidDataException("The persisted job identity or lifecycle fields are malformed.");
        JobJson.ValidateStructured(input, nameof(input));
        if (result is not null) JobJson.ValidateStructured(result, nameof(result));
        if (progress is not null) JobJson.ValidateStructured(progress, nameof(progress));
        if (dryRun is not null) JobJson.ValidateStructured(dryRun, nameof(dryRun));
        var createdUtc = ValidateUtc(created, nameof(created));
        var updatedUtc = ValidateUtc(updated, nameof(updated));
        if (createdUtc > updatedUtc) throw new InvalidDataException("CreatedUtc must not be later than UpdatedUtc.");
        if (notBefore is not null) _ = ValidateUtc(notBefore, nameof(notBefore));
        if (archived is not null) _ = ValidateUtc(archived, nameof(archived));
        if (JobStateMachine.IsTerminal(status) != (archived is not null))
            throw new InvalidDataException("Terminal job archive timestamp is inconsistent with status.");
        if (published != (dryRun is not null)) throw new InvalidDataException("Dry Run publication fields disagree.");
        if ((status is JobStatus.CompletedDryRunOnly or JobStatus.CompletedWithAssessmentFailure) && !published)
            throw new InvalidDataException("Assessment outcome lacks a published Dry Run.");
        if (!JobStateMachine.IsTerminal(status) && result is not null)
            throw new InvalidDataException("A nonterminal job cannot contain a result.");
        if (cancellation && status is JobStatus.Completed or JobStatus.CompletedDryRunOnly or
            JobStatus.CompletedWithAssessmentFailure or JobStatus.Failed)
            throw new InvalidDataException("A cancellation-requested job cannot have a successful or failed terminal status.");
        if (status == JobStatus.Cancelled && published && result != "{\"assessmentDisposition\":\"cancelled\"}")
            throw new InvalidDataException("Cancelled published Dry Run lacks its canonical Assessment disposition.");
        if (status == JobStatus.Queued && (result is not null || progress is not null || published))
            throw new InvalidDataException("Queued job contains execution state.");
    }

    private static void AddParameters(SqliteCommand command, JobRecord record)
    {
        command.Parameters.AddWithValue("$id", record.JobId);
        command.Parameters.AddWithValue("$project", record.ProjectKey);
        command.Parameters.AddWithValue("$kind", record.Kind);
        command.Parameters.AddWithValue("$status", JobStatusJson.ToWire(record.Status));
        command.Parameters.AddWithValue("$attempt", record.Attempt);
        command.Parameters.AddWithValue("$lineage", record.LogicalJobId);
        command.Parameters.AddWithValue("$input", record.InputJson);
        command.Parameters.AddWithValue("$result", (object?)record.ResultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$progress", (object?)record.ProgressJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$dryrun", (object?)record.DryRunJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$cancel", record.CancellationRequested ? 1 : 0);
        command.Parameters.AddWithValue("$created", record.CreatedUtc);
        command.Parameters.AddWithValue("$updated", record.UpdatedUtc);
        command.Parameters.AddWithValue("$version", record.Version);
        command.Parameters.AddWithValue("$published", record.DryRunPublished ? 1 : 0);
        command.Parameters.AddWithValue("$failure", JobFailureCategoryJson.ToWire(record.FailureCategory));
        command.Parameters.AddWithValue("$notBefore", (object?)record.NotBeforeUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived", (object?)record.ArchivedUtc ?? DBNull.Value);
    }

    private static void EnsureVersion(JobRecord current, long expectedVersion)
    {
        if (current.Version != expectedVersion)
            throw new InvalidOperationException("The job changed concurrently; reload it before writing.");
    }

    private static DateTimeOffset ValidateUtc(string value, string name)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
            parsed.Offset != TimeSpan.Zero || !(value.EndsWith("Z", StringComparison.Ordinal) || value.EndsWith("+00:00", StringComparison.Ordinal)))
            throw new ArgumentException("Job timestamps must be valid UTC timestamps.", name);
        return parsed;
    }

    private static JobRecord PrepareTransition(JobRecord current, JobRecord changed)
    {
        if (!JobStateMachine.IsTerminal(changed.Status)) return changed;
        var category = changed.Status == JobStatus.Cancelled ? JobFailureCategory.Cancellation :
            changed.Status == JobStatus.Failed && changed.FailureCategory == JobFailureCategory.None
                ? JobFailureCategory.Unknown : changed.FailureCategory;
        return changed with { FailureCategory = category, ArchivedUtc = changed.ArchivedUtc ?? changed.UpdatedUtc };
    }

    private static void ValidateFailureCategory(JobRecord record)
    {
        if (!JobStateMachine.IsTerminal(record.Status) && record.FailureCategory != JobFailureCategory.None)
            throw new ArgumentException("Only terminal jobs may record a failure category.");
        if (record.Status is JobStatus.Completed or JobStatus.CompletedDryRunOnly or JobStatus.CompletedWithAssessmentFailure &&
            record.FailureCategory != JobFailureCategory.None)
            throw new ArgumentException("Successful terminal jobs cannot record a failure category.");
        if (record.Status == JobStatus.Cancelled && record.FailureCategory != JobFailureCategory.Cancellation)
            throw new ArgumentException("Cancelled jobs must record cancellation.");
        if (record.Status == JobStatus.Interrupted && record.FailureCategory is not
            (JobFailureCategory.Infrastructure or JobFailureCategory.Cancellation))
            throw new ArgumentException("Interrupted jobs must record infrastructure or cancellation.");
    }
}
