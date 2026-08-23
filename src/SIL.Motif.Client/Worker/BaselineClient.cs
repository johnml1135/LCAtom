using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Client.Worker;

/// <summary>Signals a classified refusal from a Baseline control command.</summary>
public sealed class BaselineCommandException : Exception
{
    /// <summary>Creates an exception from the worker's closed failure response.</summary>
    public BaselineCommandException(BaselineCommandFailure failure)
        : base((failure ?? throw new ArgumentNullException(nameof(failure))).Message)
    {
        Failure = failure;
    }

    /// <summary>The classified, retry-aware failure returned by the worker.</summary>
    public BaselineCommandFailure Failure { get; }
}

/// <summary>Provides typed Baseline offer and publication calls over an established worker connection.</summary>
public sealed class BaselineClient
{
    private const string RequiredCapability = "baseline.v1";
    private readonly WorkerConnection _connection;

    /// <summary>Creates a typed Baseline client over an established worker connection.</summary>
    public BaselineClient(WorkerConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>Uploads and publishes one Baseline bundle through the worker-issued transfer offer.</summary>
    public async Task<BaselinePublicationResult> PublishAsync(ProjectLocator project, Stream bundle,
        BaselineToken token, CancellationToken cancellationToken)
    {
        RequireCapability();
        if (bundle is null)
            throw new ArgumentNullException(nameof(bundle));
        var offer = await RequestOfferAsync(project, cancellationToken).ConfigureAwait(false);
        await _connection.UploadAsync(offer, bundle, cancellationToken).ConfigureAwait(false);
        return await PublishTransferAsync(project, offer.TransferId, token, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BinaryTransferOffer> RequestOfferAsync(ProjectLocator project,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync<BaselineOfferRequest, BaselineOfferResponse>(
            WorkerCommands.BaselineOffer, new BaselineOfferRequest(project), cancellationToken)
            .ConfigureAwait(false);
        if (response.Failure is not null)
            throw new BaselineCommandException(response.Failure);
        return response.Offer!;
    }

    private async Task<BaselinePublicationResult> PublishTransferAsync(ProjectLocator project, string transferId,
        BaselineToken token, CancellationToken cancellationToken)
    {
        var response = await SendAsync<BaselinePublishRequest, BaselinePublishResponse>(
            WorkerCommands.BaselinePublish, new BaselinePublishRequest(project, transferId, token),
            cancellationToken).ConfigureAwait(false);
        if (response.Failure is not null)
            throw new BaselineCommandException(response.Failure);
        return response.Publication!;
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(string command, TRequest request,
        CancellationToken cancellationToken)
    {
        var options = WorkerJson.CreateOptions();
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, options);
        using var requestDocument = JsonDocument.Parse(requestBytes);
        var requestId = Guid.NewGuid().ToString("N");
        var protocolVersion = _connection.Negotiated.ProtocolVersion;
        var response = await _connection.SendAsync(new WorkerEnvelope(
            requestId, command, requestDocument.RootElement.Clone(), protocolVersion), cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal) ||
            !string.Equals(response.Command, command, StringComparison.Ordinal) ||
            response.ProtocolVersion != protocolVersion)
            throw new InvalidDataException("The worker returned an unrelated Baseline response.");
        return JsonSerializer.Deserialize<TResponse>(response.Payload.GetRawText(), options) ??
            throw new InvalidDataException("The Baseline response was empty.");
    }

    private void RequireCapability()
    {
        if (!_connection.Negotiated.Capabilities.Contains(RequiredCapability, StringComparer.Ordinal))
            throw new InvalidOperationException("The worker connection did not negotiate baseline.v1.");
    }
}
