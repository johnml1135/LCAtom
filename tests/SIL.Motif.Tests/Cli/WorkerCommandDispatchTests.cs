using System.Text.Json;
using Microsoft.Data.Sqlite;
using SIL.Motif.Cli.Store;
using SIL.Motif.Cli.Worker;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Parsing;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Store;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Cli;

public sealed class WorkerCommandDispatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-cli-worker-" + Guid.NewGuid().ToString("N"));

    public WorkerCommandDispatchTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AMissingCapabilityIsRefusedLocallyAndNamesWhatIsMissing()
    {
        await using var server = WorkerServer.CreateForTests("cli-no-store-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var runtimes = ComposeRuntime(server);
        var running = server.StartAsync(cancellation.Token);
        using var connection = await Connect(server, cancellation.Token, "jobs.v1");
        var client = new WorkerCommandClient(connection);

        Assert.False(client.CanExecute(WorkerCommands.StoreCutover));
        var refusal = await Assert.ThrowsAsync<WorkerCommandUnavailableException>(() =>
            client.ExecuteAsync<StoreCutoverRequest, StoreCutoverResponse>(WorkerCommands.StoreCutover,
                new StoreCutoverRequest(Project(), Path.Combine(_root, "store")), cancellation.Token));

        Assert.Equal("store.v1", refusal.Capability);
        Assert.Contains("store.v1", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(WorkerCommands.StoreCutover, refusal.Message, StringComparison.Ordinal);

        // A command this build has never heard of is named as such, not reported as a missing capability.
        Assert.False(client.CanExecute("store.rollback"));
        var unknown = await Assert.ThrowsAsync<WorkerCommandUnavailableException>(() =>
            client.ExecuteAsync<StoreCutoverRequest, StoreCutoverResponse>("store.rollback",
                new StoreCutoverRequest(Project(), Path.Combine(_root, "store")), cancellation.Token));
        Assert.Null(unknown.Capability);

        // Refusing locally must leave the connection usable, not spend it.
        Assert.True(client.CanExecute(WorkerCommands.JobStatus));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ANegotiatedCommandRoundTripsThroughTheTypedClient()
    {
        var store = SeedStore();
        await using var server = WorkerServer.CreateForTests("cli-store-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var runtimes = ComposeRuntime(server);
        var runtime = runtimes.GetOrOpen(Project());
        var running = server.StartAsync(cancellation.Token);
        using var connection = await Connect(server, cancellation.Token, "store.v1");
        var client = new WorkerCommandClient(connection);

        Assert.True(client.CanExecute(WorkerCommands.StoreCutover));
        var response = await client.ExecuteAsync<StoreCutoverRequest, StoreCutoverResponse>(
            WorkerCommands.StoreCutover, new StoreCutoverRequest(Project(), store), cancellation.Token);

        Assert.Equal(runtime.WorkspaceKey, response.ProjectKey);
        Assert.Equal(1, response.ImportedProposals);
        Assert.False(Directory.Exists(store));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AWorkerRefusalSurfacesTypedAndLeavesTheConnectionUsable()
    {
        await using var server = WorkerServer.CreateForTests("cli-refused-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var runtimes = ComposeRuntime(server);
        var runtime = runtimes.GetOrOpen(Project());
        JobRecord record;
        using (await runtime.AcquireOperationAsync(cancellation.Token))
            record = runtime.Jobs.Create("job-1", runtime.WorkspaceKey, "dry-run", "{}", "2026-08-25T12:00:00Z");
        var running = server.StartAsync(cancellation.Token);
        using var connection = await Connect(server, cancellation.Token, "jobs.v1", "store.v1");
        var client = new WorkerCommandClient(connection);

        // A relative path is refused inside the handler's own deserialization, past every wire-level check.
        using var malformed = JsonDocument.Parse(
            "{\"Project\":{\"FullFwDataPath\":\"demo.fwdata\",\"FieldWorksProjectIdentity\":\"p\"}," +
            "\"JobId\":\"job-1\"}");
        var refused = await Assert.ThrowsAsync<WorkerRequestRefusedException>(() => connection.SendAsync(
            new WorkerEnvelope("request-1", WorkerCommands.JobStatus, malformed.RootElement.Clone(), 1),
            cancellation.Token));
        Assert.Equal(WorkerRefusalReason.MalformedPayload, refused.Reason);

        var status = await client.ExecuteAsync<JobStatusRequest, JobStatusResponse>(
            WorkerCommands.JobStatus, new JobStatusRequest(Project(), record.JobId), cancellation.Token);

        Assert.True(status.Found);
        Assert.Equal(record.JobId, status.JobId);

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private ProjectLocator Project() =>
        new ProjectLocator(Path.Combine(_root, "demo.fwdata"), "fieldworks-project");

    private static Task<WorkerConnection> Connect(WorkerServer server, CancellationToken cancellationToken,
        params string[] capabilities) =>
        new WorkerClient().ConnectAsync(server.EndpointName,
            new WorkerHandshakeRequest("motif-cli", "1.0.0", new ProtocolRange(1, 1), capabilities),
            TimeSpan.FromSeconds(5), cancellationToken);

    private ProjectRuntimeRegistry ComposeRuntime(WorkerServer server)
    {
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(_root, "owned"));
        return server.CreateRuntimeRegistry(
            new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0)),
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs), new WorkspaceCleaner(ownership)));
    }

    private string SeedStore()
    {
        var store = Path.Combine(_root, "store");
        var proposals = new ProposalStore(store);
        proposals.EnsureDirectoriesExist();
        var id = CanonicalId.Mint("proposal/").Value;
        var json = "{\"contractVersions\":{},\"proposalId\":\"" + id + "\",\"requires\":[],\"operations\":[]}";
        var digest = IntentDigest.Compute(ProposalJsonParser.Parse(json));
        File.WriteAllText(proposals.ObjectPath(digest), json);
        Directory.CreateDirectory(Path.GetDirectoryName(proposals.ManifestPath(id))!);
        File.WriteAllText(proposals.ManifestPath(id),
            "{\"proposalId\":\"" + id + "\",\"status\":\"proposed\",\"currentIntentDigest\":\"" + digest + "\"}");
        return store;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
