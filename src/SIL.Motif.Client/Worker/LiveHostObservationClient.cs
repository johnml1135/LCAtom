using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Client.Worker;

/// <summary>Maintains one connection-scoped live-project authority lease and its freshness observations.</summary>
public sealed class LiveHostObservationClient
{
    private const string RequiredCapability = "live-host.v1";
    private readonly WorkerConnection _connection;

    public LiveHostObservationClient(WorkerConnection connection) =>
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public Task<LiveHostObservationResponse> RegisterAsync(ProjectLocator project,
        LiveProjectObservation observation, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.LiveHostRegister,
            new LiveHostRegisterRequest(project, observation), cancellationToken);

    public Task<LiveHostObservationResponse> UpdateAsync(ProjectLocator project,
        LiveProjectObservation observation, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.LiveHostObservationUpdate,
            new LiveHostObservationUpdateRequest(project, observation), cancellationToken);

    public Task<LiveHostObservationResponse> DisconnectAsync(ProjectLocator project,
        string hostSessionId, CancellationToken cancellationToken) =>
        SendAsync(WorkerCommands.LiveHostDisconnect,
            new LiveHostDisconnectRequest(project, hostSessionId), cancellationToken);

    private async Task<LiveHostObservationResponse> SendAsync<TRequest>(string command, TRequest request,
        CancellationToken cancellationToken)
    {
        if (!_connection.Negotiated.Capabilities.Contains(RequiredCapability, StringComparer.Ordinal))
            throw new InvalidOperationException("The worker connection did not negotiate live-host.v1.");
        var options = WorkerJson.CreateOptions();
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(request, options));
        var requestId = Guid.NewGuid().ToString("N");
        var protocol = _connection.Negotiated.ProtocolVersion;
        var response = await _connection.SendAsync(new WorkerEnvelope(requestId, command,
            document.RootElement.Clone(), protocol), cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(response.RequestId, requestId) ||
            !StringComparer.Ordinal.Equals(response.Command, command) || response.ProtocolVersion != protocol)
            throw new InvalidDataException("The worker returned an unrelated live-host response.");
        return JsonSerializer.Deserialize<LiveHostObservationResponse>(response.Payload.GetRawText(), options) ??
            throw new InvalidDataException("The live-host response was empty.");
    }
}
