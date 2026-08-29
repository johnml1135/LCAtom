using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Jobs;

namespace SIL.Motif.Worker.Jobs;

/// <summary>
/// Claims queued work for one project, runs it, and records what became of it.
/// </summary>
/// <remarks>
/// <para>
/// The loop is the only thing that ever calls <see cref="JobClaims.Claim"/>. It polls because
/// SQLite has no way to be told that a row appeared, and it jitters that poll so two runners started
/// together do not contend on every tick.
/// </para>
/// <para>
/// Every claimed row reaches a terminal state before this returns, including when the loop is being
/// cancelled. A row left running under a lease nobody will renew is unclaimable until that lease expires,
/// which is a delay a user would experience as the product having lost their work.
/// </para>
/// </remarks>
public sealed class JobRunnerLoop
{
    /// <summary>
    /// Runs one claimed job. Throwing fails it; returning a <see cref="JobOutcome"/> finishes it with
    /// that terminal status. Returning <c>null</c> means the handler already left the row exactly where
    /// it belongs — terminal or not — and the loop must not touch it again.
    /// </summary>
    public delegate Task<JobOutcome?> Handler(JobRecord job, CancellationToken cancellationToken);

    private static readonly TimeSpan HeartbeatShare = TimeSpan.FromSeconds(1);
    private readonly JobClaims _claims;
    private readonly string _projectKey;
    private readonly string _ownerId;
    private readonly TimeSpan _lease;
    private readonly TimeSpan _poll;
    private readonly IReadOnlyDictionary<string, Handler> _handlers;
    private readonly Random _jitter = new();

    /// <summary>Creates a loop bound to one project's queue and the kinds it can run.</summary>
    public JobRunnerLoop(JobClaims claims, string projectKey, string ownerId, TimeSpan lease,
        TimeSpan poll, IReadOnlyDictionary<string, Handler> handlers)
    {
        _claims = claims ?? throw new ArgumentNullException(nameof(claims));
        _projectKey = projectKey ?? throw new ArgumentNullException(nameof(projectKey));
        _ownerId = ownerId ?? throw new ArgumentNullException(nameof(ownerId));
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        _lease = lease;
        _poll = poll;
        _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
    }

    /// <summary>Runs every job it can claim, then returns once the queue is empty.</summary>
    public async Task RunUntilIdleAsync(CancellationToken cancellationToken)
    {
        // Once each per pass: a job that cannot reach a terminal state stays claimable forever.
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var claimed = _claims.Claim(_projectKey, _ownerId, Now(), _lease);
            if (claimed is null) return;
            if (!attempted.Add(claimed.JobId)) return;
            await RunOneAsync(claimed, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return;
            if (_poll > TimeSpan.Zero)
                await Task.Delay(Jittered(_poll), CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Reads this project's next claimable row's identity and position, without claiming it.</summary>
    internal JobQueueHead? PeekHead() => _claims.PeekHead(_projectKey, Now());

    /// <summary>Claims this project's next due row, or returns null when nothing is claimable right now.</summary>
    internal JobRecord? TryClaim() => _claims.Claim(_projectKey, _ownerId, Now(), _lease);

    /// <summary>Runs one already-claimed row to a terminal state, or leaves it exactly where a handler parked it.</summary>
    internal Task RunClaimedAsync(JobRecord claimed, CancellationToken cancellationToken) =>
        RunOneAsync(claimed, cancellationToken);

    private async Task RunOneAsync(JobRecord claimed, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(claimed.Kind, out var handler))
        {
            FinishWithDetail(claimed, JobStatus.Failed, JobFailureCategory.Unknown,
                "No handler is registered for job kind '" + claimed.Kind + "'.");
            return;
        }

        using var beating = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatAsync(claimed, beating.Token, linked);
        try
        {
            var outcome = await handler(claimed, linked.Token).ConfigureAwait(false);
            if (outcome is not null) Finish(claimed, outcome.Status, outcome.Category, outcome.ResultJson);
        }
        catch (OperationCanceledException)
        {
            FinishWithDetail(claimed, JobStatus.Cancelled, JobFailureCategory.Cancellation, "The runner stopped.");
        }
        catch (Exception exception)
        {
            FinishWithDetail(claimed, JobStatus.Failed, JobFailureCategory.Unknown, exception.Message);
        }
        finally
        {
            beating.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    /// Cancels <paramref name="cancelIfRequested"/> the first heartbeat after <c>jobs cancel</c> sets the flag.
    private async Task HeartbeatAsync(JobRecord claimed, CancellationToken stopping,
        CancellationTokenSource cancelIfRequested)
    {
        var interval = _lease < HeartbeatShare * 3 ? _lease / 3 : HeartbeatShare;
        while (!stopping.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            _claims.Renew(claimed.JobId, claimed.ClaimToken!, Now(), _lease);
            if (_claims.IsCancellationRequested(claimed.JobId)) cancelIfRequested.Cancel();
        }
    }

    /// A handler's own <see cref="JobOutcome.ResultJson"/> is already structured JSON; write it as-is.
    private void Finish(JobRecord claimed, JobStatus status, JobFailureCategory category, string? resultJson)
    {
        try
        {
            if (!_claims.Finish(claimed.JobId, claimed.ClaimToken!, status, category, resultJson))
                Console.Error.WriteLine(claimed.JobId + " is no longer this runner's to finish.");
        }
        catch (Exception exception)
        {
            // Reported, not thrown: one unfinishable job must not strand every job behind it.
            Console.Error.WriteLine("could not finish " + claimed.JobId + ": " + exception.Message);
        }
    }

    // The loop's own generic failure paths carry a plain message; wrap it the same way every one does.
    private void FinishWithDetail(JobRecord claimed, JobStatus status, JobFailureCategory category, string detail) =>
        Finish(claimed, status, category, JsonSerializer.Serialize(new { detail }));

    private TimeSpan Jittered(TimeSpan interval) =>
        interval + TimeSpan.FromMilliseconds(_jitter.Next(0, (int)interval.TotalMilliseconds + 1));

    private static string Now() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

/// <summary>The terminal status a <see cref="JobRunnerLoop.Handler"/> asks the loop to finish a job with.</summary>
/// <param name="Status">Must be one <see cref="JobStateMachine.IsTerminal"/> accepts from <c>running</c>.</param>
/// <param name="Category">The failure category the terminal status requires; <c>none</c> for a success.</param>
/// <param name="ResultJson">Already-structured JSON, written verbatim rather than wrapped.</param>
public sealed record JobOutcome(JobStatus Status, JobFailureCategory Category = JobFailureCategory.None,
    string? ResultJson = null)
{
    /// <summary>The ordinary successful outcome: no result payload, no failure category.</summary>
    public static readonly JobOutcome Completed = new(JobStatus.Completed);
}
