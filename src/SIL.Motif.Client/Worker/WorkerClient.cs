using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Client.Worker;

/// <summary>Connects a cross-runtime client to the user's Motif worker.</summary>
public sealed class WorkerClient
{
    /// <summary>Connects, negotiates the protocol, and starts the control read loop.</summary>
    public async Task<WorkerConnection> ConnectAsync(
        string pipeName,
        WorkerHandshakeRequest handshake,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        if (handshake is null)
            throw new ArgumentNullException(nameof(handshake));
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Connection timeout must be positive.");

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await WorkerFrame.ConnectAsync(stream, timeoutCancellation.Token).ConfigureAwait(false);
            await WorkerFrame.WriteAsync(stream, handshake, timeoutCancellation.Token).ConfigureAwait(false);
            var offerFrame = await WorkerFrame.ReadAsync(stream, timeoutCancellation.Token).ConfigureAwait(false);
            var offer = WorkerFrame.Deserialize<WorkerHandshakeOffer>(offerFrame);
            var negotiated = WorkerHandshake.Negotiate(handshake, offer);
            return new WorkerConnection(stream, negotiated, offer.ConnectionId);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            stream.Dispose();
            throw new OperationCanceledException("The worker connection was cancelled.", exception, cancellationToken);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}
