using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Model;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Host.Store;
using SIL.Motif.Runner.DryRun;

namespace SIL.Motif.Worker.Store;

/// <summary>Durable Proposal workflow access through a worker-owned Motif database.</summary>
public interface IProposalRepository
{
    /// <summary>Gets the current revision and review state for one Proposal.</summary>
    ProposalRecord Get(CanonicalId proposalId);
    /// <summary>Lists current Proposals, optionally restricted to one workflow status; drafts are included, marked by <see cref="ProposalRecord.DraftName"/>.</summary>
    IReadOnlyList<ProposalRecord> List(ProposalListFilter filter);
    /// <summary>Stores one immutable revision and moves its Proposal pointer to that revision.</summary>
    void SaveRevision(ProposalRevisionRecord revision);
    /// <summary>Stores a Decision bound to one exact Proposal revision.</summary>
    void SaveDecision(DecisionRecord decision);
    /// <summary>
    /// Creates a Draft: a Proposal identified by <paramref name="proposalId"/> with <c>DraftName</c> set and
    /// no committed revision yet — <c>CurrentIntentDigest</c> stays <c>NULL</c> until <see cref="Finalize"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A draft already exists under <paramref name="draftName"/>.</exception>
    void CreateDraft(string draftName, CanonicalId proposalId, string draftJson);
    /// <summary>Replaces a Draft's in-progress content. The Draft keeps its identity and name.</summary>
    void SaveDraft(string draftName, string draftJson);
    /// <summary>Gets one Draft by name.</summary>
    ProposalRecord GetDraft(string draftName);
    /// <summary>Lists every Draft, ordered by name.</summary>
    IReadOnlyList<ProposalRecord> ListDrafts();
    /// <summary>
    /// Commits a Draft: in one transaction, writes its first <c>ProposalRevisions</c> row, sets
    /// <c>CurrentIntentDigest</c>, clears <c>DraftName</c>/<c>DraftJson</c>/<c>AnchorJson</c>, and moves
    /// the Proposal to <c>proposed</c>. A failure (for example a digest collision with different content
    /// already recorded under this Proposal) leaves the Draft exactly as it was — none of the four
    /// effects is observable without all of them.
    /// </summary>
    /// <returns><c>true</c> when this id already carried a committed revision — an amend, not a first commit.</returns>
    bool Finalize(string draftName, string intentDigest, string proposalJson, string label, string comment);
    /// <summary>
    /// Loads one finalized Proposal, verifying that its recorded revision exists and that the
    /// revision's own embedded id and recomputed digest agree with the pointer that named it.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No finalized Proposal is recorded under this id.</exception>
    /// <exception cref="InvalidDataException">
    /// The pointer names a revision that is missing, or the revision's embedded id or content digest
    /// disagrees with it (store inconsistency).
    /// </exception>
    (ProposalRecord Record, Proposal Envelope) GetFinalized(CanonicalId proposalId);
    /// <summary>
    /// Resolves <paramref name="requested"/>'s finalized prerequisite closure through <see cref="GetFinalized"/>
    /// and returns the Proposals that must prepare a Dry Run scratch, in deterministic topological order.
    /// Applied prerequisites (<paramref name="appliedProposalIds"/>) satisfy their dependency branch
    /// without requiring their revisions to be readable.
    /// </summary>
    PrerequisiteExecutionPlan PlanPrerequisites(Proposal requested, IReadOnlyCollection<Guid> appliedProposalIds);
    /// <summary>Sets the bound-DryRun anchor recorded against a Proposal's current revision, or clears it.</summary>
    void SetAnchor(CanonicalId proposalId, string? anchorJson);
    /// <summary>
    /// Gets one finalized (non-draft) Proposal's review state for a status transition, without
    /// requiring its revision to be readable — a transition reads and writes only <c>Proposals</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No finalized Proposal is recorded under this id.</exception>
    ProposalRecord GetForTransition(CanonicalId proposalId);
    /// <summary>
    /// Moves a Proposal to <paramref name="status"/>, records <paramref name="supersededBy"/>, and, when
    /// <paramref name="clearDecision"/> is set, deletes any Decision bound to its current revision — a
    /// Decision is scoped to the content it was recorded against, so a transition that is not itself a
    /// new Decision must not leave a stale one visible.
    /// </summary>
    void SetStatus(CanonicalId proposalId, string status, string? supersededBy, bool clearDecision);
    /// <summary>Sets a Proposal's status to <c>applied</c>, touching nothing else on the row.</summary>
    void MarkApplied(CanonicalId proposalId);
    /// <summary>Whether a Draft is currently registered under this name.</summary>
    bool DraftNameExists(string draftName);
    /// <summary>
    /// Turns an already-finalized Proposal back into a Draft under a new local draft name, keeping its
    /// id and current committed content addressable until the draft is finalized again as an amend.
    /// </summary>
    /// <exception cref="InvalidOperationException">A draft already exists under <paramref name="draftName"/>.</exception>
    /// <exception cref="KeyNotFoundException">No finalized Proposal is recorded under this id.</exception>
    void ReopenAsDraft(CanonicalId proposalId, string draftName, string draftJson);
}

/// <summary>
/// The current pointer and review state of a Proposal. <see cref="DraftName"/> non-null marks a Draft: it has
/// no committed revision, so <see cref="IntentDigest"/> is null and <see cref="ProposalJson"/> is its
/// in-progress content rather than an immutable revision's bytes.
/// </summary>
public sealed record ProposalRecord(
    CanonicalId ProposalId, string? IntentDigest, string? ProposalJson, string Status, string? Label,
    string? Comment, string? SupersededBy, DecisionRecord? Decision = null, byte[]? ProposalJsonBytes = null,
    string? AnchorJson = null, string? ArchivedUtc = null, string? DraftName = null);

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
                   d.Outcome, d.ActorType, d.ActorId, d.Comment, d.TimestampUtc, d.IntentDigest, p.AnchorJson, p.ArchivedUtc,
                   p.DraftName, p.DraftJson
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
                   d.Outcome, d.ActorType, d.ActorId, d.Comment, d.TimestampUtc, d.IntentDigest, p.AnchorJson, p.ArchivedUtc,
                   p.DraftName, p.DraftJson
            FROM Proposals p LEFT JOIN ProposalRevisions r ON r.ProposalId = p.ProposalId
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
        status.CommandText = "UPDATE Proposals SET Status = $status, SupersededBy = NULL, ArchivedUtc = CASE WHEN " +
            "$status IN ('applied','rejected','superseded','withdrawn') THEN COALESCE(ArchivedUtc, $archived) " +
            "ELSE NULL END WHERE ProposalId = $id AND CurrentIntentDigest = $digest;";
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
            "DELETE FROM Reports WHERE ProposalId = $id;", "DELETE FROM ProposalRevisions WHERE ProposalId = $id;" })
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

    /// <inheritdoc />
    public void CreateDraft(string draftName, CanonicalId proposalId, string draftJson)
    {
        if (string.IsNullOrWhiteSpace(draftName)) throw new ArgumentException("A draft name is required.", nameof(draftName));
        if (string.IsNullOrWhiteSpace(draftJson)) throw new ArgumentException("Draft content is required.", nameof(draftJson));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM Proposals WHERE DraftName = $name;";
            check.Parameters.AddWithValue("$name", draftName);
            if (check.ExecuteScalar() is not null)
                throw new InvalidOperationException(
                    $"Draft '{draftName}' already exists. Finalize or discard it before creating a new draft with this name.");
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO Proposals (ProposalId, CurrentIntentDigest, Status, DraftName, DraftJson)
            VALUES ($id, NULL, $status, $name, $json);
            """;
        insert.Parameters.AddWithValue("$id", proposalId.Value);
        insert.Parameters.AddWithValue("$status", DraftStatus);
        insert.Parameters.AddWithValue("$name", draftName);
        insert.Parameters.AddWithValue("$json", draftJson);
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <inheritdoc />
    public void SaveDraft(string draftName, string draftJson)
    {
        if (string.IsNullOrWhiteSpace(draftJson)) throw new ArgumentException("Draft content is required.", nameof(draftJson));
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Proposals SET DraftJson = $json WHERE DraftName = $name;";
        command.Parameters.AddWithValue("$json", draftJson);
        command.Parameters.AddWithValue("$name", draftName);
        if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException($"Draft '{draftName}' was not found.");
    }

    /// <inheritdoc />
    public ProposalRecord GetDraft(string draftName)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = DraftSelectSql + " WHERE DraftName = $name;";
        command.Parameters.AddWithValue("$name", draftName);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadDraftRecord(reader) : throw new KeyNotFoundException($"Draft '{draftName}' was not found.");
    }

    /// <inheritdoc />
    public IReadOnlyList<ProposalRecord> ListDrafts()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = DraftSelectSql + " WHERE DraftName IS NOT NULL ORDER BY DraftName;";
        using var reader = command.ExecuteReader();
        var records = new List<ProposalRecord>();
        while (reader.Read()) records.Add(ReadDraftRecord(reader));
        return records;
    }

    /// <inheritdoc />
    public bool Finalize(string draftName, string intentDigest, string proposalJson, string label, string comment)
    {
        if (string.IsNullOrWhiteSpace(intentDigest) || string.IsNullOrWhiteSpace(proposalJson))
            throw new ArgumentException("Finalize requires an intent digest and Proposal content.", nameof(intentDigest));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        string proposalId;
        bool wasAmend;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT ProposalId, CurrentIntentDigest FROM Proposals WHERE DraftName = $name;";
            find.Parameters.AddWithValue("$name", draftName);
            using var reader = find.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException($"Draft '{draftName}' was not found.");
            proposalId = reader.GetString(0);
            wasAmend = !reader.IsDBNull(1);
        }
        using (var revision = connection.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = """
                INSERT INTO ProposalRevisions (ProposalId, IntentDigest, ProposalJson, CreatedUtc)
                VALUES ($id, $digest, $bytes, $created);
                """;
            revision.Parameters.AddWithValue("$id", proposalId);
            revision.Parameters.AddWithValue("$digest", intentDigest);
            revision.Parameters.AddWithValue("$bytes", Encoding.UTF8.GetBytes(proposalJson));
            revision.Parameters.AddWithValue(
                "$created", _clock.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            revision.ExecuteNonQuery();
        }
        using (var commit = connection.CreateCommand())
        {
            commit.Transaction = transaction;
            commit.CommandText = """
                UPDATE Proposals SET CurrentIntentDigest = $digest, Status = $status, Label = $label,
                    Comment = $comment, DraftName = NULL, DraftJson = NULL, AnchorJson = NULL
                WHERE DraftName = $name;
                """;
            commit.Parameters.AddWithValue("$digest", intentDigest);
            commit.Parameters.AddWithValue("$status", "proposed");
            commit.Parameters.AddWithValue("$label", label);
            commit.Parameters.AddWithValue("$comment", comment);
            commit.Parameters.AddWithValue("$name", draftName);
            commit.ExecuteNonQuery();
        }
        transaction.Commit();
        return wasAmend;
    }

    /// <inheritdoc />
    public (ProposalRecord Record, Proposal Envelope) GetFinalized(CanonicalId proposalId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.CurrentIntentDigest, r.ProposalJson, p.Status, p.Label, p.Comment, p.SupersededBy, p.AnchorJson
            FROM Proposals p LEFT JOIN ProposalRevisions r
                ON r.ProposalId = p.ProposalId AND r.IntentDigest = p.CurrentIntentDigest
            WHERE p.ProposalId = $id AND p.DraftName IS NULL;
            """;
        command.Parameters.AddWithValue("$id", proposalId.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new KeyNotFoundException(
                $"Proposal '{proposalId.Value}' not found in store. Run 'list' to see " +
                "committed proposals.");
        }

        var digest = reader.IsDBNull(0) ? null : reader.GetString(0);
        if (digest is null || reader[1] is not byte[] bytes)
        {
            throw new InvalidDataException(
                $"Proposal '{proposalId.Value}' manifest points at intentDigest '{digest}', but no " +
                "matching revision is recorded (store inconsistency).");
        }

        var status = reader.GetString(2);
        var label = reader.IsDBNull(3) ? null : reader.GetString(3);
        var comment = reader.IsDBNull(4) ? null : reader.GetString(4);
        var supersededBy = reader.IsDBNull(5) ? null : reader.GetString(5);
        var anchorJson = reader.IsDBNull(6) ? null : reader.GetString(6);

        string json;
        try
        {
            json = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "The stored Proposal JSON is not valid UTF-8 (store inconsistency).", exception);
        }

        var envelope = ProposalJsonParser.Parse(json);
        if (!string.Equals(envelope.ProposalId.Value, proposalId.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Proposal store identity is inconsistent: lookup id {proposalId.Value} names a " +
                $"revision whose Proposal id is {envelope.ProposalId.Value} (store inconsistency).");
        }

        var actualDigest = IntentDigest.Compute(envelope);
        if (!string.Equals(actualDigest, digest, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Proposal {proposalId.Value} revision content computes intentDigest '{actualDigest}', " +
                $"not the bound '{digest}' (store inconsistency).");
        }

        var record = new ProposalRecord(proposalId, digest, json, status, label, comment, supersededBy, null, bytes, anchorJson);
        return (record, envelope);
    }

    /// <inheritdoc />
    public PrerequisiteExecutionPlan PlanPrerequisites(Proposal requested, IReadOnlyCollection<Guid> appliedProposalIds)
    {
        if (requested is null) throw new ArgumentNullException(nameof(requested));
        if (appliedProposalIds is null) throw new ArgumentNullException(nameof(appliedProposalIds));

        var candidates = PrerequisiteClosureResolver.Resolve(
            requested,
            id => GetFinalized(CanonicalId.Parse(id)).Envelope,
            appliedProposalIds);
        return PrerequisiteExecutionPlan.Create(requested, candidates, appliedProposalIds);
    }

    /// <inheritdoc />
    public void SetAnchor(CanonicalId proposalId, string? anchorJson)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Proposals SET AnchorJson = $anchor WHERE ProposalId = $id;";
        command.Parameters.AddWithValue("$anchor", (object?)anchorJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", proposalId.Value);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public ProposalRecord GetForTransition(CanonicalId proposalId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Status, Label, Comment, SupersededBy, CurrentIntentDigest
            FROM Proposals WHERE ProposalId = $id AND DraftName IS NULL;
            """;
        command.Parameters.AddWithValue("$id", proposalId.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new KeyNotFoundException(
                $"Proposal '{proposalId.Value}' not found in store. Run 'list' to see " +
                "committed proposals.");
        }

        return new ProposalRecord(
            proposalId,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            null,
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <inheritdoc />
    public void SetStatus(CanonicalId proposalId, string status, string? supersededBy, bool clearDecision)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        if (clearDecision)
        {
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = """
                DELETE FROM Decisions WHERE ProposalId = $id AND IntentDigest =
                    (SELECT CurrentIntentDigest FROM Proposals WHERE ProposalId = $id);
                """;
            clear.Parameters.AddWithValue("$id", proposalId.Value);
            clear.ExecuteNonQuery();
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE Proposals SET Status = $status, SupersededBy = $superseded WHERE ProposalId = $id;";
        update.Parameters.AddWithValue("$status", status);
        update.Parameters.AddWithValue("$superseded", (object?)supersededBy ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", proposalId.Value);
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <inheritdoc />
    public void MarkApplied(CanonicalId proposalId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Proposals SET Status = 'applied' WHERE ProposalId = $id;";
        command.Parameters.AddWithValue("$id", proposalId.Value);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public bool DraftNameExists(string draftName)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM Proposals WHERE DraftName = $name;";
        command.Parameters.AddWithValue("$name", draftName);
        return command.ExecuteScalar() is not null;
    }

    /// <inheritdoc />
    public void ReopenAsDraft(CanonicalId proposalId, string draftName, string draftJson)
    {
        if (string.IsNullOrWhiteSpace(draftName)) throw new ArgumentException("A draft name is required.", nameof(draftName));
        if (string.IsNullOrWhiteSpace(draftJson)) throw new ArgumentException("Draft content is required.", nameof(draftJson));
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM Proposals WHERE DraftName = $name;";
            check.Parameters.AddWithValue("$name", draftName);
            if (check.ExecuteScalar() is not null)
                throw new InvalidOperationException(
                    $"Draft '{draftName}' already exists. Finalize or delete it before creating a new draft with this name.");
        }
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE Proposals SET DraftName = $name, DraftJson = $json
            WHERE ProposalId = $id AND CurrentIntentDigest IS NOT NULL;
            """;
        update.Parameters.AddWithValue("$name", draftName);
        update.Parameters.AddWithValue("$json", draftJson);
        update.Parameters.AddWithValue("$id", proposalId.Value);
        if (update.ExecuteNonQuery() != 1)
        {
            throw new KeyNotFoundException(
                $"Proposal '{proposalId.Value}' not found in store. Run 'list' to see " +
                "committed proposals.");
        }
        transaction.Commit();
    }

    private const string DraftSelectSql =
        "SELECT ProposalId, Status, Label, Comment, SupersededBy, AnchorJson, ArchivedUtc, DraftName, DraftJson FROM Proposals";

    private static ProposalRecord ReadDraftRecord(SqliteDataReader reader)
    {
        var proposalId = CanonicalId.Parse(reader.GetString(0));
        return new ProposalRecord(proposalId, null, reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), null, null,
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private static ProposalRecord ReadRecord(SqliteDataReader reader)
    {
        var proposalId = CanonicalId.Parse(reader.GetString(0));
        DecisionRecord? decision = reader.IsDBNull(7)
            ? null
            : new DecisionRecord(proposalId, reader.GetString(12), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11));
        var draftName = reader.IsDBNull(15) ? null : reader.GetString(15);
        var intentDigest = reader.IsDBNull(1) ? null : reader.GetString(1);
        string? json;
        byte[]? bytes = null;
        if (draftName is not null)
        {
            // A draft has no committed revision yet; its content is the working DraftJson, not a BLOB.
            json = reader.IsDBNull(16) ? null : reader.GetString(16);
        }
        else
        {
            if (reader.IsDBNull(2) || reader[2] is not byte[] committedBytes)
                throw new InvalidDataException("Stored Proposal JSON must be a non-null BLOB.");
            bytes = committedBytes;
            try
            {
                json = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The stored Proposal JSON is not valid UTF-8.", exception);
            }
        }
        return new ProposalRecord(proposalId, intentDigest, json, reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), decision, bytes,
            reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetString(14),
            draftName);
    }

    private const string DraftStatus = "draft";

    private static bool IsTerminal(string status) => status is "applied" or "rejected" or "superseded" or "withdrawn";

    private static DateTimeOffset? ParseNullableUtc(string? value) => value is null ? null : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
