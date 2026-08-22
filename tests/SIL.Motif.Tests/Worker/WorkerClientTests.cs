using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Worker;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerClientTests
{
    [Fact]
    public async Task ConnectSendAndCompleteEventUsesOneCorrelatedResponse()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            var handshake = await ReadJsonAsync(stream);
            Assert.Equal("client-1", handshake.GetProperty("ClientId").GetString());
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("3.5.0", new ProtocolRange(1, 1), Array.Empty<string>()));

            var request = await ReadJsonAsync(stream);
            var eventPayload = JsonDocument.Parse("{\"step\":1}").RootElement.Clone();
            await WriteJsonAsync(stream, new WorkerEventEnvelope("event-1", WorkerCommands.ApplyRequested, eventPayload, 1));
            var completion = await ReadJsonAsync(stream);
            Assert.Equal("event-1", completion.GetProperty("EventId").GetString());
            Assert.Equal("Accepted", completion.GetProperty("Outcome").GetString());
            await WriteJsonAsync(stream, new WorkerEnvelope(request.GetProperty("RequestId").GetString()!,
                WorkerCommands.Handshake, request.GetProperty("Payload").Clone(), 1));
        });

        var handshakeRequest = new WorkerHandshakeRequest("client-1", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>());
        using var connection = await new WorkerClient().ConnectAsync(pipeName, handshakeRequest, TimeSpan.FromSeconds(5), CancellationToken.None);
        var events = new ConcurrentQueue<WorkerEventEnvelope>();
        var eventSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.EventReceived += (_, value) =>
        {
            events.Enqueue(value);
            eventSeen.TrySetResult(true);
        };

        var payload = JsonDocument.Parse("{\"value\":42}").RootElement.Clone();
        var responseTask = connection.SendAsync(new WorkerEnvelope("request-1", WorkerCommands.Handshake, payload, 1), CancellationToken.None);
        await eventSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.CompleteEventAsync(new WorkerEventResultEnvelope("event-1", WorkerEventOutcome.Accepted,
            JsonDocument.Parse("{}").RootElement.Clone(), 1), CancellationToken.None);
        var response = await responseTask;

        Assert.Equal("request-1", response.RequestId);
        Assert.Single(events);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DuplicateOrUnknownEventCompletionsAreRefused()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            await WriteJsonAsync(stream, new WorkerEventEnvelope("event-1", WorkerCommands.ApplyRequested,
                JsonDocument.Parse("{}").RootElement.Clone(), 1));
            await ReadJsonAsync(stream);
        });

        using var connection = await new WorkerClient().ConnectAsync(pipeName,
            new WorkerHandshakeRequest("client-1", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        var eventSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.EventReceived += (_, _) => eventSeen.TrySetResult(true);
        await eventSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = new WorkerEventResultEnvelope("event-1", WorkerEventOutcome.Accepted,
            JsonDocument.Parse("{}").RootElement.Clone(), 1);
        await connection.CompleteEventAsync(result, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.CompleteEventAsync(result, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.CompleteEventAsync(
            new WorkerEventResultEnvelope("unknown", WorkerEventOutcome.Accepted, JsonDocument.Parse("{}").RootElement.Clone(), 1),
            CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MismatchedResponseIdRejectsPendingRequest()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerEnvelope("wrong", WorkerCommands.Handshake,
                JsonDocument.Parse("{}").RootElement.Clone(), 1));
        });

        using var connection = await new WorkerClient().ConnectAsync(pipeName,
            new WorkerHandshakeRequest("client-1", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        var pending = connection.SendAsync(new WorkerEnvelope("request-1", WorkerCommands.Handshake,
            JsonDocument.Parse("{}").RootElement.Clone(), 1), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationClosesConnection()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            try
            {
                await ReadJsonAsync(stream);
            }
            catch (EndOfStreamException)
            {
            }
        });

        using var connection = await new WorkerClient().ConnectAsync(pipeName,
            new WorkerHandshakeRequest("client-1", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SendAsync(
            new WorkerEnvelope("request-1", WorkerCommands.Handshake, JsonDocument.Parse("{}").RootElement.Clone(), 1),
            cancellation.Token));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UploadValidatesLengthDigestExpiryAndSingleUse()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var binaryPipeName = "motif-binary-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var binaryServer = new NamedPipeServerStream(binaryPipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var bytes = Encoding.UTF8.GetBytes("binary payload");
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            await binaryServer.WaitForConnectionAsync();
            var received = new MemoryStream();
            await binaryServer.CopyToAsync(received);
            Assert.Equal(bytes, received.ToArray());
            await ReadJsonAsync(stream);
            binaryServer.Dispose();
        });
        using var connection = await new WorkerClient().ConnectAsync(pipeName,
            new WorkerHandshakeRequest("client-1", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        var offer = new BinaryTransferOffer("transfer-1", "upload", binaryPipeName, bytes.Length, DateTimeOffset.UtcNow.AddMinutes(1));
        var expired = new BinaryTransferOffer("transfer-2", "upload", binaryPipeName, bytes.Length, DateTimeOffset.UtcNow.AddMinutes(-1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.UploadAsync(expired, new MemoryStream(bytes), CancellationToken.None));
        using var source = new MemoryStream(bytes);
        await connection.UploadAsync(offer, source, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.UploadAsync(offer, new MemoryStream(bytes), CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UploadRejectsExcessBytes()
    {
        var controlName = "motif-client-" + Guid.NewGuid().ToString("N");
        var binaryName = "motif-binary-" + Guid.NewGuid().ToString("N");
        var control = new NamedPipeServerStream(controlName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var binary = new NamedPipeServerStream(binaryName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var serverTask = RunServerAsync(control, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            await binary.WaitForConnectionAsync();
            await binary.CopyToAsync(Stream.Null);
            binary.Dispose();
        });
        using var connection = await new WorkerClient().ConnectAsync(controlName,
            new WorkerHandshakeRequest("client-1", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        var offer = new BinaryTransferOffer("transfer-excess", "upload", binaryName, 1, DateTimeOffset.UtcNow.AddMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.UploadAsync(offer,
            new MemoryStream(new byte[] { 1, 2 }), CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task RunServerAsync(NamedPipeServerStream server,
        Func<Stream, BinaryTransferOffer, Task> handler)
    {
        await server.WaitForConnectionAsync();
        await handler(server, null!);
        server.Dispose();
    }

    private static async Task<JsonElement> ReadJsonAsync(Stream stream)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix);
        var length = BitConverter.ToInt32(prefix, 0);
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload);
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    private static async Task WriteJsonAsync(Stream stream, object value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var prefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(prefix, 0, prefix.Length);
        await stream.WriteAsync(payload, 0, payload.Length);
        await stream.FlushAsync();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer, offset, buffer.Length - offset);
            if (count == 0)
                throw new EndOfStreamException();
            offset += count;
        }
    }
}
