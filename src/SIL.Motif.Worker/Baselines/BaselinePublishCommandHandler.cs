using System.Data.Common;
using System.Text.Json;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Worker.Projects;

namespace SIL.Motif.Worker.Baselines;

internal sealed class BaselinePublishCommandHandler : IWorkerCommandHandler
{
    private readonly ProjectRuntimeRegistry _runtimes;
    private readonly BaselineTransferRegistry _transfers;
    private readonly BaselineWorkspaceCatalog _workspaces;
    private readonly BaselineBundleReceiver _receiver;
    private readonly Func<ProjectRuntime, string, BaselinePublication, DateTimeOffset, BaselineRecord> _record;
    private readonly string _connectionId;

    public BaselinePublishCommandHandler(ProjectRuntimeRegistry runtimes,
        BaselineTransferRegistry transfers, BaselineWorkspaceCatalog workspaces, string connectionId,
        BaselineBundleReceiver? receiver = null,
        Func<ProjectRuntime, string, BaselinePublication, DateTimeOffset, BaselineRecord>? record = null)
    {
        _runtimes = runtimes ?? throw new ArgumentNullException(nameof(runtimes));
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        _workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A worker connection identity is required.", nameof(connectionId));
        _connectionId = connectionId;
        _receiver = receiver ?? new BaselineBundleReceiver();
        _record = record ?? ((runtime, key, publication, publishedUtc) =>
            runtime.Baselines.Record(key, publication, publishedUtc));
    }

    public string Command => WorkerCommands.BaselinePublish;

    public async Task<JsonElement> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<BaselinePublishRequest>(
            payload.GetRawText(), WorkerJson.CreateOptions())
            ?? throw new InvalidDataException("The Baseline publish request was empty.");
        var workspaceKey = ProjectWorkspaceKey.Compute(request.Project);
        if (!_runtimes.TryGet(workspaceKey, out var runtime))
            return Response(new BaselinePublishResponse(null, Failure(
                BaselineFailureCode.ProjectRuntimeUnavailable, true,
                "The project runtime is unavailable for Baseline publication.")));

        IDisposable operation;
        try
        {
            operation = await runtime.AcquireExclusiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return Response(new BaselinePublishResponse(null, Failure(
                BaselineFailureCode.ProjectRuntimeUnavailable, true,
                "The project runtime is unavailable for Baseline publication.")));
        }

        using (operation)
        {
            BaselinePublicationTarget target;
            try
            {
                target = _workspaces.For(runtime);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or
                UnauthorizedAccessException)
            {
                return Response(new BaselinePublishResponse(null, Failure(
                    BaselineFailureCode.PublicationFailed, true,
                    "The Baseline publication target is unavailable.")));
            }
            if (!StringComparer.Ordinal.Equals(
                    request.Token.ProjectIdentity, runtime.Project.FieldWorksProjectIdentity))
                return Response(new BaselinePublishResponse(null, Failure(
                    BaselineFailureCode.BundleInvalid, false,
                    "The Baseline token identifies another project.")));

            VerifiedBinaryTransfer transfer;
            try
            {
                transfer = _transfers.Claim(
                    _connectionId, request.Project, request.TransferId);
            }
            catch (InvalidOperationException)
            {
                await _transfers.ReleaseClaimAsync(_connectionId, request.TransferId).ConfigureAwait(false);
                return Response(new BaselinePublishResponse(null, Failure(
                    BaselineFailureCode.TransferUnknown, false,
                    "The Baseline transfer is unknown, invalid, or no longer available.")));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await _transfers.ReleaseClaimAsync(_connectionId, request.TransferId).ConfigureAwait(false);
                return Response(new BaselinePublishResponse(null, Failure(
                    BaselineFailureCode.TransferInvalid, false,
                    "The Baseline transfer could not be verified.")));
            }

            try
            {
                BaselineRecord? previous;
                try
                {
                    previous = runtime.Baselines.GetCurrent(workspaceKey);
                }
                catch (Exception exception) when (exception is DbException or IOException or
                    InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
                {
                    return Response(new BaselinePublishResponse(null, Failure(
                        BaselineFailureCode.PublicationFailed, true,
                        "The Baseline publication could not be recorded.")));
                }
                if (previous is not null &&
                    StringComparer.Ordinal.Equals(previous.Token.BundleDigest, request.Token.BundleDigest) &&
                    previous.Token != request.Token)
                {
                    return Response(new BaselinePublishResponse(null, Failure(
                        BaselineFailureCode.BundleInvalid, false,
                        "The Baseline token conflicts with the durable publication.")));
                }
                BaselinePublicationOutcome outcome;
                try
                {
                    outcome = await _receiver.PublishVerifiedWithOutcomeAsync(
                        transfer, request.Token, target, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    return Response(new BaselinePublishResponse(null, Failure(
                        BaselineFailureCode.BundleInvalid, false,
                        "The Baseline bundle failed validation.")));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return Response(new BaselinePublishResponse(null, Failure(
                        BaselineFailureCode.PublicationFailed, true,
                        "The Baseline could not be published.")));
                }
                var publication = outcome.Publication;

                BaselineRecord durable;
                try
                {
                    durable = _record(runtime, workspaceKey, publication, DateTimeOffset.UtcNow);
                }
                catch (Exception exception) when (exception is DbException or IOException or
                    InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
                {
                    if (outcome.Created && !StringComparer.Ordinal.Equals(
                            previous?.Token.BundleDigest, publication.Token.BundleDigest))
                        BaselineBundleReceiver.DeletePublicationIfOwned(publication, target);
                    return Response(new BaselinePublishResponse(null, Failure(
                        BaselineFailureCode.PublicationFailed, true,
                        "The Baseline publication could not be recorded.")));
                }

                return Response(new BaselinePublishResponse(
                    new BaselinePublicationResult(workspaceKey, durable.Token), null));
            }
            finally
            {
                await _transfers.ReleaseClaimAsync(
                    _connectionId, transfer.TransferId).ConfigureAwait(false);
            }
        }
    }

    private static BaselineCommandFailure Failure(
        BaselineFailureCode code, bool retryable, string message) => new(code, retryable, message);

    private static JsonElement Response(BaselinePublishResponse response)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(response, WorkerJson.CreateOptions()));
        return document.RootElement.Clone();
    }
}
