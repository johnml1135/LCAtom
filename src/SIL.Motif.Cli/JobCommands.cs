using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using SIL.Motif.Contract;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Model.DryRun;
using SIL.Motif.Model.Effects;
using SIL.Motif.Projection;
using SIL.Motif.Projection.Rendering;
using SIL.Motif.Projection.Usage;
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
            var job = new JobRepository(database).Get(jobId);
            if (job is null)
                return ProjectStoreCommand.Refuse(FailureReason.NotFound,
                    "No job '" + jobId + "' is recorded for this project.");

            var response = new JobStatusResponse(job.JobId, job.ProjectKey, true, job.Kind, job.Status,
                job.Attempt, job.UpdatedUtc, job.CancellationRequested, job.FailureCategory, job.Version);
            return new CommandResult(0, asJson
                ? ProjectionJson.Serialize(response) + Environment.NewLine
                : Render(response));
        });
    }

    private static string Render(JobStatusResponse response)
    {
        var text = new StringBuilder();
        text.AppendLine("Job " + response.JobId);
        text.AppendLine("  Kind:    " + response.Kind);
        text.AppendLine("  Status:  " + response.Status);
        text.AppendLine("  Attempt: " + response.Attempt);
        text.AppendLine("  Updated: " + response.UpdatedUtc);
        return text.ToString();
    }

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
