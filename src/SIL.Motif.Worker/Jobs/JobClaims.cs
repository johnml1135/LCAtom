using System.Globalization;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Host.Store;

namespace SIL.Motif.Worker.Jobs;

/// <summary>
/// One runner's hold on a project's queue: take a job, keep it, and let it go.
/// </summary>
/// <remarks>
/// <para>
/// The three belong together because one token authorises all of them. A claim mints it, a renewal proves
/// with it that the row is still this runner's, and finishing has to prove the same thing again — because
/// between the two the row may have been reclaimed, and the reclaim minted a different token. A caller
/// holding the three separately has to know that, and getting it wrong either reports an outcome for
/// somebody else's work or leaves a row running under a lease nobody will renew, which is unclaimable
/// until it expires and which a user experiences as the product losing their work.
/// </para>
/// <para>
/// Claiming is safe against other processes without any lock of ours. SQLite admits a single writer at a
/// time, and that global write lock is the serialisation, so the claim is one conditional statement: the
/// subquery picks a candidate and the outer predicate makes the write a no-op if anybody took it in
/// between. Affecting zero rows is an ordinary outcome, not an error.
/// </para>
/// </remarks>
public sealed class JobClaims
{
    private const string QueuedAndDue = "(Status = 'queued' AND (NotBeforeUtc IS NULL OR NotBeforeUtc <= $now))";
    private const string RunningAndExpired = "(Status = 'running' AND (LeaseUntilUtc IS NULL OR LeaseUntilUtc <= $now))";

    private readonly MotifDatabase _database;
    private readonly JobRepository _jobs;

    /// <summary>Creates a claim protocol over one project's paired database.</summary>
    public JobClaims(MotifDatabase database, IJobClock? clock = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _jobs = new JobRepository(database, clock);
    }

    /// <summary>Reads the identity and position of one project's next claimable row, without claiming it.</summary>
    /// <remarks>
    /// This is what lets a sweep across many projects' databases pick the globally first job before it
    /// commits to any one of them: peek every project's head, then <see cref="Claim"/> only the winner.
    /// </remarks>
    public JobQueueHead? PeekHead(string projectKey, string nowUtc)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            throw new ArgumentException("A project key is required.", nameof(projectKey));
        if (string.IsNullOrWhiteSpace(nowUtc))
            throw new ArgumentException("A timestamp is required.", nameof(nowUtc));

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT JobId, QueueOrder FROM Jobs WHERE ProjectKey = $project AND " +
            "ArchivedUtc IS NULL AND (" + QueuedAndDue + " OR " + RunningAndExpired + ") " +
            "ORDER BY QueueOrder, JobId LIMIT 1;";
        command.Parameters.AddWithValue("$now", nowUtc);
        command.Parameters.AddWithValue("$project", projectKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new JobQueueHead(reader.GetString(0), reader.GetDouble(1)) : null;
    }

    /// <summary>Takes the oldest due job for one project, or returns null when another claimant won.</summary>
    /// <remarks>
    /// A running job whose lease has run out is claimable too, which is how work left by a runner that
    /// stopped breathing is taken back. That path increments the attempt, so a job that wedges repeatedly
    /// exhausts its attempts instead of cycling forever.
    /// </remarks>
    public JobRecord? Claim(string projectKey, string ownerId, string nowUtc, TimeSpan lease)
    {
        if (string.IsNullOrWhiteSpace(projectKey))
            throw new ArgumentException("A project key is required.", nameof(projectKey));
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("An owner identity is required.", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(nowUtc))
            throw new ArgumentException("A timestamp is required.", nameof(nowUtc));
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease), "A lease must be positive.");

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var token = Guid.NewGuid().ToString("N");
        using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText =
                "UPDATE Jobs SET Status = 'running', OwnerId = $owner, ClaimToken = $token, " +
                "LeaseUntilUtc = $until, HeartbeatUtc = $now, UpdatedUtc = $now, Version = Version + 1, " +
                "Attempt = Attempt + CASE WHEN Status = 'running' THEN 1 ELSE 0 END " +
                "WHERE JobId = (SELECT JobId FROM Jobs WHERE ProjectKey = $project AND ArchivedUtc IS NULL " +
                "AND (" + QueuedAndDue + " OR " + RunningAndExpired + ") ORDER BY QueueOrder, JobId LIMIT 1) " +
                "AND (" + QueuedAndDue + " OR " + RunningAndExpired + ");";
            claim.Parameters.AddWithValue("$owner", ownerId);
            claim.Parameters.AddWithValue("$token", token);
            claim.Parameters.AddWithValue("$until", Stamp(nowUtc, lease));
            claim.Parameters.AddWithValue("$now", nowUtc);
            claim.Parameters.AddWithValue("$project", projectKey);
            if (claim.ExecuteNonQuery() == 0)
            {
                transaction.Rollback();
                return null;
            }
        }

        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = JobRepository.SelectSql + " WHERE ClaimToken = $token;";
        read.Parameters.AddWithValue("$token", token);
        JobRecord claimed;
        using (var reader = read.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("The claimed job could not be read back.");
            claimed = JobRepository.Read(reader);
        }
        transaction.Commit();
        return claimed;
    }

    /// <summary>Pushes one held job lease forward, refusing a token that no longer owns the row.</summary>
    /// <remarks>
    /// The token, not the owner identity, is what authorises this. A runner can stall past its lease, lose
    /// the row, and wake up still carrying the same owner identity — only a token minted by the claim that
    /// actually holds the row now tells those two apart.
    /// </remarks>
    public bool Renew(string jobId, string claimToken, string nowUtc, TimeSpan lease)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("A job id is required.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(claimToken))
            throw new ArgumentException("A claim token is required.", nameof(claimToken));
        if (lease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lease), "A lease must be positive.");

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE Jobs SET LeaseUntilUtc = $until, HeartbeatUtc = $now " +
            "WHERE JobId = $id AND ClaimToken = $token;";
        command.Parameters.AddWithValue("$until", Stamp(nowUtc, lease));
        command.Parameters.AddWithValue("$now", nowUtc);
        command.Parameters.AddWithValue("$id", jobId);
        command.Parameters.AddWithValue("$token", claimToken);
        var extended = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return extended;
    }

    /// <summary>Moves a held job to its terminal state, or reports it was no longer this runner's to move.</summary>
    /// <remarks>
    /// The token authorises this for the same reason it authorises a renewal, and the danger is larger
    /// here. A runner that stalled past its lease still holds a record saying <c>running</c>; the row it
    /// names is running too, because somebody else reclaimed it. Finishing on the status alone would let
    /// the runner that lost the row report an outcome for work another runner is still doing.
    /// </remarks>
    public bool Finish(string jobId, string claimToken, JobStatus status, JobFailureCategory category,
        string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(claimToken))
            throw new ArgumentException("A claim token is required.", nameof(claimToken));

        var current = _jobs.Get(jobId);
        if (current is null || current.Status != JobStatus.Running) return false;
        if (!string.Equals(current.ClaimToken, claimToken, StringComparison.Ordinal)) return false;
        _jobs.Transition(jobId, status, current.Version, category, resultJson);
        return true;
    }

    /// A lease deadline is written in the same sortable form as every other timestamp in this store.
    private static string Stamp(string nowUtc, TimeSpan lease) =>
        (DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) + lease)
        .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The identity and position of one project's next claimable row, as read by <see cref="JobClaims.PeekHead"/>.
/// </summary>
/// <param name="JobId">Never claimed by this alone; a sweep must still call <see cref="JobClaims.Claim"/>.</param>
/// <param name="QueueOrder">Compared before <paramref name="JobId"/>, which only breaks a tie.</param>
public readonly record struct JobQueueHead(string JobId, double QueueOrder);
