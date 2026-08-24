using System.IO.Pipes;
using System.Text.Json;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using Xunit;

namespace SIL.Motif.Tests.Client;

public sealed class LiveHostObservationClientTests
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ClientSendsRegisterUpdateAndDisconnectInOrder()
    {
        var pipeName = "motif-live-host-client-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await ReadAsync(server);
            await WriteAsync(server, new WorkerHandshakeOffer("1.0.0", new ProtocolRange(1, 1),
                new[] { "live-host.v1" }, "connection"));
            foreach (var expected in new[] { WorkerCommands.LiveHostRegister,
                         WorkerCommands.LiveHostObservationUpdate, WorkerCommands.LiveHostDisconnect })
            {
                var request = await ReadAsync(server);
                Assert.Equal(expected, request.GetProperty("Command").GetString());
                Assert.Equal("host-session", request.GetProperty("Payload")
                    .GetProperty(expected == WorkerCommands.LiveHostDisconnect ? "HostSessionId" : "Observation")
                    .GetStringOrProperty("HostSessionId"));
                await WriteAsync(server, Envelope(request.GetProperty("RequestId").GetString()!, expected,
                    new LiveHostObservationResponse("project-key", true)));
            }
        });
        using var connection = await new WorkerClient().ConnectAsync(pipeName,
            new WorkerHandshakeRequest("client", "1.0.0", new ProtocolRange(1, 1), new[] { "live-host.v1" }),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        var client = new LiveHostObservationClient(connection);
        var project = new ProjectLocator("C:\\workspace\\demo.fwdata", "project");
        var observation = new LiveProjectObservation("host-session", 1, false, Digest);

        Assert.True((await client.RegisterAsync(project, observation, CancellationToken.None)).Accepted);
        Assert.True((await client.UpdateAsync(project, observation, CancellationToken.None)).Accepted);
        Assert.True((await client.DisconnectAsync(project, "host-session", CancellationToken.None)).Accepted);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static WorkerEnvelope Envelope<T>(string id, string command, T payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, WorkerJson.CreateOptions()));
        return new WorkerEnvelope(id, command, document.RootElement.Clone(), 1);
    }

    private static async Task<JsonElement> ReadAsync(Stream stream)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix);
        var payload = new byte[BitConverter.ToInt32(prefix)];
        await stream.ReadExactlyAsync(payload);
        return JsonDocument.Parse(payload).RootElement.Clone();
    }

    private static async Task WriteAsync(Stream stream, object value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, WorkerJson.CreateOptions());
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }
}

internal static class LiveHostJsonTestExtensions
{
    public static string? GetStringOrProperty(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetProperty(property).GetString();
}
