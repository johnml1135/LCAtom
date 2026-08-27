using System;
using System.Text;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;

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
}
