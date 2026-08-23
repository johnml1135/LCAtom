using System.Text.Json;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Worker.Baselines;

internal sealed class BaselineTransferOfferCommandHandler : IWorkerCommandHandler
{
    internal const long MaximumBundleBytes = 512L * 1024 * 1024;
    internal static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(5);
    private readonly BaselineTransferRegistry _transfers;
    private readonly string _connectionId;

    public BaselineTransferOfferCommandHandler(BaselineTransferRegistry transfers, string connectionId)
    {
        _transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A worker connection identity is required.", nameof(connectionId));
        _connectionId = connectionId;
    }

    public string Command => WorkerCommands.BaselineOffer;

    public Task<JsonElement> HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Deserialize<BaselineOfferRequest>(
            payload.GetRawText(), WorkerJson.CreateOptions())
            ?? throw new InvalidDataException("The Baseline offer request was empty.");
        BaselineOfferResponse response;
        try
        {
            var offer = _transfers.CreateOffer(_connectionId, request.Project,
                MaximumBundleBytes, OfferLifetime, cancellationToken);
            response = new BaselineOfferResponse(offer, null);
        }
        catch (BinaryTransferCapacityException exception)
        {
            response = new BaselineOfferResponse(null, new BaselineCommandFailure(
                BaselineFailureCode.CapacityUnavailable, true, exception.Message));
        }
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response, WorkerJson.CreateOptions()));
        return Task.FromResult(document.RootElement.Clone());
    }
}
