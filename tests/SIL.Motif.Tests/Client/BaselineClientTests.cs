using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using Xunit;

namespace SIL.Motif.Tests.Client;

public sealed class BaselineClientTests
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task OfferAndPublish_UseFreshCorrelatedRequestsAndNegotiatedProtocol()
    {
        var pipeName = "motif-baseline-client-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(pipeName);
        var offer = new BinaryTransferOffer("transfer-1", "upload", "pipe-1", 4096,
            DateTimeOffset.UtcNow.AddMinutes(1));
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await ReadJsonAsync(server);
            await WriteJsonAsync(server, new WorkerHandshakeOffer(
                "1.0.0", new ProtocolRange(1, 1), new[] { "baseline.v1", "jobs.v1" }));

            var offerRequest = await ReadJsonAsync(server);
            Assert.Equal(WorkerCommands.BaselineOffer, offerRequest.GetProperty("Command").GetString());
            Assert.Equal(1, offerRequest.GetProperty("ProtocolVersion").GetInt32());
            Assert.Equal("fieldworks-project", offerRequest.GetProperty("Payload")
                .GetProperty("Project").GetProperty("FieldWorksProjectIdentity").GetString());
            var offerRequestId = offerRequest.GetProperty("RequestId").GetString()!;
            await WriteJsonAsync(server, Envelope(offerRequestId, WorkerCommands.BaselineOffer,
                new BaselineOfferResponse(offer, null)));

            var publishRequest = await ReadJsonAsync(server);
            Assert.Equal(WorkerCommands.BaselinePublish, publishRequest.GetProperty("Command").GetString());
            Assert.Equal(1, publishRequest.GetProperty("ProtocolVersion").GetInt32());
            Assert.Equal("transfer-1", publishRequest.GetProperty("Payload").GetProperty("TransferId").GetString());
            Assert.Equal(Digest, publishRequest.GetProperty("Payload").GetProperty("Token")
                .GetProperty("SemanticSnapshotDigest").GetString());
            var publishRequestId = publishRequest.GetProperty("RequestId").GetString()!;
            Assert.NotEqual(offerRequestId, publishRequestId);
            await WriteJsonAsync(server, Envelope(publishRequestId, WorkerCommands.BaselinePublish,
                new BaselinePublishResponse(new BaselinePublicationResult("project-key", Token()), null)));
        });

        using var connection = await ConnectAsync(pipeName, "baseline.v1", "jobs.v1");
        var client = new BaselineClient(connection);

        var receivedOffer = await client.RequestOfferAsync(Project(), CancellationToken.None);
        var publication = await client.PublishAsync(Project(), receivedOffer.TransferId, Token(), CancellationToken.None);

        Assert.Equal(offer, receivedOffer);
        Assert.Equal("project-key", publication.ProjectKey);
        Assert.Equal(Token(), publication.Token);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MissingCapability_IsRefusedBeforeWritingARequest()
    {
        var pipeName = "motif-baseline-capability-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await ReadJsonAsync(server);
            await WriteJsonAsync(server, new WorkerHandshakeOffer(
                "1.0.0", new ProtocolRange(1, 1), new[] { "jobs.v1" }));
            var prefix = new byte[1];
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await server.ReadExactlyAsync(prefix, timeout.Token));
        });

        using var connection = await ConnectAsync(pipeName, "jobs.v1");
        var client = new BaselineClient(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestOfferAsync(Project(), CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailureResponse_IsMappedToBaselineCommandException()
    {
        var pipeName = "motif-baseline-failure-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(pipeName);
        var failure = new BaselineCommandFailure(
            BaselineFailureCode.ProjectRuntimeUnavailable, true, "The project runtime is unavailable.");
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await ReadJsonAsync(server);
            await WriteJsonAsync(server, new WorkerHandshakeOffer(
                "1.0.0", new ProtocolRange(1, 1), new[] { "baseline.v1" }));
            var request = await ReadJsonAsync(server);
            await WriteJsonAsync(server, Envelope(request.GetProperty("RequestId").GetString()!,
                WorkerCommands.BaselineOffer, new BaselineOfferResponse(null, failure)));
        });

        using var connection = await ConnectAsync(pipeName, "baseline.v1");

        var exception = await Assert.ThrowsAsync<BaselineCommandException>(() =>
            new BaselineClient(connection).RequestOfferAsync(Project(), CancellationToken.None));

        Assert.Equal(failure, exception.Failure);
        Assert.Equal(failure.Message, exception.Message);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WrongResponseCommand_IsRejectedAfterRequestIdCorrelation()
    {
        var pipeName = "motif-baseline-correlation-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(pipeName);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await ReadJsonAsync(server);
            await WriteJsonAsync(server, new WorkerHandshakeOffer(
                "1.0.0", new ProtocolRange(1, 1), new[] { "baseline.v1" }));
            var request = await ReadJsonAsync(server);
            await WriteJsonAsync(server, Envelope(request.GetProperty("RequestId").GetString()!,
                WorkerCommands.BaselinePublish,
                new BaselinePublishResponse(new BaselinePublicationResult("project-key", Token()), null)));
        });

        using var connection = await ConnectAsync(pipeName, "baseline.v1");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new BaselineClient(connection).RequestOfferAsync(Project(), CancellationToken.None));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static WorkerEnvelope Envelope<T>(string requestId, string command, T payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(payload,
            WorkerJson.CreateOptions()));
        return new WorkerEnvelope(requestId, command, document.RootElement.Clone(), 1);
    }

    private static Task<WorkerConnection> ConnectAsync(string pipeName, params string[] capabilities) =>
        new WorkerClient().ConnectAsync(pipeName,
            new WorkerHandshakeRequest("client-1", "1.0.0", new ProtocolRange(1, 1), capabilities),
            TimeSpan.FromSeconds(5), CancellationToken.None);

    private static NamedPipeServerStream NewServer(string pipeName) => new(
        pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static ProjectLocator Project() =>
        new("C:\\workspace\\demo.fwdata", "fieldworks-project");

    private static BaselineToken Token() =>
        new("fieldworks-project", Digest, "1", "2026-08-23T12:34:56Z", OtherDigest);

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
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, WorkerJson.CreateOptions());
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

    private static int ReadInt32LittleEndian(byte[] bytes) =>
        bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24;

    private static void WriteInt32LittleEndian(byte[] bytes, int value)
    {
        bytes[0] = (byte)value;
        bytes[1] = (byte)(value >> 8);
        bytes[2] = (byte)(value >> 16);
        bytes[3] = (byte)(value >> 24);
    }
}
