using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Worker;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerClientConnectionStageTests
{
    [Fact]
    public async Task MissingPipeIsReportedBeforePeerConnection()
    {
        var exception = await Assert.ThrowsAsync<WorkerConnectionFailureException>(() =>
            new WorkerClient().ConnectAsync("motif-missing-" + Guid.NewGuid().ToString("N"), Handshake(),
                TimeSpan.FromMilliseconds(100), CancellationToken.None));

        Assert.Equal(WorkerConnectionFailureStage.BeforePeerConnection, exception.Stage);
    }

    [Fact]
    public async Task ConnectedPipeClosedBeforeHandshakeIsReportedAfterPeerConnection()
    {
        var pipeName = "motif-stage-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(pipeName);
        var serverTask = AcceptAndCloseAsync(server);

        var exception = await Assert.ThrowsAsync<WorkerConnectionFailureException>(() =>
            new WorkerClient().ConnectAsync(pipeName, Handshake(), TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.Equal(WorkerConnectionFailureStage.AfterPeerConnection, exception.Stage);
        await serverTask;
    }

    [Fact]
    public async Task ConnectedPipeWithMalformedPrefixIsReportedAfterPeerConnection()
    {
        var pipeName = "motif-stage-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(pipeName);
        var serverTask = WriteAndCloseAsync(server, new byte[] { 0xff, 0xff, 0xff, 0x7f });

        var exception = await Assert.ThrowsAsync<WorkerConnectionFailureException>(() =>
            new WorkerClient().ConnectAsync(pipeName, Handshake(), TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.Equal(WorkerConnectionFailureStage.AfterPeerConnection, exception.Stage);
        await serverTask;
    }

    [Fact]
    public async Task ConnectedPipeWithIncompatibleHandshakeIsReportedAfterPeerConnection()
    {
        var pipeName = "motif-stage-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(pipeName);
        var serverTask = ReadHandshakeAndWriteOfferAsync(server, new WorkerHandshakeOffer(
            "0.0.1", new ProtocolRange(2, 2), Array.Empty<string>()));

        var exception = await Assert.ThrowsAsync<WorkerConnectionFailureException>(() =>
            new WorkerClient().ConnectAsync(pipeName, Handshake(), TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.Equal(WorkerConnectionFailureStage.AfterPeerConnection, exception.Stage);
        await serverTask;
    }

    [Fact]
    public async Task ConnectedPipeThatTimesOutBeforeOfferIsReportedAfterPeerConnection()
    {
        var pipeName = "motif-stage-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(pipeName);
        var connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            connected.TrySetResult(true);
            await Task.Delay(TimeSpan.FromSeconds(2));
        });

        var connect = new WorkerClient().ConnectAsync(pipeName, Handshake(),
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var exception = await Assert.ThrowsAsync<WorkerConnectionFailureException>(() => connect);

        Assert.Equal(WorkerConnectionFailureStage.AfterPeerConnection, exception.Stage);
        await serverTask;
    }

    private static WorkerHandshakeRequest Handshake() => new WorkerHandshakeRequest(
        "stage-test", "0.0.1", new ProtocolRange(1, 1), Array.Empty<string>());

    private static NamedPipeServerStream NewServer(string name) => new NamedPipeServerStream(
        name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task AcceptAndCloseAsync(NamedPipeServerStream server)
    {
        await server.WaitForConnectionAsync();
    }

    private static async Task WriteAndCloseAsync(NamedPipeServerStream server, byte[] bytes)
    {
        await server.WaitForConnectionAsync();
        await server.WriteAsync(bytes);
        await server.FlushAsync();
    }

    private static async Task ReadHandshakeAndWriteOfferAsync(NamedPipeServerStream server,
        WorkerHandshakeOffer offer)
    {
        await server.WaitForConnectionAsync();
        var prefix = new byte[4];
        await ReadExactlyAsync(server, prefix);
        var length = prefix[0] | prefix[1] << 8 | prefix[2] << 16 | prefix[3] << 24;
        var payload = new byte[length];
        await ReadExactlyAsync(server, payload);
        var response = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(offer);
        var output = new byte[4 + response.Length];
        output[0] = (byte)response.Length;
        output[1] = (byte)(response.Length >> 8);
        output[2] = (byte)(response.Length >> 16);
        output[3] = (byte)(response.Length >> 24);
        response.CopyTo(output, 4);
        await server.WriteAsync(output);
        await server.FlushAsync();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
            if (count == 0)
                throw new EndOfStreamException();
            offset += count;
        }
    }
}
