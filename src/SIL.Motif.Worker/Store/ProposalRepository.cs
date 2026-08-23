using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Store;

/// <summary>Durable Proposal workflow access through a worker-owned Motif database.</summary>
public interface IProposalRepository
{
    /// <summary>Gets the current revision and review state for one Proposal.</summary>
    ProposalRecord Get(CanonicalId proposalId);
    /// <summary>Lists current Proposals, optionally restricted to one workflow status.</summary>
    IReadOnlyList<ProposalRecord> List(ProposalListFilter filter);
    /// <summary>Stores one immutable revision and moves its Proposal pointer to that revision.</summary>
    void SaveRevision(ProposalRevisionRecord revision);
    /// <summary>Stores a Decision bound to one exact Proposal revision.</summary>
    void SaveDecision(DecisionRecord decision);
}

/// <summary>The current pointer and review state of a Proposal.</summary>
public sealed record ProposalRecord(
    CanonicalId ProposalId, string IntentDigest, string ProposalJson, string Status, string? Label,
    string? Comment, string? SupersededBy, DecisionRecord? Decision = null, byte[]? ProposalJsonBytes = null,
    string? AnchorJson = null, string? ArchivedUtc = null);

/// <summary>An immutable Proposal revision, retaining the exact source JSON bytes.</summary>
public sealed record ProposalRevisionRecord(
    CanonicalId ProposalId, string IntentDigest, string ProposalJson, string Status, string? Label,
    string? Comment, string? SupersededBy, string? CreatedUtc = null, byte[]? ProposalJsonBytes = null);

/// <summary>A review Decision bound to a Proposal intent digest.</summary>
public sealed record DecisionRecord(
    CanonicalId ProposalId, string IntentDigest, string Outcome, string ActorType, string ActorId,
    string? Comment, string TimestampUtc);

/// <summary>Selection options for current Proposal rows.</summary>
public sealed record ProposalListFilter(string? Status = null, bool IncludeArchived = false);

/// <summary>Reads and writes normalized Proposal and review tables.</summary>
public sealed class ProposalRepository : IProposalRepository
{
    private readonly MotifDatabase _database;
    private readonly IJobClock _clock;

    /// <summary>Creates a repository over an already worker-owned database.</summary>
    public ProposalRepository(MotifDatabase database, IJobClock? clock = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _clock = clock ?? new SystemJobClock();
    }

    /// <inheritdoc />
    public ProposalRecord Get(CanonicalId proposalId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.ProposalId, p.CurrentIntentDigest, r.ProposalJson,
                   p.Status, p.Label, p.Comment, p.SupersededBy,
                   d.Outcome, d.ActorType, d.ActorId, d.Comment, d.TimestampUtc, d.IntentDigest, p.AnchorJson, p.ArchivedUtc
            FROM Proposals p JOIN ProposalRevisions r ON r.ProposalId = p.ProposalId
                AND r.IntentDigest = p.CurrentIntentDigest
            LEFT JOIN Decisions d ON d.ProposalId = p.ProposalId AND d.IntentDigest = p.CurrentIntentDigest
            WHERE p.ProposalId = $id;
            """;
        command.Parameters.AddWithValue("$id", proposalId.Value);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadRecord(reader)
            : throw new KeyNotFoundException($"Proposal '{proposalId.Value}' was not found.");
    }

    /// <inheritdoc />
    public IReadOnlyList<ProposalRecord> List(ProposalListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        var archived = filter.IncludeArchived ? "" :
            " AND p.Status NOT IN ('applied', 'rejected', 'superseded', 'withdrawn')";
        command.CommandText = """
            SELECT p.ProposalId, p.CurrentIntentDigest, r.ProposalJson,
                   p.Status, p.Label, p.Comment, p.SupersededBy,
                   d.Outcome, d.ActorType, d.ActorId, d.Comment, d.TimestampUtc, d.IntentDigest, p.AnchorJson, p.ArchivedUtc
            FROM Proposals p JOIN ProposalRevisions r ON r.ProposalId = p.ProposalId
                AND r.IntentDigest = p.CurrentIntentDigest
            LEFT JOIN Decisions d ON d.ProposalId = p.ProposalId AND d.IntentDigest = p.CurrentIntentDigest
            WHERE ($status IS NULL OR p.Status = $status)
            """ + archived + " ORDER BY p.ProposalId;";
        command.Parameters.AddWithValue("$status", (object?)filter.Status ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var records = new List<ProposalRecord>();
        while (reader.Read()) records.Add(ReadRecord(reader));
        return records;
    }

    /// <inheritdoc />
    public void SaveRevision(ProposalRevisionRecord revision)
    {
        _ = revision.ProposalId.Value;
        if (string.IsNullOrWhiteSpace(revision.IntentDigest) || revision.ProposalJson is null ||
            string.IsNullOrWhiteSpace(revision.Status))
            throw new ArgumentException("Revision digest, JSON, and status are required.", nameof(revision));

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var created = revision.CreatedUtc ?? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var bytes = revision.ProposalJsonBytes ?? Encoding.UTF8.GetBytes(revision.ProposalJson);
        var archived = IsTerminal(revision.Status) ? _clock.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : null;
        using (var parent = connection.CreateCommand())
        {
            parent.Transaction = transaction;
            parent.CommandText = """
                INSERT INTO Proposals
                    (ProposalId, CurrentIntentDigest, Status, Label, Comment, SupersededBy, ArchivedUtc)
                VALUES ($id, $digest, $status, $label, $comment, $superseded, $archived)
                ON CONFLICT(ProposalId) DO UPDATE SET CurrentIntentDigest = excluded.CurrentIntentDigest,
                    Status = excluded.Status, Label = excluded.Label, Comment = excluded.Comment,
                    SupersededBy = excluded.SupersededBy, AnchorJson = NULL,
                    ArchivedUtc = CASE WHEN excluded.Status IN ('applied','rejected','superseded','withdrawn')
                        THEN COALESCE(Proposals.ArchivedUtc, excluded.ArchivedUtc) ELSE NULL END;
                """;
            AddParameters(parent, revision, bytes);
            parent.Parameters.AddWithValue("$superseded", (object?)revision.SupersededBy ?? DBNull.Value);
            parent.Parameters.AddWithValue("$archived", (object?)archived ?? DBNull.Value);
            parent.ExecuteNonQuery();
        }

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT ProposalJson
                FROM ProposalRevisions WHERE ProposalId = $id AND IntentDigest = $digest;
                """;
            check.Parameters.AddWithValue("$id", revision.ProposalId.Value);
            check.Parameters.AddWithValue("$digest", revision.IntentDigest);
            using var reader = check.ExecuteReader();
            if (reader.Read())
            {
                if (reader[0] is not byte[] existingBytes)
                    throw new InvalidDataException("Proposal revision JSON is not stored as a BLOB.");
                if (!existingBytes.SequenceEqual(bytes))
                    throw new InvalidDataException(
                        $"Proposal revision '{revision.IntentDigest}' already exists with different content.");
                transaction.Commit();
                return;
            }
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ProposalRevisions
                (ProposalId, IntentDigest, ProposalJson, CreatedUtc)
            VALUES ($id, $digest, $bytes, $created);
            """;
        AddParameters(insert, revision, bytes);
        insert.Parameters.AddWithValue("$created", created);
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <inheritdoc />
    public void SaveDecision(DecisionRecord decision)
    {
        if (string.IsNullOrWhiteSpace(decision.IntentDigest) || string.IsNullOrWhiteSpace(decision.Outcome) ||
            string.IsNullOrWhiteSpace(decision.ActorType) || string.IsNullOrWhiteSpace(decision.ActorId) ||
            string.IsNullOrWhiteSpace(decision.TimestampUtc))
            throw new ArgumentException("Decision identity, actor, outcome, and timestamp are required.", nameof(decision));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var revision = connection.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = "SELECT 1 FROM ProposalRevisions WHERE ProposalId = $id AND IntentDigest = $digest;";
            revision.Parameters.AddWithValue("$id", decision.ProposalId.Value);
            revision.Parameters.AddWithValue("$digest", decision.IntentDigest);
            if (revision.ExecuteScalar() is null)
                throw new InvalidDataException("A Decision must bind to an existing Proposal revision.");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Decisions (ProposalId, IntentDigest, Outcome, ActorType, ActorId, Comment, TimestampUtc)
            VALUES ($id, $digest, $outcome, $actorType, $actorId, $comment, $timestamp)
            ON CONFLICT(ProposalId, IntentDigest) DO UPDATE SET Outcome = excluded.Outcome,
                ActorType = excluded.ActorType, ActorId = excluded.ActorId, Comment = excluded.Comment,
                TimestampUtc = excluded.TimestampUtc;
            """;
        command.Parameters.AddWithValue("$id", decision.ProposalId.Value);
        command.Parameters.AddWithValue("$digest", decision.IntentDigest);
        command.Parameters.AddWithValue("$outcome", decision.Outcome);
        command.Parameters.AddWithValue("$actorType", decision.ActorType);
        command.Parameters.AddWithValue("$actorId", decision.ActorId);
        command.Parameters.AddWithValue("$comment", (object?)decision.Comment ?? DBNull.Value);
        command.Parameters.AddWithValue("$timestamp", decision.TimestampUtc);
        command.ExecuteNonQuery();
        using var status = connection.CreateCommand();
        status.Transaction = transaction;
        status.CommandText = "UPDATE Proposals SET Status = $status, ArchivedUtc = CASE WHEN $status IN " +
            "('applied','rejected','superseded','withdrawn') THEN COALESCE(ArchivedUtc, $archived) ELSE NULL END " +
            "WHERE ProposalId = $id AND CurrentIntentDigest = $digest;";
        status.Parameters.AddWithValue("$status", decision.Outcome);
        status.Parameters.AddWithValue("$id", decision.ProposalId.Value);
        status.Parameters.AddWithValue("$digest", decision.IntentDigest);
        status.Parameters.AddWithValue("$archived", _clock.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        status.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<ProposalRecord> ListArchived(DateTimeOffset now, ArchivePolicy? policy = null)
    {
        policy ??= ArchivePolicy.Default;
        return List(new ProposalListFilter(IncludeArchived: true))
            .Where(proposal => IsTerminal(proposal.Status) &&
                policy.ShouldPurge(ParseNullableUtc(proposal.ArchivedUtc), now)).ToArray();
    }

    public void DeleteArchived(CanonicalId proposalId)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT Status FROM Proposals WHERE ProposalId = $id;";
            check.Parameters.AddWithValue("$id", proposalId.Value);
            var status = check.ExecuteScalar() as string;
            if (status is null || !IsTerminal(status)) throw new InvalidOperationException("Only terminal Proposals may be archived.");
        }
        foreach (var sql in new[] { "DELETE FROM Decisions WHERE ProposalId = $id;", "DELETE FROM Receipts WHERE ProposalId = $id;",
            "DELETE FROM Reports WHERE ProposalId = $id;", "DELETE FROM Drafts WHERE ProposalId = $id;",
            "DELETE FROM ProposalRevisions WHERE ProposalId = $id;" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", proposalId.Value);
            command.ExecuteNonQuery();
        }
        using var proposal = connection.CreateCommand();
        proposal.Transaction = transaction;
        proposal.CommandText = "DELETE FROM Proposals WHERE ProposalId = $id;";
        proposal.Parameters.AddWithValue("$id", proposalId.Value);
        proposal.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void AddParameters(SqliteCommand command, ProposalRevisionRecord revision, byte[] bytes)
    {
        command.Parameters.AddWithValue("$id", revision.ProposalId.Value);
        command.Parameters.AddWithValue("$digest", revision.IntentDigest);
        command.Parameters.AddWithValue("$json", revision.ProposalJson);
        command.Parameters.AddWithValue("$bytes", bytes);
        command.Parameters.AddWithValue("$status", revision.Status);
        command.Parameters.AddWithValue("$label", (object?)revision.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$comment", (object?)revision.Comment ?? DBNull.Value);
    }

    private static ProposalRecord ReadRecord(SqliteDataReader reader)
    {
        var proposalId = CanonicalId.Parse(reader.GetString(0));
        DecisionRecord? decision = reader.IsDBNull(7)
            ? null
            : new DecisionRecord(proposalId, reader.GetString(12), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11));
        if (reader.IsDBNull(2) || reader[2] is not byte[] bytes)
            throw new InvalidDataException("Stored Proposal JSON must be a non-null BLOB.");
        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The stored Proposal JSON is not valid UTF-8.", exception);
        }
        return new ProposalRecord(proposalId, reader.GetString(1), json, reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), decision, bytes,
            reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14));
    }

    private static bool IsTerminal(string status) => status is "applied" or "rejected" or "superseded" or "withdrawn";

    private static DateTimeOffset? ParseNullableUtc(string? value) => value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
