using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Client.Worker;

/// <summary>Provides typed client calls for durable worker jobs.</summary>
public sealed class WorkerJobClient
{
    private readonly WorkerConnection _connection;

    /// <summary>Creates a typed job client over an established worker connection.</summary>
    public WorkerJobClient(WorkerConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>Gets one durable job status and maps a missing row to a typed key failure.</summary>
    public async Task<JobStatusResponse> GetStatusAsync(ProjectLocator project, string jobId,
        CancellationToken cancellationToken)
    {
        var request = new JobStatusRequest(project, jobId);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, WorkerJson.CreateOptions());
        using var requestDocument = JsonDocument.Parse(requestBytes);
        var requestId = Guid.NewGuid().ToString("N");
        var response = await _connection.SendAsync(new WorkerEnvelope(
            requestId, WorkerCommands.JobStatus, requestDocument.RootElement.Clone(),
            _connection.Negotiated.ProtocolVersion), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal) ||
            !string.Equals(response.Command, WorkerCommands.JobStatus, StringComparison.Ordinal) ||
            response.ProtocolVersion != _connection.Negotiated.ProtocolVersion)
            throw new InvalidDataException("The worker returned an unrelated job status response.");
        var result = JsonSerializer.Deserialize<JobStatusResponse>(response.Payload.GetRawText(),
            WorkerJson.CreateOptions()) ?? throw new InvalidDataException("The job status response was empty.");
        if (!result.Found)
            throw new KeyNotFoundException("The requested job was not found.");
        return result;
    }
}
