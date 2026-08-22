using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    public async Task ReplayedCompletedEventClosesConnectionWithoutSecondCompletion()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            var eventEnvelope = new WorkerEventEnvelope("event-replay", WorkerCommands.ApplyRequested,
                JsonDocument.Parse("{}").RootElement.Clone(), 1);
            await WriteJsonAsync(stream, eventEnvelope);
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, eventEnvelope);
            try
            {
                await ReadJsonAsync(stream);
                throw new InvalidOperationException("The client accepted a replayed event result.");
            }
            catch (EndOfStreamException)
            {
            }
        });
        using var connection = await ConnectAsync(pipeName);
        var received = 0;
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.EventReceived += (_, _) =>
        {
            Interlocked.Increment(ref received);
            seen.TrySetResult(true);
        };
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await connection.CompleteEventAsync(new WorkerEventResultEnvelope("event-replay", WorkerEventOutcome.Accepted,
            JsonDocument.Parse("{}").RootElement.Clone(), 1), CancellationToken.None);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, received);
    }

    [Fact]
    public async Task EventsRemainOrderedAcrossSubscriptionChanges()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            for (var index = 1; index <= 3; index++)
            {
                await WriteJsonAsync(stream, new WorkerEventEnvelope("event-" + index, WorkerCommands.ApplyRequested,
                    JsonDocument.Parse("{}").RootElement.Clone(), 1));
            }
            sent.TrySetResult(true);
            try
            {
                await ReadJsonAsync(stream);
            }
            catch (EndOfStreamException)
            {
            }
        });
        using var connection = await ConnectAsync(pipeName);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = new List<string>();
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        EventHandler<WorkerEventEnvelope> firstHandler = (_, value) =>
        {
            first.Add(value.EventId);
            firstStarted.TrySetResult(true);
            release.Wait(TimeSpan.FromSeconds(5));
        };
        connection.EventReceived += firstHandler;
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        connection.EventReceived -= firstHandler;
        var remaining = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new List<string>();
        connection.EventReceived += (_, value) =>
        {
            second.Add(value.EventId);
            if (second.Count == 2)
                remaining.TrySetResult(true);
        };
        release.Set();
        await remaining.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "event-1" }, first);
        Assert.Equal(new[] { "event-2", "event-3" }, second);
        connection.Dispose();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EventQueueOverflowFaultsConnectionCompletion()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            try
            {
                for (var index = 0; index < 129; index++)
                {
                    await WriteJsonAsync(stream, new WorkerEventEnvelope("overflow-" + index,
                        WorkerCommands.ApplyRequested, JsonDocument.Parse("{}").RootElement.Clone(), 1));
                }
            }
            catch (IOException)
            {
            }
        });
        using var connection = await ConnectAsync(pipeName);
        await Assert.ThrowsAsync<WorkerEventQueueOverflowException>(() =>
            connection.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SubscriberExceptionsReachCompletionAfterLaterSubscribersRun()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            await WriteJsonAsync(stream, new WorkerEventEnvelope("handler-failure", WorkerCommands.ApplyRequested,
                JsonDocument.Parse("{}").RootElement.Clone(), 1));
            try
            {
                await ReadJsonAsync(stream);
            }
            catch (EndOfStreamException)
            {
            }
        });
        using var connection = await ConnectAsync(pipeName);
        var laterSubscriber = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<WorkerEventEnvelope> first = (_, _) => throw new InvalidOperationException("handler failure");
        EventHandler<WorkerEventEnvelope> second = (_, _) => laterSubscriber.TrySetResult(true);
        connection.EventReceived += first;
        connection.EventReceived += second;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            connection.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await laterSubscriber.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("handler failure", exception.Message);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompletionFaultsForMalformedPeerFrameWithoutPendingRequest()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            await WriteRawFrameAsync(stream, Encoding.UTF8.GetBytes("{"));
        });
        using var connection = await ConnectAsync(pipeName);
        await Assert.ThrowsAnyAsync<JsonException>(() => connection.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompletionFaultsForDuplicateEventWithoutPendingRequest()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            var eventEnvelope = new WorkerEventEnvelope("duplicate-terminal", WorkerCommands.ApplyRequested,
                JsonDocument.Parse("{}").RootElement.Clone(), 1);
            await WriteJsonAsync(stream, eventEnvelope);
            await WriteJsonAsync(stream, eventEnvelope);
        });
        using var connection = await ConnectAsync(pipeName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompletionFaultsForPeerEofWithoutPendingRequest()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
        });
        using var connection = await ConnectAsync(pipeName);
        await Assert.ThrowsAsync<EndOfStreamException>(() => connection.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExplicitDisposeCompletesTerminalTaskNormallyAndIsRepeatable()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
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
        using var connection = await ConnectAsync(pipeName);
        connection.Dispose();
        connection.Dispose();
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BlockingOrThrowingEventHandlerDoesNotBlockOrCorruptResponses()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            var request = await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerEventEnvelope("event-blocking", WorkerCommands.ApplyRequested,
                JsonDocument.Parse("{}").RootElement.Clone(), 1));
            await WriteJsonAsync(stream, new WorkerEnvelope(request.GetProperty("RequestId").GetString()!,
                WorkerCommands.Handshake, JsonDocument.Parse("{}").RootElement.Clone(), 1));
        });
        using var connection = await ConnectAsync(pipeName);
        var handlerStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        connection.EventReceived += (_, _) =>
        {
            handlerStarted.TrySetResult(true);
            release.Wait(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("Handler failure is isolated from framing.");
        };
        var responseTask = connection.SendAsync(new WorkerEnvelope("request-blocking", WorkerCommands.Handshake,
            JsonDocument.Parse("{}").RootElement.Clone(), 1), CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromSeconds(2)));
        release.Set();
        Assert.Same(responseTask, completed);
        Assert.Equal("request-blocking", (await responseTask).RequestId);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EventResultWithMismatchedProtocolIsRejected()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            await WriteJsonAsync(stream, new WorkerEventEnvelope("event-protocol", WorkerCommands.ApplyRequested,
                JsonDocument.Parse("{}").RootElement.Clone(), 1));
            try
            {
                await ReadJsonAsync(stream);
            }
            catch (EndOfStreamException)
            {
            }
        });
        using var connection = await ConnectAsync(pipeName);
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.EventReceived += (_, _) => seen.TrySetResult(true);
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.CompleteEventAsync(
            new WorkerEventResultEnvelope("event-protocol", WorkerEventOutcome.Accepted,
                JsonDocument.Parse("{}").RootElement.Clone(), 2), CancellationToken.None));
        connection.Dispose();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OutboundFrameUsesLittleEndianPrefixAndRejectsExcessPayload()
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            var prefix = new byte[4];
            await ReadExactlyAsync(stream, prefix);
            var length = ReadInt32LittleEndian(prefix);
            Assert.Equal(new byte[] { (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24) }, prefix);
            var payload = new byte[length];
            await ReadExactlyAsync(stream, payload);
            using var request = JsonDocument.Parse(payload);
            Assert.Equal("prefix-check", request.RootElement.GetProperty("RequestId").GetString());
            await WriteJsonAsync(stream, new WorkerEnvelope("prefix-check", WorkerCommands.Handshake,
                JsonDocument.Parse("{}").RootElement.Clone(), 1));
            try
            {
                await ReadJsonAsync(stream);
            }
            catch (EndOfStreamException)
            {
            }
        });
        using var connection = await ConnectAsync(pipeName);
        await connection.SendAsync(new WorkerEnvelope("prefix-check", WorkerCommands.Handshake,
            JsonDocument.Parse("{}").RootElement.Clone(), 1), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => connection.SendAsync(new WorkerEnvelope("too-large",
            WorkerCommands.Handshake, JsonDocument.Parse("{\"data\":\"" + new string('x', 1024 * 1024) + "\"}").RootElement.Clone(), 1),
            CancellationToken.None));
        connection.Dispose();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InboundOversizedFrameIsRejected()
    {
        await AssertInboundLengthRejectedAsync(new byte[] { 0x01, 0x00, 0x10, 0x00 });
    }

    [Fact]
    public async Task InboundNonPositiveFrameIsRejected()
    {
        await AssertInboundLengthRejectedAsync(new byte[] { 0x00, 0x00, 0x00, 0x00 });
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
        var binaryEof = false;
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            await binaryServer.WaitForConnectionAsync();
            var received = new MemoryStream();
            await binaryServer.CopyToAsync(received);
            binaryEof = true;
            Assert.Equal(bytes, received.ToArray());
            var completion = await ReadJsonAsync(stream);
            Assert.Equal("transfer-1", completion.GetProperty("TransferId").GetString());
            Assert.Equal(bytes.Length, completion.GetProperty("ByteCount").GetInt64());
            using var digest = SHA256.Create();
            Assert.Equal(Convert.ToHexString(digest.ComputeHash(bytes)).ToLowerInvariant(),
                completion.GetProperty("Sha256").GetString());
            Assert.True(binaryEof);
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

    [Fact]
    public async Task UploadExpiryDuringBlockedBinaryUploadPreventsCompletion()
    {
        var controlName = "motif-client-" + Guid.NewGuid().ToString("N");
        var binaryName = "motif-binary-" + Guid.NewGuid().ToString("N");
        var control = NewServer(controlName);
        var binary = new NamedPipeServerStream(binaryName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var completionReceived = false;
        var serverTask = RunServerAsync(control, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            await binary.WaitForConnectionAsync();
            await binary.CopyToAsync(Stream.Null);
            binary.Dispose();
            try
            {
                await ReadJsonAsync(stream);
                completionReceived = true;
            }
            catch (EndOfStreamException)
            {
            }
        });
        using var connection = await new WorkerClient().ConnectAsync(controlName,
            new WorkerHandshakeRequest("client-1", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        using var source = new DelayedSource(TimeSpan.FromMilliseconds(500));
        var offer = new BinaryTransferOffer("transfer-expiring", "upload", binaryName, 1,
            DateTimeOffset.UtcNow.AddMilliseconds(100));
        var upload = connection.UploadAsync(offer, source, CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var exception = await Record.ExceptionAsync(() => upload.WaitAsync(TimeSpan.FromSeconds(5)));
        connection.Dispose();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<InvalidOperationException>(exception);
        Assert.False(completionReceived);
    }

    private static async Task RunServerAsync(NamedPipeServerStream server,
        Func<Stream, BinaryTransferOffer, Task> handler)
    {
        await server.WaitForConnectionAsync();
        await handler(server, null!);
        server.Dispose();
    }

    private static NamedPipeServerStream NewServer(string pipeName)
    {
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private static Task<WorkerConnection> ConnectAsync(string pipeName)
    {
        return new WorkerClient().ConnectAsync(pipeName,
            new WorkerHandshakeRequest("client-1", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()),
            TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    private static async Task AssertInboundLengthRejectedAsync(byte[] prefix)
    {
        var pipeName = "motif-client-" + Guid.NewGuid().ToString("N");
        var server = NewServer(pipeName);
        var serverTask = RunServerAsync(server, async (stream, offer) =>
        {
            await ReadJsonAsync(stream);
            await WriteJsonAsync(stream, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()));
            var request = await ReadJsonAsync(stream);
            await stream.WriteAsync(prefix, 0, prefix.Length);
            await stream.FlushAsync();
            Assert.Equal("request-length", request.GetProperty("RequestId").GetString());
        });
        using var connection = await ConnectAsync(pipeName);
        await Assert.ThrowsAsync<InvalidDataException>(() => connection.SendAsync(new WorkerEnvelope("request-length",
            WorkerCommands.Handshake, JsonDocument.Parse("{}").RootElement.Clone(), 1), CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<JsonElement> ReadJsonAsync(Stream stream)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix);
        var length = ReadInt32LittleEndian(prefix);
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload);
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    private static async Task WriteJsonAsync(Stream stream, object value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        var prefix = new byte[4];
        WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, 0, prefix.Length);
        await stream.WriteAsync(payload, 0, payload.Length);
        await stream.FlushAsync();
    }

    private static async Task WriteRawFrameAsync(Stream stream, byte[] payload)
    {
        var prefix = new byte[4];
        WriteInt32LittleEndian(prefix, payload.Length);
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

    private static int ReadInt32LittleEndian(byte[] bytes)
    {
        return bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
    }

    private static void WriteInt32LittleEndian(byte[] bytes, int value)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
        bytes[3] = (byte)(value >> 24);
    }

    private sealed class DelayedSource : MemoryStream
    {
        private readonly TimeSpan _delay;

        public DelayedSource(TimeSpan delay)
        {
            _delay = delay;
        }

        public TaskCompletionSource<bool> Started { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Task.Delay(_delay).ConfigureAwait(false);
            return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
    }
}
