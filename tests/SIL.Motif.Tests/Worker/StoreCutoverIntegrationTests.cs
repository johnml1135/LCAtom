using System.Text.Json;
using Microsoft.Data.Sqlite;
using SIL.Motif.Cli.Store;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Canonicalization;
using SIL.Motif.Contract.Ids;
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

namespace SIL.Motif.Tests.Worker;

public sealed class StoreCutoverIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-cutover-wire-" + Guid.NewGuid().ToString("N"));

    public StoreCutoverIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CutoverRoundTripsOverTheNamedPipeAndTakesTheStore()
    {
        var store = SeedStore();
        await using var server = WorkerServer.CreateForTests("worker-cutover-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var runtimes = ComposeRuntime(server);
        var project = new ProjectLocator(Path.Combine(_root, "demo.fwdata"), "fieldworks-project");
        var runtime = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        using var connection = await Connect(server, cancellation.Token, "store.v1");

        var response = await Cutover(connection, project, store, cancellation.Token);

        Assert.Equal(runtime.WorkspaceKey, response.ProjectKey);
        Assert.Equal(1, response.ImportedProposals);
        Assert.Equal(1, response.ImportedLegacyRows);
        Assert.Empty(response.UnarchivedPaths);
        Assert.False(Directory.Exists(store));
        Assert.Equal(1L, Count(runtime, "Proposals"));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARepeatedCutoverImportsNothingFurther()
    {
        var store = SeedStore();
        await using var server = WorkerServer.CreateForTests("worker-cutover-twice-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var runtimes = ComposeRuntime(server);
        var project = new ProjectLocator(Path.Combine(_root, "demo.fwdata"), "fieldworks-project");
        var runtime = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        using var connection = await Connect(server, cancellation.Token, "store.v1");

        await Cutover(connection, project, store, cancellation.Token);
        var again = await Cutover(connection, project, store, cancellation.Token);

        Assert.Equal(0, again.ImportedProposals);
        Assert.Equal(1L, Count(runtime, "Proposals"));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnOperationSubmittedDuringTheCutoverWaitsAndSeesOnlyTheCommittedState()
    {
        var store = SeedStore();
        await using var server = WorkerServer.CreateForTests("worker-cutover-lease-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var runtimes = ComposeRuntime(server);
        var project = new ProjectLocator(Path.Combine(_root, "demo.fwdata"), "fieldworks-project");
        var runtime = runtimes.GetOrOpen(project);

        // Hold the exclusive lease the handler needs, so the later shared waiter is provably queued behind it.
        using var exclusive = await runtime.AcquireExclusiveAsync(cancellation.Token);
        var waiting = runtime.AcquireOperationAsync(cancellation.Token);
        Assert.False(waiting.IsCompleted);

        ProjectStoreCutover.Run(store, runtime.Database);
        Assert.False(waiting.IsCompleted);
        exclusive.Dispose();

        using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(10), cancellation.Token);
        Assert.Equal(1L, Count(runtime, "Proposals"));
        Assert.Equal(1L, Count(runtime, "Corpora"));
    }

    private static async Task<StoreCutoverResponse> Cutover(WorkerConnection connection, ProjectLocator project,
        string store, CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(
            new StoreCutoverRequest(project, store), WorkerJson.CreateOptions()));
        var response = await connection.SendAsync(new WorkerEnvelope(
            Guid.NewGuid().ToString("N"), WorkerCommands.StoreCutover, payload.RootElement.Clone(), 1),
            cancellationToken);
        return JsonSerializer.Deserialize<StoreCutoverResponse>(
            response.Payload.GetRawText(), WorkerJson.CreateOptions())!;
    }

    private static Task<WorkerConnection> Connect(WorkerServer server, CancellationToken cancellationToken,
        params string[] capabilities) =>
        new WorkerClient().ConnectAsync(server.EndpointName,
            new WorkerHandshakeRequest("test-client", "1.0.0", new ProtocolRange(1, 1), capabilities),
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

        var options = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(store, "motif.db"),
            Pooling = false
        };
        using var legacy = new SqliteConnection(options.ToString());
        legacy.Open();
        MotifSchema.EnsureLegacyTables(legacy);
        using var command = legacy.CreateCommand();
        command.CommandText = "INSERT INTO Corpora VALUES ('c1','{\"source\":\"legacy\"}');";
        command.ExecuteNonQuery();
        return store;
    }

    private static long Count(ProjectRuntime runtime, string table)
    {
        using var connection = runtime.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
    }
}
