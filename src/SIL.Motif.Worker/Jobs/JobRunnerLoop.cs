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
    /// <summary>Runs one claimed job. Throwing fails it; returning completes it.</summary>
    public delegate Task Handler(JobRecord job, CancellationToken cancellationToken);

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

    private async Task RunOneAsync(JobRecord claimed, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(claimed.Kind, out var handler))
        {
            Finish(claimed, JobStatus.Failed, JobFailureCategory.Unknown,
                "No handler is registered for job kind '" + claimed.Kind + "'.");
            return;
        }

        using var beating = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(claimed, beating.Token);
        try
        {
            await handler(claimed, cancellationToken).ConfigureAwait(false);
            Finish(claimed, JobStatus.Completed, JobFailureCategory.None, null);
        }
        catch (OperationCanceledException)
        {
            Finish(claimed, JobStatus.Cancelled, JobFailureCategory.Cancellation, "The runner stopped.");
        }
        catch (Exception exception)
        {
            Finish(claimed, JobStatus.Failed, JobFailureCategory.Unknown, exception.Message);
        }
        finally
        {
            beating.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task HeartbeatAsync(JobRecord claimed, CancellationToken stopping)
    {
        var interval = _lease < HeartbeatShare * 3 ? _lease / 3 : HeartbeatShare;
        while (!stopping.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            _claims.Renew(claimed.JobId, claimed.ClaimToken!, Now(), _lease);
        }
    }

    private void Finish(JobRecord claimed, JobStatus status, JobFailureCategory category, string? detail)
    {
        try
        {
            if (!_claims.Finish(claimed.JobId, claimed.ClaimToken!, status, category,
                    detail is null ? null : JsonSerializer.Serialize(new { detail })))
                Console.Error.WriteLine(claimed.JobId + " is no longer this runner's to finish.");
        }
        catch (Exception exception)
        {
            // Reported, not thrown: one unfinishable job must not strand every job behind it.
            Console.Error.WriteLine("could not finish " + claimed.JobId + ": " + exception.Message);
        }
    }

    private TimeSpan Jittered(TimeSpan interval) =>
        interval + TimeSpan.FromMilliseconds(_jitter.Next(0, (int)interval.TotalMilliseconds + 1));

    private static string Now() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
