using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using SIL.Motif.Contract;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.Store;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Model.Effects;
using SIL.Motif.Projection;
using SIL.Motif.Projection.Rendering;
using SIL.Motif.Projection.Usage;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;
using DryRunModel = SIL.Motif.Model.DryRun.DryRun;

namespace SIL.Motif.Cli;

/// <summary>
/// The verbs that put durable work in the queue and read back what became of it.
/// </summary>
/// <remarks>
/// Both open the paired database directly, like every other verb. Enqueueing returns as soon as the row
/// is committed rather than waiting for the work, because the runner that will do it is a different
/// process and may not even be started yet — the row is the whole handoff.
/// </remarks>
public static class JobCommands
{
    /// <summary>The durable kind a queued Baseline refresh carries.</summary>
    public const string BaselineRefreshKind = "baseline-refresh";

    /// <summary>The durable kind a queued Dry Run carries.</summary>
    public const string DryRunKind = "dry-run";

    /// <summary><c>--wait</c>'s default bound before it gives up and reports the job still unfinished.</summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>Queues a Baseline refresh for one project and prints the job id that names it.</summary>
    public static CommandResult EnqueueBaselineRefresh(string fwDataPath, string productVersion)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var jobs = new JobRepository(database);
            var jobId = CanonicalId.Mint("job/").Value;
            var created = jobs.Create(jobId, ProjectWorkspaceKey.Compute(project), BaselineRefreshKind,
                "{}", DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            return new CommandResult(0, created.JobId + Environment.NewLine);
        });
    }

    /// <summary>
    /// Loads and validates the named Proposal through the same <see cref="ProposalRepository.GetFinalized"/>
    /// path <c>show</c> and <c>apply</c> use, refusing before any row is queued when it is absent or
    /// inconsistent, then queues a Dry Run job and prints the job id that names it.
    /// </summary>
    public static CommandResult EnqueueDryRun(string fwDataPath, string productVersion, string proposalId,
        UsageLog? usage = null)
    {
        usage?.Record(DryRunKind,
            new[] { UsageArgumentShape.Text("fwDataPath"), UsageArgumentShape.Text("proposalId") });
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var repository = new ProposalRepository(database);
            ProposalRecord record;
            try
            {
                var id = Commands.NormalizeId(proposalId);
                (record, _) = repository.GetFinalized(CanonicalId.Parse(id));
            }
            catch (Exception exception)
            {
                return Commands.RefuseProposalLoad(exception);
            }

            var jobs = new JobRepository(database);
            var jobId = CanonicalId.Mint("job/").Value;
            var created = jobs.Create(jobId, ProjectWorkspaceKey.Compute(project), DryRunKind,
                record.ProposalJson!, DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            return new CommandResult(0, created.JobId + Environment.NewLine);
        });
    }

    /// <summary>
    /// Polls one Dry Run job until it reaches a terminal state, binds the published anchor onto the
    /// Proposal exactly as the in-process verb used to (so <c>apply</c> keeps working), and renders it.
    /// A job still not terminal when <paramref name="timeout"/> elapses is reported as its own distinct
    /// refusal rather than as though the Dry Run had failed.
    /// </summary>
    public static CommandResult WaitForDryRun(string fwDataPath, string productVersion, string proposalId,
        string jobId, bool asJson, TimeSpan timeout)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, _) =>
        {
            var jobs = new JobRepository(database);
            var deadline = DateTimeOffset.UtcNow + timeout;
            JobRecord? job;
            while (true)
            {
                job = jobs.Get(jobId);
                if (job is null)
                    return ProjectStoreCommand.Refuse(FailureReason.NotFound,
                        "No job '" + jobId + "' is recorded for this project.");
                if (JobStateMachine.IsTerminal(job.Status)) break;
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return ProjectStoreCommand.Refuse(FailureReason.Busy,
                        "Timed out after " + timeout + " waiting for Dry Run job '" + jobId +
                        "' to finish; it is still " + JobStatusJson.ToWire(job.Status) +
                        ". Check again with 'jobs show " + jobId + " --project <fwdata>'.");
                }
                Thread.Sleep(WaitPollInterval);
            }

            if (job.Status != JobStatus.CompletedDryRunOnly || job.DryRunJson is null)
            {
                return ProjectStoreCommand.Refuse(FailureReason.Refused,
                    "Dry Run job '" + jobId + "' finished as " + JobStatusJson.ToWire(job.Status) +
                    " rather than completing.");
            }

            var repository = new ProposalRepository(database);
            var id = Commands.NormalizeId(proposalId);
            var canonicalId = CanonicalId.Parse(id);
            var dryRun = ParsePublishedDryRun(job.DryRunJson);

            // Persist the bound-DryRun anchor (docs/adr/0004 decision 3): apply requires it present and unmoved.
            repository.SetAnchor(canonicalId, JsonSerializer.Serialize(dryRun.Anchor));

            var projection = DryRunProjectionBuilder.Build(id, dryRun);
            return asJson
                ? new CommandResult(0, ProjectionJson.Serialize(projection) + Environment.NewLine)
                : new CommandResult(0, CommandTextRenderer.Render(projection));
        });
    }

    /// <summary>Reports what the durable store currently says about one job.</summary>
    public static CommandResult Show(string fwDataPath, string jobId, string productVersion, bool asJson)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return ProjectStoreCommand.Refuse(FailureReason.InvalidArgument, "A job id is required.");

        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var entry = new JobRepository(database).GetWithQueueOrder(jobId);
            if (entry is null)
                return ProjectStoreCommand.Refuse(FailureReason.NotFound,
                    "No job '" + jobId + "' is recorded for this project.");

            var job = entry.Value.Job;
            var response = new JobStatusResponse(job.JobId, job.ProjectKey, true, job.Kind, job.Status,
                job.Attempt, job.UpdatedUtc, job.CancellationRequested, job.FailureCategory, job.Version,
                entry.Value.QueueOrder);
            return new CommandResult(0, asJson
                ? ProjectionJson.Serialize(response) + Environment.NewLine
                : Render(response));
        });
    }

    /// <summary>
    /// Cancels one job. A job still queued (or parked waiting for a Baseline or the project host) is
    /// moved straight to <c>cancelled</c> — nothing is running it, so no runner is needed. A running job
    /// only has its cancellation flag set; the runner that holds it reads the flag on its own heartbeat
    /// and cancels the handler's token from there, landing in the same terminal state.
    /// </summary>
    public static CommandResult Cancel(string fwDataPath, string jobId, string productVersion, bool asJson)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var jobs = new JobRepository(database);
            var current = jobs.Get(jobId);
            if (current is null)
                return ProjectStoreCommand.Refuse(FailureReason.NotFound,
                    "No job '" + jobId + "' is recorded for this project.");
            if (JobStateMachine.IsTerminal(current.Status))
                return ProjectStoreCommand.Refuse(FailureReason.Refused,
                    "Job '" + jobId + "' already finished as " + JobStatusJson.ToWire(current.Status) +
                    "; there is nothing to cancel.");

            var changed = current.Status == JobStatus.Running
                ? jobs.RequestCancellation(jobId, current.Version)
                : jobs.Transition(jobId, JobStatus.Cancelled, current.Version, JobFailureCategory.Cancellation);

            var response = new JobStatusResponse(changed.JobId, changed.ProjectKey, true, changed.Kind,
                changed.Status, changed.Attempt, changed.UpdatedUtc, changed.CancellationRequested,
                changed.FailureCategory, changed.Version);
            return new CommandResult(0, asJson
                ? ProjectionJson.Serialize(response) + Environment.NewLine
                : Render(response));
        });
    }

    /// <summary>Starts a fresh attempt of a terminal job's lineage, claimable exactly like a new job.</summary>
    public static CommandResult Requeue(string fwDataPath, string jobId, string productVersion, bool asJson)
    {
        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var jobs = new JobRepository(database);
            var current = jobs.Get(jobId);
            if (current is null)
                return ProjectStoreCommand.Refuse(FailureReason.NotFound,
                    "No job '" + jobId + "' is recorded for this project.");
            if (!JobStateMachine.IsTerminal(current.Status))
                return ProjectStoreCommand.Refuse(FailureReason.Refused,
                    "Job '" + jobId + "' is still " + JobStatusJson.ToWire(current.Status) +
                    "; only a finished job can be requeued.");

            var retried = jobs.Retry(jobId, current.Version);
            var response = new JobStatusResponse(retried.JobId, retried.ProjectKey, true, retried.Kind,
                retried.Status, retried.Attempt, retried.UpdatedUtc, retried.CancellationRequested,
                retried.FailureCategory, retried.Version);
            return new CommandResult(0, asJson
                ? ProjectionJson.Serialize(response) + Environment.NewLine
                : Render(response));
        });
    }

    /// <summary>Every active job across every Known project, in the order <see cref="JobClaims.Claim"/> takes it.</summary>
    public static CommandResult ListAll(string productVersion, bool asJson)
    {
        var entries = ReadGlobalActiveQueue(productVersion);
        var response = new JobQueueListResponse(entries.Select(entry => new JobQueueEntryResponse(
            entry.Job.JobId, entry.Job.ProjectKey, entry.Project.FullFwDataPath, entry.Job.Kind,
            entry.Job.Status, entry.Job.Attempt, entry.Job.UpdatedUtc, entry.QueueOrder)).ToArray());
        return new CommandResult(0, asJson
            ? ProjectionJson.Serialize(response) + Environment.NewLine
            : RenderQueueList(response));
    }

    /// <summary>
    /// Repositions one job in the single global queue by writing its own <c>QueueOrder</c> alone — one
    /// row, in this job's own project database, every time. A neighbour's row is never touched: it usually
    /// lives in a different project's database, and no transaction spans two SQLite files, so a write that
    /// depended on a neighbour's row too could half-complete and reorder a job nobody asked to move.
    /// </summary>
    /// <remarks>
    /// <c>--before</c>'s ordinary case is a midpoint between the named job and its global predecessor. A
    /// predecessor tied with it has no midpoint — but a tie means their relative order was already
    /// arbitrary (decided only by <c>JobId</c>, nothing the caller chose), so landing the mover just below
    /// the target, ahead of the whole tied run, satisfies "before target" without needing to touch the
    /// predecessor's row at all.
    /// </remarks>
    public static CommandResult Move(string fwDataPath, string jobId, string productVersion,
        JobMoveTarget target, bool asJson)
    {
        if (target.Kind == JobMoveKind.Before && string.Equals(target.BeforeJobId, jobId, StringComparison.Ordinal))
            return ProjectStoreCommand.Refuse(FailureReason.InvalidArgument, "A job cannot be moved before itself.");

        return ProjectStoreCommand.Run(fwDataPath, productVersion, (database, project) =>
        {
            var jobs = new JobRepository(database);
            var mover = jobs.GetWithQueueOrder(jobId);
            if (mover is null)
                return ProjectStoreCommand.Refuse(FailureReason.NotFound,
                    "No job '" + jobId + "' is recorded for this project.");
            if (JobStateMachine.IsTerminal(mover.Value.Job.Status))
                return ProjectStoreCommand.Refuse(FailureReason.Refused,
                    "Job '" + jobId + "' already finished; a terminal job's position cannot be changed.");

            var others = ReadGlobalActiveQueue(productVersion)
                .Where(entry => !string.Equals(entry.Job.JobId, jobId, StringComparison.Ordinal)).ToArray();

            double newOrder;
            switch (target.Kind)
            {
                case JobMoveKind.ToTop:
                    newOrder = others.Length == 0 ? mover.Value.QueueOrder : others[0].QueueOrder - 1.0;
                    break;
                case JobMoveKind.ToBottom:
                    newOrder = others.Length == 0 ? mover.Value.QueueOrder : others[^1].QueueOrder + 1.0;
                    break;
                default:
                    var targetIndex = Array.FindIndex(others,
                        entry => string.Equals(entry.Job.JobId, target.BeforeJobId, StringComparison.Ordinal));
                    if (targetIndex < 0)
                        return ProjectStoreCommand.Refuse(FailureReason.NotFound,
                            "No active job '" + target.BeforeJobId + "' is recorded to move before.");
                    newOrder = QueueOrderBefore(others, targetIndex);
                    break;
            }

            var moved = jobs.SetQueueOrder(jobId, newOrder, mover.Value.Job.Version, NowStamp());
            var response = new JobStatusResponse(moved.JobId, moved.ProjectKey, true, moved.Kind, moved.Status,
                moved.Attempt, moved.UpdatedUtc, moved.CancellationRequested, moved.FailureCategory,
                moved.Version, newOrder);
            return new CommandResult(0, asJson
                ? ProjectionJson.Serialize(response) + Environment.NewLine
                : Render(response));
        });
    }

    /// A tie has no midpoint; landing just below the target puts the mover ahead of the whole tied run.
    private static double QueueOrderBefore(IReadOnlyList<GlobalQueueEntry> others, int targetIndex)
    {
        var target = others[targetIndex];
        if (targetIndex == 0) return target.QueueOrder - 1.0;

        var predecessor = others[targetIndex - 1];
        return predecessor.QueueOrder < target.QueueOrder
            ? (predecessor.QueueOrder + target.QueueOrder) / 2.0
            : target.QueueOrder - 0.5;
    }

    /// Every active job in every reachable Known project, in global QueueOrder-then-JobId order.
    private static IReadOnlyList<GlobalQueueEntry> ReadGlobalActiveQueue(string productVersion)
    {
        using var machine = MachineDatabase.Open(RunnerOptions.ResolveRoot());
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, ParseProductVersion(productVersion));
        var entries = new List<GlobalQueueEntry>();
        foreach (var known in new KnownProjectRegistry(machine).List())
        {
            if (!File.Exists(known.FullFwDataPath)) continue;
            var locator = new ProjectLocator(known.FullFwDataPath, Path.GetFileNameWithoutExtension(known.FullFwDataPath));
            try
            {
                using var database = catalog.OpenOwned(locator);
                foreach (var entry in new JobRepository(database).ListActiveByQueueOrder(known.WorkspaceKey))
                    entries.Add(new GlobalQueueEntry(known, entry.Job, entry.QueueOrder));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
            {
                // Reported, not thrown: one unreadable project must not hide every other project's queue.
                Console.Error.WriteLine("warning: '" + known.FullFwDataPath + "' could not be read (" +
                    exception.Message + "); its jobs are not shown.");
            }
        }
        return entries.OrderBy(entry => entry.QueueOrder).ThenBy(entry => entry.Job.JobId, StringComparer.Ordinal)
            .ToArray();
    }

    private static Version ParseProductVersion(string productVersion) =>
        Version.TryParse(productVersion, out var parsed) ? parsed : new Version(1, 0);

    private static string NowStamp() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string Render(JobStatusResponse response)
    {
        var text = new StringBuilder();
        text.AppendLine("Job " + response.JobId);
        text.AppendLine("  Kind:    " + response.Kind);
        text.AppendLine("  Status:  " + response.Status);
        text.AppendLine("  Attempt: " + response.Attempt);
        text.AppendLine("  Updated: " + response.UpdatedUtc);
        if (response.QueueOrder is { } queueOrder)
            text.AppendLine("  Queue order: " + queueOrder.ToString("R"));
        return text.ToString();
    }

    private static string RenderQueueList(JobQueueListResponse response)
    {
        var text = new StringBuilder();
        if (response.Jobs.Count == 0)
        {
            text.AppendLine("No active jobs.");
            return text.ToString();
        }
        for (var index = 0; index < response.Jobs.Count; index++)
        {
            var job = response.Jobs[index];
            text.AppendLine((index + 1) + ". " + job.JobId + "  " + job.Kind + "  " +
                JobStatusJson.ToWire(job.Status) + "  " + job.ProjectPath);
        }
        return text.ToString();
    }

    /// <summary>One active job read while assembling the cross-project queue view.</summary>
    private readonly record struct GlobalQueueEntry(KnownProjectRecord Project, JobRecord Job, double QueueOrder);

    // Reads a published Dry Run's JSON back into the model the renderer takes.
    private static DryRunModel ParsePublishedDryRun(string dryRunJson)
    {
        using var document = JsonDocument.Parse(dryRunJson);
        var root = document.RootElement;
        var anchor = JsonSerializer.Deserialize<BoundDryRunAnchor>(
            root.GetProperty("anchor").GetRawText(), MotifJson.CreateOptions())!;
        return new DryRunModel(
            root.GetProperty("intentDigest").GetString()!,
            root.GetProperty("baselineNote").GetString()!,
            ParseExpectedEffects(root.GetProperty("expectedEffects")),
            root.GetProperty("effectDigest").GetString()!,
            anchor);
    }

    private static IReadOnlyList<ExpectedEffect> ParseExpectedEffects(JsonElement array)
    {
        var effects = new List<ExpectedEffect>();
        foreach (var element in array.EnumerateArray())
        {
            effects.Add(new ExpectedEffect(
                CanonicalId.Parse(element.GetProperty("canonicalId").GetString()!),
                element.GetProperty("field").GetString()!,
                ReadAlternatives(element.GetProperty("before")),
                ReadAlternatives(element.GetProperty("after"))));
        }
        return effects;
    }

    private static IReadOnlyDictionary<string, string> ReadAlternatives(JsonElement alternatives)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in alternatives.EnumerateObject())
            map[property.Name] = property.Value.GetString() ?? "";
        return map;
    }
}

/// <summary>Which end of the global queue <c>jobs move</c> targets.</summary>
public enum JobMoveKind
{
    ToTop,
    ToBottom,
    Before
}

/// <summary>One <c>jobs move</c> invocation's destination: an end of the queue, or immediately before a named job.</summary>
public readonly record struct JobMoveTarget(JobMoveKind Kind, string? BeforeJobId)
{
    public static JobMoveTarget ToTop() => new(JobMoveKind.ToTop, null);
    public static JobMoveTarget ToBottom() => new(JobMoveKind.ToBottom, null);
    public static JobMoveTarget Before(string jobId) => new(JobMoveKind.Before, jobId);
}
