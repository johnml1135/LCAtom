using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;

namespace SIL.Motif.Client.Worker;

/// <summary>Identifies whether a connection failure happened before or after pipe connection.</summary>
public enum WorkerConnectionFailureStage
{
    /// <summary>No peer connection was established.</summary>
    BeforePeerConnection,

    /// <summary>The named pipe connected, so the endpoint is authoritative but invalid.</summary>
    AfterPeerConnection
}

/// <summary>Reports a worker connection failure with authoritative transport stage.</summary>
public sealed class WorkerConnectionFailureException : Exception
{
    /// <summary>Creates a staged worker connection failure.</summary>
    public WorkerConnectionFailureException(WorkerConnectionFailureStage stage, string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
    }

    /// <summary>The stage at which the connection failed.</summary>
    public WorkerConnectionFailureStage Stage { get; }
}

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
        var peerConnected = false;
        try
        {
            await WorkerFrame.ConnectAsync(stream, timeoutCancellation.Token).ConfigureAwait(false);
            peerConnected = true;
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
        catch (Exception exception)
        {
            peerConnected |= stream.IsConnected;
            stream.Dispose();
            var stage = peerConnected
                ? WorkerConnectionFailureStage.AfterPeerConnection
                : WorkerConnectionFailureStage.BeforePeerConnection;
            throw new WorkerConnectionFailureException(stage,
                stage == WorkerConnectionFailureStage.AfterPeerConnection
                    ? "The connected worker endpoint returned an invalid response."
                    : "The worker endpoint could not be connected.", exception);
        }
    }
}
