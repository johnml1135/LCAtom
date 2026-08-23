using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Worker.Projects;

namespace SIL.Motif.Worker.Jobs;

/// <summary>Reads one durable job row from its already-ready keyed project runtime.</summary>
public sealed class JobStatusCommandHandler : IWorkerCommandHandler
{
    private readonly ProjectRuntimeRegistry _runtimes;

    /// <summary>Creates a status handler bound to the worker's runtime registry.</summary>
    public JobStatusCommandHandler(ProjectRuntimeRegistry runtimes)
    {
        _runtimes = runtimes ?? throw new ArgumentNullException(nameof(runtimes));
    }

    /// <inheritdoc />
    public string Command => WorkerCommands.JobStatus;

    /// <inheritdoc />
    public async Task<JsonElement> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<JobStatusRequest>(payload.GetRawText(), WorkerJson.CreateOptions())
            ?? throw new InvalidDataException("The job status request was empty.");
        var workspaceKey = ProjectWorkspaceKey.Compute(request.Project);
        if (!_runtimes.TryGet(workspaceKey, out var runtime))
            throw new InvalidOperationException("The project runtime is not ready.");

        using var operation = await runtime.AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        var record = runtime.Jobs.Get(request.JobId);
        var response = record is null
            ? new JobStatusResponse(request.JobId, workspaceKey, false, null, null, null, null, null, null, null)
            : new JobStatusResponse(record.JobId, record.ProjectKey, true, record.Kind, record.Status,
                record.Attempt, record.UpdatedUtc, record.CancellationRequested, record.FailureCategory,
                record.Version);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WorkerJson.CreateOptions()));
        return document.RootElement.Clone();
    }
}
