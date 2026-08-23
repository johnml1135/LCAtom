using System.Globalization;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Jobs;

/// <summary>Persists job attempts with transactional optimistic concurrency.</summary>
public sealed class JobRepository
{
    private readonly MotifDatabase _database;
    private readonly JobStateMachine _stateMachine;

    public JobRepository(MotifDatabase database, IJobClock? clock = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _stateMachine = new JobStateMachine(clock);
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

    public JobRecord Transition(string jobId, JobStatus next, long expectedVersion, string? resultJson = null)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var current = ReadRequired(connection, transaction, jobId);
        EnsureVersion(current, expectedVersion);
        var changed = _stateMachine.Transition(current, next, resultJson);
        UpdateTransition(connection, transaction, changed);
        transaction.Commit();
        return changed;
    }

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
        UpdateProgressRow(connection, transaction, changed);
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
            requested.DryRunPublished)
            throw new ArgumentException("A new queued job cannot contain execution state.");
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
                (JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson, ProgressJson,
                 CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished)
            VALUES ($id, $project, $kind, $status, $attempt, $lineage, $input, $result, $progress,
                    $cancel, $created, $updated, $version, $published);
            """;
        AddParameters(command, record);
        command.ExecuteNonQuery();
    }

    private static void UpdateTransition(SqliteConnection connection, SqliteTransaction transaction, JobRecord record)
    {
        ExecuteConcurrencyUpdate(connection, transaction, record,
            "Status = $status, ResultJson = $result, UpdatedUtc = $updated, Version = $version");
    }

    private static void UpdateCancellation(SqliteConnection connection, SqliteTransaction transaction, JobRecord record) =>
        ExecuteConcurrencyUpdate(connection, transaction, record,
            "CancellationRequested = $cancel, UpdatedUtc = $updated, Version = $version");

    private static void UpdateProgressRow(SqliteConnection connection, SqliteTransaction transaction, JobRecord record) =>
        ExecuteConcurrencyUpdate(connection, transaction, record,
            "ProgressJson = $progress, DryRunPublished = $published, UpdatedUtc = $updated, Version = $version");

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

    private static readonly string SelectSql = "SELECT JobId, ProjectKey, Kind, Status, Attempt, LineageId, InputJson, ResultJson, ProgressJson, " +
        "CancellationRequested, CreatedUtc, UpdatedUtc, Version, DryRunPublished FROM Jobs";

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
        var status = JobStatusJson.Parse(reader.GetString(3));
        return new JobRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), status, reader.GetInt32(4),
            reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(10), reader.GetString(11),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(5), reader.GetInt32(9) != 0, reader.GetInt64(12),
            reader.GetInt32(13) != 0);
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
        command.Parameters.AddWithValue("$cancel", record.CancellationRequested ? 1 : 0);
        command.Parameters.AddWithValue("$created", record.CreatedUtc);
        command.Parameters.AddWithValue("$updated", record.UpdatedUtc);
        command.Parameters.AddWithValue("$version", record.Version);
        command.Parameters.AddWithValue("$published", record.DryRunPublished ? 1 : 0);
    }

    private static void EnsureVersion(JobRecord current, long expectedVersion)
    {
        if (current.Version != expectedVersion)
            throw new InvalidOperationException("The job changed concurrently; reload it before writing.");
    }

    private static DateTimeOffset ValidateUtc(string value, string name)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
            throw new ArgumentException("Job timestamps must be valid UTC timestamps.", name);
        return parsed;
    }
}
