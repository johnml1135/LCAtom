using System;
using System.IO;
using System.Text;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Responses;
using SIL.Motif.Host.Store;
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
        return WithDatabase(fwDataPath, productVersion, (database, project) =>
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
            return Refuse(FailureReason.InvalidArgument, "A job id is required.");

        return WithDatabase(fwDataPath, productVersion, (database, project) =>
        {
            var job = new JobRepository(database).Get(jobId);
            if (job is null)
                return Refuse(FailureReason.NotFound, "No job '" + jobId + "' is recorded for this project.");

            var response = new JobStatusResponse(job.JobId, job.ProjectKey, true, job.Kind, job.Status,
                job.Attempt, job.UpdatedUtc, job.CancellationRequested, job.FailureCategory, job.Version);
            return new CommandResult(0, asJson
                ? ProjectionJson.Serialize(response) + Environment.NewLine
                : Render(response));
        });
    }

    private static CommandResult WithDatabase(string fwDataPath, string productVersion,
        Func<MotifDatabase, ProjectLocator, CommandResult> act)
    {
        ProjectLocator project;
        try
        {
            project = Locate(fwDataPath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return Refuse(FailureReason.InvalidArgument, exception.Message);
        }

        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, ParseVersion(productVersion));
        try
        {
            using var database = catalog.OpenOwned(project);
            return act(database, project);
        }
        catch (IOException exception)
        {
            // Another process holds the database; the caller may try again once it lets go.
            return Refuse(FailureReason.Busy, exception.Message);
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidDataException)
        {
            return Refuse(FailureReason.Refused, exception.Message);
        }
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

    private static CommandResult Refuse(FailureReason reason, string message) =>
        new CommandResult(FailureEnvelope.ExitCodeFor(reason),
            "error: " + message + Environment.NewLine, reason);

    /// A malformed product version must not stop a queue read; the floor it feeds is a lower bound.
    private static Version ParseVersion(string productVersion) =>
        Version.TryParse(productVersion, out var parsed) ? parsed : new Version(1, 0);

    /// The file must exist: an unresolvable path would key a second, empty workspace instead of the real one.
    private static ProjectLocator Locate(string fwDataPath)
    {
        var full = Path.GetFullPath(fwDataPath);
        if (!File.Exists(full))
            throw new FileNotFoundException("Project file not found: '" + full + "'.", full);
        return new ProjectLocator(full, Path.GetFileNameWithoutExtension(full));
    }
}
