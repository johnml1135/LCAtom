using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerJobStatusIntegrationTests
{
    [Fact]
    public async Task JobStatus_RoundsTripsOverNamedPipeFromOneReadyRuntime()
    {
        using var temporary = new TemporaryRoot("motif-job-status-");
        var root = temporary.RootPath;
        await using var server = WorkerServer.CreateForTests("worker-job-status-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var runtimes = server.CreateRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs),
                new WorkspaceCleaner(ownership)));
        var project = new ProjectLocator(Path.Combine(root, "demo.fwdata"), "fieldworks-project");
        var equivalent = new ProjectLocator(Path.Combine(root, ".", "demo.fwdata"), "fieldworks-project");
        var runtime = runtimes.GetOrOpen(project);
        JobRecord record;
        using (var operation = await runtime.AcquireOperationAsync(cancellation.Token))
        {
            record = runtime.Jobs.Create("job-1", runtime.WorkspaceKey, "dry-run", "{}",
                "2026-08-23T12:00:00Z");
            record = runtime.Jobs.Transition(record, JobStatus.WaitingForBaseline);
        }

        var running = server.StartAsync(cancellation.Token);
        using var connection = await new WorkerClient().ConnectAsync(server.EndpointName,
            new WorkerHandshakeRequest("test-client", "1.0.0", new ProtocolRange(1, 1),
                new[] { "jobs.v1" }), TimeSpan.FromSeconds(5), cancellation.Token);
        var client = new WorkerJobClient(connection);

        var status = await client.GetStatusAsync(equivalent, record.JobId, cancellation.Token);

        Assert.Equal(record.JobId, status.JobId);
        Assert.Equal(runtime.WorkspaceKey, status.ProjectKey);
        Assert.True(status.Found);
        Assert.Equal(JobStatus.WaitingForBaseline, status.Status);
        Assert.Equal(record.Attempt, status.Attempt);
        Assert.Equal(record.UpdatedUtc, status.UpdatedUtc);
        Assert.Equal(ProjectWorkspaceKey.Compute(project), ProjectWorkspaceKey.Compute(equivalent));
        Assert.Equal(ProjectDatabaseCatalog.DatabasePathFor(project), runtime.Database.FullPath);
        Assert.Equal(ProjectDatabaseCatalog.DatabasePathFor(equivalent), runtime.Database.FullPath);

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MissingJob_IsMappedToTypedKeyNotFoundException()
    {
        using var temporary = new TemporaryRoot("motif-job-status-missing-");
        var root = temporary.RootPath;
        await using var server = WorkerServer.CreateForTests("worker-job-status-missing-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var runtimes = server.CreateRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs),
                new WorkspaceCleaner(ownership)));
        var project = new ProjectLocator(Path.Combine(root, "demo.fwdata"), "fieldworks-project");
        _ = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        using var connection = await new WorkerClient().ConnectAsync(server.EndpointName,
            new WorkerHandshakeRequest("test-client", "1.0.0", new ProtocolRange(1, 1),
                new[] { "jobs.v1" }), TimeSpan.FromSeconds(5), cancellation.Token);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => new WorkerJobClient(connection)
            .GetStatusAsync(project, "missing-job", cancellation.Token));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MalformedRequestInputsAreRejectedBeforeSending()
    {
        Assert.Throws<ArgumentNullException>(() => new JobStatusRequest(null!, "job-1"));
        Assert.Throws<ArgumentException>(() => new JobStatusRequest(
            new ProjectLocator("C:\\workspace\\demo.fwdata", "project"), " "));
    }

    [Fact]
    public async Task CapabilityMismatchClosesBeforeDatabaseDispatch()
    {
        await using var server = WorkerServer.CreateForTests("worker-job-status-capability-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        using var connection = await new WorkerClient().ConnectAsync(server.EndpointName,
            new WorkerHandshakeRequest("test-client", "1.0.0", new ProtocolRange(1, 1),
                Array.Empty<string>()), TimeSpan.FromSeconds(5), cancellation.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => new WorkerJobClient(connection).GetStatusAsync(
            new ProjectLocator("C:\\workspace\\demo.fwdata", "project"), "job-1", cancellation.Token));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NonReadyAndDifferentProjectRuntimesAreRefusedWithoutOpeningDatabase()
    {
        using var temporary = new TemporaryRoot("motif-job-status-refusal-");
        var root = temporary.RootPath;
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var work = new WorkerWorkTracker();
        using var runtimes = new ProjectRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs),
                new WorkspaceCleaner(ownership)), work, new ProjectRuntimeActivity());
        var readyProject = new ProjectLocator(Path.Combine(root, "ready.fwdata"), "ready");
        _ = runtimes.GetOrOpen(readyProject);
        var differentProject = new ProjectLocator(Path.Combine(root, "different.fwdata"), "different");
        var handler = new JobStatusCommandHandler(runtimes);
        using var payload = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(
            new JobStatusRequest(differentProject, "job-1"), WorkerJson.CreateOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            payload.RootElement.Clone(), CancellationToken.None));
        Assert.False(File.Exists(ProjectDatabaseCatalog.DatabasePathFor(differentProject)));
        Assert.False(runtimes.TryGet(ProjectWorkspaceKey.Compute(differentProject), out _));
    }

    [Fact]
    public async Task DifferentProjectPathIsRefusedOverPipeWithoutOpeningItsDatabase()
    {
        using var temporary = new TemporaryRoot("motif-job-status-pipe-different-");
        var root = temporary.RootPath;
        await using var server = WorkerServer.CreateForTests("worker-job-status-pipe-different-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var runtimes = server.CreateRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs),
                new WorkspaceCleaner(ownership)));
        var readyProject = new ProjectLocator(Path.Combine(root, "ready.fwdata"), "ready");
        _ = runtimes.GetOrOpen(readyProject);
        var differentProject = new ProjectLocator(Path.Combine(root, "different.fwdata"), "different");
        var running = server.StartAsync(cancellation.Token);
        using var connection = await new WorkerClient().ConnectAsync(server.EndpointName,
            new WorkerHandshakeRequest("test-client", "1.0.0", new ProtocolRange(1, 1),
                new[] { "jobs.v1" }), TimeSpan.FromSeconds(5), cancellation.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => new WorkerJobClient(connection)
            .GetStatusAsync(differentProject, "job-1", cancellation.Token));

        Assert.False(File.Exists(ProjectDatabaseCatalog.DatabasePathFor(differentProject)));
        Assert.False(runtimes.TryGet(ProjectWorkspaceKey.Compute(differentProject), out _));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReleasedRuntimeIsRefusedOverPipeBeforeDatabaseRead()
    {
        using var temporary = new TemporaryRoot("motif-job-status-pipe-not-ready-");
        var root = temporary.RootPath;
        await using var server = WorkerServer.CreateForTests("worker-job-status-pipe-not-ready-" + Guid.NewGuid().ToString("N"), false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ownership = WorkspaceOwnership.Bootstrap(Path.Combine(root, "owned"));
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        using var runtimes = server.CreateRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs),
                new WorkspaceCleaner(ownership)));
        var project = new ProjectLocator(Path.Combine(root, "released.fwdata"), "released");
        var runtime = runtimes.GetOrOpen(project);
        Assert.True(runtimes.TryReleaseIfIdle(runtime.WorkspaceKey));
        Assert.False(runtimes.TryGet(runtime.WorkspaceKey, out _));
        var running = server.StartAsync(cancellation.Token);
        using var connection = await new WorkerClient().ConnectAsync(server.EndpointName,
            new WorkerHandshakeRequest("test-client", "1.0.0", new ProtocolRange(1, 1),
                new[] { "jobs.v1" }), TimeSpan.FromSeconds(5), cancellation.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => new WorkerJobClient(connection)
            .GetStatusAsync(project, "job-1", cancellation.Token));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NegotiatedProtocolMismatchIsRejectedByTypedConnection()
    {
        await using var server = WorkerServer.CreateForTests("worker-job-status-protocol-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        using var connection = await new WorkerClient().ConnectAsync(server.EndpointName,
            new WorkerHandshakeRequest("test-client", "1.0.0", new ProtocolRange(1, 1),
                new[] { "jobs.v1" }), TimeSpan.FromSeconds(5), cancellation.Token);
        using var payload = JsonDocument.Parse("{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAsync(new WorkerEnvelope(
            "wrong-protocol", WorkerCommands.JobStatus, payload.RootElement.Clone(), 2),
            cancellation.Token));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot(string prefix)
        {
            RootPath = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            try { Directory.Delete(RootPath, true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
