using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class LiveHostObservationIntegrationTests : IDisposable
{
    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-live-host-" + Guid.NewGuid().ToString("N"));

    public LiveHostObservationIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task NamedPipeCommandsOwnOneSessionAndReleaseItOnDisconnect()
    {
        await using var server = WorkerServer.CreateForTests("live-host-" + Guid.NewGuid().ToString("N"), false, _root);
        using var runtimes = ComposeRuntime(server);
        var project = Project("owned");
        _ = runtimes.GetOrOpen(project);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        using var first = await ConnectAsync(server.EndpointName);
        using var second = await ConnectAsync(server.EndpointName);
        var firstClient = new LiveHostObservationClient(first);
        var secondClient = new LiveHostObservationClient(second);

        Assert.True((await firstClient.RegisterAsync(project, Observation("first", 1), cancellation.Token)).Accepted);
        Assert.False((await secondClient.RegisterAsync(project,
            Observation("second", 1), cancellation.Token)).Accepted);
        Assert.False((await firstClient.UpdateAsync(project, Observation("wrong", 2), cancellation.Token)).Accepted);
        Assert.True((await firstClient.UpdateAsync(project, Observation("first", 2), cancellation.Token)).Accepted);
        Assert.False((await firstClient.DisconnectAsync(project, "wrong", cancellation.Token)).Accepted);
        var projectKey = ProjectWorkspaceKey.Compute(project);
        var observedRelease = server.HostReleases.Observe(projectKey);
        var released = server.HostReleases.WaitForReleaseAsync(
            projectKey, observedRelease, cancellation.Token);
        Assert.True((await firstClient.DisconnectAsync(project, "first", cancellation.Token)).Accepted);
        await released;

        Assert.True((await secondClient.RegisterAsync(project,
            Observation("second", 1), cancellation.Token)).Accepted);
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServerDisposalOwnsAndDisposesProjectLanes()
    {
        var server = WorkerServer.CreateForTests(
            "live-host-lanes-" + Guid.NewGuid().ToString("N"), false, _root);
        _ = ComposeRuntime(server);
        var lanes = server.ProjectLanes;

        await server.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => lanes.GetOrCreate("project-key"));
    }

    [Fact]
    public async Task RegistrationWaitsForTheRuntimeExclusiveLease()
    {
        await using var server = WorkerServer.CreateForTests("live-host-lease-" + Guid.NewGuid().ToString("N"), false, _root);
        using var runtimes = ComposeRuntime(server);
        var project = Project("lease");
        var runtime = runtimes.GetOrOpen(project);
        using var operation = await runtime.AcquireOperationAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        using var connection = await ConnectAsync(server.EndpointName);

        var registration = new LiveHostObservationClient(connection)
            .RegisterAsync(project, Observation("host", 1), cancellation.Token);
        await Task.Delay(100, cancellation.Token);
        Assert.False(registration.IsCompleted);
        operation.Dispose();
        Assert.True((await registration).Accepted);

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LiveHostCommandRequiresItsNegotiatedCapability()
    {
        await using var server = WorkerServer.CreateForTests("live-host-capability-" + Guid.NewGuid().ToString("N"), false, _root);
        using var runtimes = ComposeRuntime(server);
        var project = Project("capability");
        _ = runtimes.GetOrOpen(project);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = new NamedPipeClientStream(".", server.EndpointName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellation.Token);
        await WorkerWire.WriteAsync(pipe, new WorkerHandshakeRequest("client", "1.0.0",
            new ProtocolRange(1, 1), new[] { "baseline.v1" }), cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            new LiveHostRegisterRequest(project, Observation("host", 1)), WorkerJson.CreateOptions()));
        await WorkerWire.WriteAsync(pipe, new WorkerEnvelope("request", WorkerCommands.LiveHostRegister,
            document.RootElement.Clone(), 1), cancellation.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => WorkerWire.ReadAsync(pipe, cancellation.Token));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HandleAsyncDeserializesThePayloadExactlyOnce()
    {
        await using var server = WorkerServer.CreateForTests(
            "live-host-deserialize-" + Guid.NewGuid().ToString("N"), false, _root);
        using var runtimes = ComposeRuntime(server);
        var project = Project("deserialize-count");
        _ = runtimes.GetOrOpen(project);
        CountingRequest.Count = 0;
        var handler = new LiveHostObservationCommandHandler<CountingRequest>(
            WorkerCommands.LiveHostRegister, runtimes, request => request.Project, request => true);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            new LiveHostRegisterRequest(project, Observation("host", 1)), WorkerJson.CreateOptions()));

        await handler.HandleAsync(document.RootElement.Clone(), CancellationToken.None);

        Assert.Equal(1, CountingRequest.Count);
    }

    // Same wire shape as LiveHostRegisterRequest; the constructor counts deserializations.
    private sealed record CountingRequest
    {
        [JsonConstructor]
        public CountingRequest(ProjectLocator project, LiveProjectObservation observation)
        {
            Project = project;
            Observation = observation;
            Count++;
        }

        public ProjectLocator Project { get; }
        public LiveProjectObservation Observation { get; }
        public static int Count;
    }

    [Fact]
    public void NotifyReleasedAfterDisposalIsANoOp()
    {
        var coordinator = new ProjectHostReleaseCoordinator();
        coordinator.Observe("workspace");
        coordinator.Dispose();

        var exception = Record.Exception(() => coordinator.NotifyReleased("workspace"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task DisposalCancelsAnAlreadyWaitingRelease()
    {
        var coordinator = new ProjectHostReleaseCoordinator();
        var observed = coordinator.Observe("workspace");
        var waiting = coordinator.WaitForReleaseAsync("workspace", observed, CancellationToken.None);

        coordinator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ProjectLocator Project(string name) => new(Path.Combine(_root, name + ".fwdata"), name);
    private static LiveProjectObservation Observation(string session, long generation) =>
        new(session, generation, false, Digest);
    private static Task<WorkerConnection> ConnectAsync(string pipeName) =>
        new WorkerClient().ConnectAsync(pipeName, new WorkerHandshakeRequest(
            "client", "1.0.0", new ProtocolRange(1, 1), new[] { "live-host.v1" }),
            TimeSpan.FromSeconds(5), CancellationToken.None);
    private ProjectRuntimeRegistry ComposeRuntime(WorkerServer server)
    {
        var ownership = WorkspaceOwnership.Bootstrap(_root);
        return server.CreateRuntimeRegistry(new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0)),
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs), new WorkspaceCleaner(ownership)));
    }
}
