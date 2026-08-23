using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerServerTests
{
    [Fact]
    public async Task LifetimeExitsAfterIdleTimeoutWhenNoWorkIsRegistered()
    {
        var clock = new ManualClock();
        var tracker = new TestWorkTracker(false);
        using var shutdown = new CancellationTokenSource();
        var running = new WorkerLifetime(clock).RunUntilIdleAsync(
            TimeSpan.FromSeconds(5), tracker, shutdown.Token);

        clock.Advance(TimeSpan.FromSeconds(5));

        await running.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WorkLeasePreventsIdleExitUntilWorkFinishes()
    {
        var clock = new ManualClock();
        var tracker = new TestWorkTracker(true);
        using var shutdown = new CancellationTokenSource();
        var running = new WorkerLifetime(clock).RunUntilIdleAsync(
            TimeSpan.FromSeconds(5), tracker, shutdown.Token);

        await clock.WaitForDelayRegistrationAsync();
        for (var poll = 0; poll < 6; poll++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await clock.WaitForDelayRegistrationAsync();
        }
        Assert.False(running.IsCompleted);

        tracker.HasQueuedRunningOrWaitingWork = false;
        clock.Advance(TimeSpan.FromSeconds(5));
        await clock.WaitForDelayRegistrationAsync();
        Assert.False(running.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        await running.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WorkEndingAtBusyPollStartsAFullIdleInterval()
    {
        var clock = new ManualClock();
        var tracker = new TestWorkTracker(true);
        using var shutdown = new CancellationTokenSource();
        var running = new WorkerLifetime(clock).RunUntilIdleAsync(
            TimeSpan.FromSeconds(5), tracker, shutdown.Token);

        await clock.WaitForDelayRegistrationAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        tracker.HasQueuedRunningOrWaitingWork = false;
        await clock.WaitForDelayRegistrationAsync();

        Assert.False(running.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(running.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await running.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SecondWorkerProcessReportsExistingEndpointAndExits()
    {
        using var first = StartWorkerProcess(30000);
        try
        {
            var endpoint = await first.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(string.IsNullOrWhiteSpace(endpoint));
            using var second = StartWorkerProcess(1000);
            var secondOutput = await second.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("existing endpoint: " + endpoint, secondOutput);
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, second.ExitCode);
            Assert.False(first.HasExited);
        }
        finally
        {
            if (!first.HasExited)
                first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WorkerProcessExitsAfterIdleTimeout()
    {
        using var process = StartWorkerProcess(200);
        var endpoint = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task ControlPipeAcceptsTwoLiveClients()
    {
        var name = "worker-test-" + Guid.NewGuid().ToString("N");
        await using var server = WorkerServer.CreateForTests(name);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        using var first = await new SIL.Motif.Client.Worker.WorkerClient().ConnectAsync(
            server.EndpointName, Handshake(), TimeSpan.FromSeconds(5), cancellation.Token);
        using var second = await new SIL.Motif.Client.Worker.WorkerClient().ConnectAsync(
            server.EndpointName, Handshake(), TimeSpan.FromSeconds(5), cancellation.Token);
        Assert.Equal(1, first.Negotiated.ProtocolVersion);
        Assert.Equal(1, second.Negotiated.ProtocolVersion);
        first.Dispose();
        second.Dispose();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await second.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposalDrainsAClientStalledBeforeHandshake()
    {
        await using var server = WorkerServer.CreateForTests("worker-stalled-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        using var pipe = new NamedPipeClientStream(".", server.EndpointName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellation.Token);
        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await running.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OneConnectionCanOwnAndReleaseMultipleProjectRoutes()
    {
        var name = "worker-multi-project-" + Guid.NewGuid().ToString("N");
        await using var server = WorkerServer.CreateForTests(name);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        using var client = await new SIL.Motif.Client.Worker.WorkerClient().ConnectAsync(
            server.EndpointName, Handshake(), TimeSpan.FromSeconds(5), cancellation.Token);
        var first = EventProject("same-connection-a");
        var second = EventProject("same-connection-b");
        server.RegisterLiveHost(first, client.ServerConnectionId!);
        server.RegisterLiveHost(second, client.ServerConnectionId!);
        client.Dispose();
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => server.EventSink.RequestApplyAsync(
            first, JsonDocument.Parse("{}").RootElement.Clone(), CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => server.EventSink.RequestApplyAsync(
            second, JsonDocument.Parse("{}").RootElement.Clone(), CancellationToken.None));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OrdinaryClientCoexistsWithExplicitlyRegisteredLiveHost()
    {
        var name = "worker-test-" + Guid.NewGuid().ToString("N");
        await using var server = WorkerServer.CreateForTests(name);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        using var live = await new SIL.Motif.Client.Worker.WorkerClient().ConnectAsync(
            server.EndpointName, Handshake(), TimeSpan.FromSeconds(5), cancellation.Token);
        using var ordinary = await new SIL.Motif.Client.Worker.WorkerClient().ConnectAsync(
            server.EndpointName, Handshake(), TimeSpan.FromSeconds(5), cancellation.Token);
        Assert.NotEqual(live.ServerConnectionId, ordinary.ServerConnectionId);
        var project = EventProject("coexist");
        server.RegisterLiveHost(project, live.ServerConnectionId!);
        Assert.Throws<ProjectHostBusyException>(() =>
            server.RegisterLiveHost(project, ordinary.ServerConnectionId!));

        using var document = JsonDocument.Parse("{\"coexist\":true}");
        var received = new TaskCompletionSource<WorkerEventEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        live.EventReceived += (_, value) => received.TrySetResult(value);
        var pending = server.EventSink.RequestApplyAsync(project, document.RootElement.Clone(), cancellation.Token);
        var eventEnvelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        var ordinaryResponseTask = ordinary.SendAsync(new WorkerEnvelope(
            "ordinary-request", WorkerCommands.Handshake, document.RootElement.Clone(), 1),
            cancellation.Token);
        var response = await ordinaryResponseTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ordinary-request", response.RequestId);
        Assert.Equal(WorkerCommands.Handshake, response.Command);
        Assert.False(pending.IsCompleted);
        await live.CompleteEventAsync(new WorkerEventResultEnvelope(eventEnvelope.EventId,
            WorkerEventOutcome.Completed, document.RootElement.Clone(), 1), cancellation.Token);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        server.UnregisterLiveHost(project, live.ServerConnectionId!);
        server.RegisterLiveHost(project, ordinary.ServerConnectionId!);
        cancellation.Cancel();
        live.Dispose();
        ordinary.Dispose();
        await live.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await ordinary.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SharedConnectionWriterKeepsResponseAndEventFramesIntact()
    {
        using var stream = new ChunkingStream();
        using var sink = new WorkerEventSink();
        using var writerGate = new SemaphoreSlim(1, 1);
        var project = EventProject("writer");
        sink.RegisterLiveHost(project, "connection", "session", stream, 1, writerGate);
        using var document = JsonDocument.Parse("{}");
        var pending = sink.RequestApplyAsync(project, document.RootElement.Clone(), CancellationToken.None);
        var responseWrite = WriteSerializedAsync(writerGate, stream, new WorkerEnvelope(
            "response", WorkerCommands.Handshake, document.RootElement.Clone(), 1),
            CancellationToken.None);
        await responseWrite;
        Assert.True(SpinWait.SpinUntil(() => stream.Length > 0, TimeSpan.FromSeconds(1)));
        var frames = ReadFrames(stream.ToArray());
        Assert.Equal(2, frames.Count);
        Assert.Contains(frames, frame => frame.Contains(WorkerCommands.ApplyRequested, StringComparison.Ordinal));
        Assert.Contains(frames, frame => frame.Contains("response", StringComparison.Ordinal));
        sink.AcceptResult(new WorkerEventResultEnvelope(ReadEventId(stream),
            WorkerEventOutcome.Completed, document.RootElement.Clone(), 1));
        await pending.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HandshakeCompletesBeforeACommandAndDoesNotCreateProjectState()
    {
        var name = "worker-test-" + Guid.NewGuid().ToString("N");
        await using var server = WorkerServer.CreateForTests(name);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = server.StartAsync(cancellation.Token);
        using var client = await new SIL.Motif.Client.Worker.WorkerClient().ConnectAsync(
            server.EndpointName,
            new WorkerHandshakeRequest("test-client", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>()),
            TimeSpan.FromSeconds(5), cancellation.Token);

        Assert.Equal(1, client.Negotiated.ProtocolVersion);
        Assert.False(server.HasQueuedRunningOrWaitingWork);
    }

    [Fact]
    public async Task HandshakeOfferUsesCompiledWorkerMetadata()
    {
        var name = "worker-test-" + Guid.NewGuid().ToString("N");
        await using var server = WorkerServer.CreateForTests(name);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = server.StartAsync(cancellation.Token);
        using var client = await new SIL.Motif.Client.Worker.WorkerClient().ConnectAsync(
            server.EndpointName, Handshake(), TimeSpan.FromSeconds(5), cancellation.Token);

        var expected = WorkerBuildMetadataProvider.Current.ToHandshakeOffer();
        Assert.Equal(expected.ProductVersion, client.Offer.ProductVersion);
        Assert.Equal(expected.Protocols, client.Offer.Protocols);
        Assert.Equal(expected.Capabilities, client.Offer.Capabilities);
    }

    [Fact]
    public void PipeSecurityContainsOnlyTheOwningUserAndSystemRules()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var security = WorkerServer.CreatePipeSecurity(sid);
        AssertPipeRules(security, sid);
    }

    [Fact]
    public void BinaryPipeSecurityUsesTheSameRestrictedRules()
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        AssertPipeRules(BinaryTransferServer.CreatePipeSecurity(sid), sid);
    }

    [Fact]
    public async Task UnknownControlTrafficClosesBeforeDispatch()
    {
        var name = "worker-test-" + Guid.NewGuid().ToString("N");
        await using var server = WorkerServer.CreateForTests(name);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = server.StartAsync(cancellation.Token);
        using var pipe = new NamedPipeClientStream(".", server.EndpointName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellation.Token);
        await WriteFrameAsync(pipe, "{\"RequestId\":\"bad\",\"Command\":\"unknown\",\"Payload\":{},\"ProtocolVersion\":1}");
        var read = new byte[1];
        Assert.Equal(0, await pipe.ReadAsync(read, cancellation.Token));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnknownCommandAfterHandshakeClosesBeforeDispatch()
    {
        var name = "worker-test-" + Guid.NewGuid().ToString("N");
        await using var server = WorkerServer.CreateForTests(name);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = server.StartAsync(cancellation.Token);
        using var pipe = new NamedPipeClientStream(".", server.EndpointName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellation.Token);
        await WriteFrameAsync(pipe, JsonSerializer.Serialize(Handshake()));
        _ = await ReadFrameAsync(pipe, cancellation.Token);
        await WriteFrameAsync(pipe, "{\"Command\":\"unknown\",\"ProtocolVersion\":1}");
        var read = new byte[1];
        Assert.Equal(0, await pipe.ReadAsync(read, cancellation.Token));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EventSinkSupportsAllSettledEventsAndLifecycleRules()
    {
        using var host = new MemoryStream();
        using var sink = new WorkerEventSink();
        var project = EventProject("settled");
        sink.RegisterLiveHost(project, "connection", "session", host, 1, new SemaphoreSlim(1, 1));
        Assert.Throws<ProjectHostBusyException>(() => sink.RegisterLiveHost(project, "other", "session", new MemoryStream(), 1,
            new SemaphoreSlim(1, 1)));
        using var document = JsonDocument.Parse("{}");
        await CompleteEventAsync(sink, project, host, document.RootElement.Clone(),
            sink.RequestBaselineRefreshAsync, WorkerCommands.BaselineRefreshRequested);
        await CompleteEventAsync(sink, project, host, document.RootElement.Clone(),
            sink.RequestApplyAsync, WorkerCommands.ApplyRequested);
        await CompleteEventAsync(sink, project, host, document.RootElement.Clone(),
            sink.RequestReconciliationAsync, WorkerCommands.ReconciliationRequested);
        using var cancellation = new CancellationTokenSource();
        var beforeCancellation = host.Length;
        var cancelled = sink.RequestCancellationAsync(project, document.RootElement.Clone(), cancellation.Token);
        Assert.True(SpinWait.SpinUntil(() => host.Length > beforeCancellation, TimeSpan.FromSeconds(1)));
        var cancellationBytes = host.ToArray();
        var cancellationOffset = (int)beforeCancellation;
        var cancellationLength = cancellationBytes[cancellationOffset] |
            cancellationBytes[cancellationOffset + 1] << 8 |
            cancellationBytes[cancellationOffset + 2] << 16 |
            cancellationBytes[cancellationOffset + 3] << 24;
        using var cancellationDocument = JsonDocument.Parse(cancellationBytes.AsMemory(
            cancellationOffset + 4, cancellationLength));
        Assert.Equal(WorkerCommands.CancellationRequested,
            cancellationDocument.RootElement.GetProperty("Event").GetString());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Throws<InvalidOperationException>(() => sink.AcceptResult(
            new WorkerEventResultEnvelope("unknown", WorkerEventOutcome.Failed, document.RootElement.Clone(), 1)));
        Assert.Throws<InvalidOperationException>(() => sink.AcceptResult(
            new WorkerEventResultEnvelope(ReadLastEventId(host), WorkerEventOutcome.Failed,
                document.RootElement.Clone(), 2)));
    }

    [Fact]
    public async Task EventSinkDisconnectFaultsPendingRequest()
    {
        using var host = new MemoryStream();
        using var sink = new WorkerEventSink();
        var project = EventProject("disconnect");
        sink.RegisterLiveHost(project, "connection", "session", host, 1, new SemaphoreSlim(1, 1));
        using var document = JsonDocument.Parse("{}");
        var pending = sink.RequestApplyAsync(project, document.RootElement.Clone(), CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => host.Length >= 4, TimeSpan.FromSeconds(1)));
        sink.UnregisterLiveHost(project, "connection", "session");
        await Assert.ThrowsAsync<IOException>(() => pending);
    }

    [Fact]
    public async Task ProjectScopedHostsRouteIsolationBusyAndDisconnectCorrectly()
    {
        using var sink = new WorkerEventSink();
        using var hostA = new MemoryStream();
        using var hostB = new MemoryStream();
        var projectA = EventProject("project-a");
        var projectB = EventProject("project-b");
        sink.RegisterLiveHost(projectA, "connection-a", "session-a", hostA, 1, new SemaphoreSlim(1, 1));
        sink.RegisterLiveHost(projectB, "connection-b", "session-b", hostB, 1, new SemaphoreSlim(1, 1));
        Assert.Throws<ProjectHostBusyException>(() => sink.RegisterLiveHost(projectA, "other", "other",
            new MemoryStream(), 1, new SemaphoreSlim(1, 1)));

        using var document = JsonDocument.Parse("{}");
        var pendingA = sink.RequestApplyAsync(projectA, document.RootElement.Clone(), CancellationToken.None);
        var pendingB = sink.RequestApplyAsync(projectB, document.RootElement.Clone(), CancellationToken.None);
        sink.UnregisterLiveHost(projectA, "connection-a", "session-a");
        await Assert.ThrowsAsync<IOException>(() => pendingA);
        Assert.False(pendingB.IsCompleted);
        var resultB = new WorkerEventResultEnvelope(ReadEventId(hostB), WorkerEventOutcome.Completed,
            document.RootElement.Clone(), 1);
        sink.AcceptResult(resultB);
        await pendingB.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task EventSendAndDisconnectAreAtomicAtHostLookupBoundary()
    {
        var hosts = new BlockingHostRegistry();
        await using var sink = new WorkerEventSink(hosts);
        using var host = new MemoryStream();
        var project = EventProject("lookup-race");
        hosts.Register(project, new ProjectHostRegistration("connection", "session", 1, host,
            new SemaphoreSlim(1, 1)));
        using var document = JsonDocument.Parse("{}");

        var pending = Task.Run(() => sink.RequestApplyAsync(project, document.RootElement.Clone(), CancellationToken.None));
        await hosts.LookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var disconnect = Task.Run(() => sink.UnregisterLiveHost(project, "connection", "session"));
        await Task.Delay(50);
        Assert.False(disconnect.IsCompleted);

        hosts.ReleaseLookup.TrySetResult(true);
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        await disconnect;
    }

    [Fact]
    public async Task WorkerServerRoutesEventResultsOverTheLiveDuplexConnection()
    {
        var name = "worker-test-" + Guid.NewGuid().ToString("N");
        await using var server = WorkerServer.CreateForTests(name);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.StartAsync(cancellation.Token);
        using var client = await new SIL.Motif.Client.Worker.WorkerClient().ConnectAsync(
            server.EndpointName, Handshake(), TimeSpan.FromSeconds(5), cancellation.Token);
        var project = EventProject("round-trip");
        server.RegisterLiveHost(project, client.ServerConnectionId!);
        using var document = JsonDocument.Parse("{\"roundTrip\":true}");
        TaskCompletionSource<WorkerEventEnvelope>? received = null;
        client.EventReceived += (_, value) => received?.TrySetResult(value);
        var requests = new (string Name, Func<ProjectLocator, JsonElement, CancellationToken, Task<WorkerEventResultEnvelope>> Request)[]
        {
            (WorkerCommands.BaselineRefreshRequested, server.EventSink.RequestBaselineRefreshAsync),
            (WorkerCommands.ApplyRequested, server.EventSink.RequestApplyAsync),
            (WorkerCommands.ReconciliationRequested, server.EventSink.RequestReconciliationAsync),
            (WorkerCommands.CancellationRequested, server.EventSink.RequestCancellationAsync),
        };
        foreach (var request in requests)
        {
            received = new TaskCompletionSource<WorkerEventEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = request.Request(project, document.RootElement.Clone(), cancellation.Token);
            var eventEnvelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(request.Name, eventEnvelope.Event);
            var result = new WorkerEventResultEnvelope(eventEnvelope.EventId,
                WorkerEventOutcome.Completed, document.RootElement.Clone(), client.Negotiated.ProtocolVersion);
            await client.CompleteEventAsync(result, cancellation.Token);
            var completed = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(result.EventId, completed.EventId);
            Assert.Equal(result.Outcome, completed.Outcome);
            Assert.Equal(result.Payload.GetRawText(), completed.Payload.GetRawText());
        }
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ProductionConstructionUsesTheCurrentUserSid()
    {
        await using var server = new WorkerServer();
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        Assert.Equal(WorkerServer.GetControlPipeName(), server.EndpointName);
        Assert.Equal(WorkerServer.GetOwnerMutexName(), server.OwnerName);
        Assert.EndsWith(sid, server.EndpointName, StringComparison.Ordinal);
        Assert.EndsWith(sid, server.OwnerName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerServerRuntimeCompositionUsesHostAndPendingActivity()
    {
        await using var server = WorkerServer.CreateForTests("worker-runtime-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(Path.GetTempPath(), "motif-server-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ownership = WorkspaceOwnership.Bootstrap(root);
            var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
            using var runtimes = server.CreateRuntimeRegistry(catalog,
                (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs),
                    new WorkspaceCleaner(ownership)));
            var project = new ProjectLocator(Path.Combine(root, "runtime.fwdata"), "runtime");
            var runtime = runtimes.GetOrOpen(project);
            using var host = new MemoryStream();
            using var writeGate = new SemaphoreSlim(1, 1);
            server.EventSink.RegisterLiveHost(project, "connection", "session", host, 1, writeGate);

            Assert.False(runtimes.TryReleaseIfIdle(runtime.WorkspaceKey));
            server.EventSink.UnregisterLiveHost(project, "connection", "session");
            Assert.True(runtimes.TryReleaseIfIdle(runtime.WorkspaceKey));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EventSinkCompletesOnlyForTheMatchingResult()
    {
        using var host = new MemoryStream();
        var sink = new WorkerEventSink();
        var project = EventProject("matching");
        sink.RegisterLiveHost(project, "connection", "session", host, 1, new SemaphoreSlim(1, 1));
        using var document = JsonDocument.Parse("{\"request\":true}");
        var pending = sink.RequestApplyAsync(project, document.RootElement.Clone(), CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => host.Length >= 4, TimeSpan.FromSeconds(1)));
        var result = new WorkerEventResultEnvelope(
            ReadEventId(host), WorkerEventOutcome.Completed, document.RootElement.Clone(), 1);

        sink.AcceptResult(result);
        Assert.Same(result, await pending.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => sink.AcceptResult(result));
        Assert.Throws<InvalidOperationException>(() => sink.AcceptResult(
            new WorkerEventResultEnvelope("unknown", WorkerEventOutcome.Failed,
                document.RootElement.Clone(), 1)));
    }

    [Fact]
    public async Task EventSinkRefusesMoreThanItsBoundedPendingCorrelationSet()
    {
        using var host = new MemoryStream();
        await using var sink = new WorkerEventSink();
        var project = EventProject("bounded");
        sink.RegisterLiveHost(project, "connection", "session", host, 1, new SemaphoreSlim(1, 1));
        using var document = JsonDocument.Parse("{}");
        var pending = new System.Collections.Generic.List<Task<WorkerEventResultEnvelope>>();
        for (var index = 0; index < WorkerEventSink.PendingCorrelationCapacity; index++)
            pending.Add(sink.RequestApplyAsync(project, document.RootElement.Clone(), CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.RequestApplyAsync(project,
            document.RootElement.Clone(), CancellationToken.None));
        sink.UnregisterLiveHost(project, "connection", "session");
        await Task.WhenAll(pending.Select(task => task.ContinueWith(_ => { })));
    }

    [Fact]
    public async Task EventSinkDisposalWaitsForAnActiveWriterAndFaultsPendingResult()
    {
        await using var sink = new WorkerEventSink();
        var host = new BlockingStream();
        var project = EventProject("dispose");
        sink.RegisterLiveHost(project, "connection", "session", host, 1, new SemaphoreSlim(1, 1));
        using var document = JsonDocument.Parse("{}");
        var pending = sink.RequestApplyAsync(project, document.RootElement.Clone(), CancellationToken.None);
        await host.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var disposing = sink.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        host.ReleaseWrite.TrySetResult(true);
        await disposing;
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => pending);
    }

    [Fact]
    public async Task BinaryTransferPublishesOnlyAfterMatchingCompletion()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        await using var server = new BinaryTransferServer(directory);
        var bytes = Encoding.UTF8.GetBytes("binary worker payload");
        var offer = server.CreateOffer(bytes.Length, TimeSpan.FromSeconds(10));
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(bytes);
        }
        Assert.Empty(Directory.GetFiles(directory, "*.ready"));
        using var digest = SHA256.Create();
        var completion = new BinaryTransferCompletion(
            offer.TransferId, bytes.Length, Convert.ToHexString(digest.ComputeHash(bytes)).ToLowerInvariant());
        var published = await server.CompleteAsync(completion);

        Assert.True(File.Exists(published));
        Assert.EndsWith(".ready", published, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(published));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task BinaryTransferRejectsWrongDigestAndDeletesTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        await using var server = new BinaryTransferServer(directory);
        var offer = server.CreateOffer(8, TimeSpan.FromSeconds(10));
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(Encoding.UTF8.GetBytes("payload"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.CompleteAsync(
            new BinaryTransferCompletion(offer.TransferId, 7, new string('0', 64))));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task BinaryTransferRejectsExcessBytesAndRemovesOffer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        await using var server = new BinaryTransferServer(directory);
        var offer = server.CreateOffer(1, TimeSpan.FromSeconds(10));
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(new byte[] { 1, 2 });
        }
        await Assert.ThrowsAnyAsync<Exception>(() => server.CompleteAsync(
            new BinaryTransferCompletion(offer.TransferId, 2, new string('0', 64))));
        Assert.Empty(Directory.GetFiles(directory));
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.CompleteAsync(
            new BinaryTransferCompletion(offer.TransferId, 0, new string('0', 64))));
    }

    [Fact]
    public async Task BinaryTransferExpiresWaitingWithInjectedClock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        var clock = new ManualClock();
        await using var server = new BinaryTransferServer(directory, clock);
        var offer = server.CreateOffer(10, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<Exception>(() => server.CompleteAsync(
            new BinaryTransferCompletion(offer.TransferId, 0, new string('0', 64))));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task BinaryTransferExpiresWhileReceiving()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        var clock = new ManualClock();
        await using var server = new BinaryTransferServer(directory, clock);
        var offer = server.CreateOffer(10, TimeSpan.FromSeconds(1));
        using var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<Exception>(() => server.CompleteAsync(
            new BinaryTransferCompletion(offer.TransferId, 0, new string('0', 64))));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task BinaryTransferUsesMonotonicDeadlineAcrossWallClockJump()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        var clock = new ManualClock();
        await using var server = new BinaryTransferServer(directory, clock);
        var bytes = Encoding.UTF8.GetBytes("wall-clock jump");
        var offer = server.CreateOffer(bytes.Length, TimeSpan.FromSeconds(1));
        clock.JumpWall(TimeSpan.FromDays(2));
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(bytes);
        }
        using var digest = SHA256.Create();
        var completion = new BinaryTransferCompletion(offer.TransferId, bytes.Length,
            Convert.ToHexString(digest.ComputeHash(bytes)).ToLowerInvariant());
        var published = await server.CompleteAsync(completion);
        Assert.True(File.Exists(published));
    }

    [Fact]
    public void ProductionWorkTrackerLeasesKeepWorkerAliveUntilReleased()
    {
        using var tracker = new WorkerWorkTracker();
        Assert.False(tracker.HasQueuedRunningOrWaitingWork);
        using var lease = tracker.AcquireLease();
        Assert.True(tracker.HasQueuedRunningOrWaitingWork);
        lease.Dispose();
        Assert.False(tracker.HasQueuedRunningOrWaitingWork);
    }

    [Fact]
    public async Task GracefulOwnerDisposalLetsSuccessorAcquireWithoutAbandonment()
    {
        var identity = "mutex-disposal-" + Guid.NewGuid().ToString("N");
        await using var first = WorkerServer.CreateForTests(identity);
        await using var second = WorkerServer.CreateForTests(identity);
        Assert.True(first.TryAcquireOwnership());
        Assert.False(second.TryAcquireOwnership());
        await first.DisposeAsync();
        Assert.True(second.TryAcquireOwnership());
    }

    [Fact]
    public async Task BinaryTransferDeletesTemporaryFileWhenClientDisconnectsIncomplete()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        await using var server = new BinaryTransferServer(directory);
        var offer = server.CreateOffer(100, TimeSpan.FromSeconds(10));
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(new byte[] { 1, 2, 3 });
        }
        await Assert.ThrowsAnyAsync<Exception>(() => server.CompleteAsync(
            new BinaryTransferCompletion(offer.TransferId, 3, new string('0', 64))));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task BinaryTransferReportsCleanupFailureForAnOwnedPathItCannotDelete()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        using var connected = new ManualResetEventSlim(false);
        using var allowFileOpen = new ManualResetEventSlim(false);
        await using var server = BinaryTransferServer.CreateWithLifecycleProbes(directory,
            onConnectionAccepted: () =>
            {
                connected.Set();
                allowFileOpen.Wait();
            });
        var offer = server.CreateOffer(3, TimeSpan.FromSeconds(10));
        var temporaryPath = Path.Combine(directory, offer.TransferId + ".tmp");
        Directory.CreateDirectory(temporaryPath);
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            try
            {
                await client.ConnectAsync(5000);
                Assert.True(connected.Wait(TimeSpan.FromSeconds(1)));
                await client.WriteAsync(new byte[] { 1, 2, 3 });
            }
            finally
            {
                allowFileOpen.Set();
            }
        }
        await Assert.ThrowsAnyAsync<Exception>(() => server.CompleteAsync(
            new BinaryTransferCompletion(offer.TransferId, 3, new string('0', 64))));
        Assert.Contains(temporaryPath, server.CleanupFailures);
        Directory.Delete(temporaryPath);
        server.RetryCleanupFailures();
        Assert.DoesNotContain(temporaryPath, server.CleanupFailures);
    }

    [Fact]
    public async Task BinaryTransferCleansTemporaryFileWhenPublicationMoveFails()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        await using var server = new BinaryTransferServer(directory);
        var bytes = Encoding.UTF8.GetBytes("move failure");
        var offer = server.CreateOffer(bytes.Length, TimeSpan.FromSeconds(10));
        var readyPath = Path.Combine(directory, offer.TransferId + ".ready");
        await File.WriteAllTextAsync(readyPath, "already published");
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(bytes);
        }
        using var digest = SHA256.Create();
        await Assert.ThrowsAnyAsync<Exception>(() => server.CompleteAsync(
            new BinaryTransferCompletion(offer.TransferId, bytes.Length,
                Convert.ToHexString(digest.ComputeHash(bytes)).ToLowerInvariant())));
        Assert.False(File.Exists(Path.Combine(directory, offer.TransferId + ".tmp")));
        Assert.Equal("already published", await File.ReadAllTextAsync(readyPath));
    }

    [Fact]
    public async Task BinaryTransferAllowsOneCompletionAndRejectsReconnect()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        await using var server = new BinaryTransferServer(directory);
        var bytes = Encoding.UTF8.GetBytes("once");
        var offer = server.CreateOffer(bytes.Length, TimeSpan.FromSeconds(10));
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(bytes);
        }
        using var digest = SHA256.Create();
        var completion = new BinaryTransferCompletion(offer.TransferId, bytes.Length,
            Convert.ToHexString(digest.ComputeHash(bytes)).ToLowerInvariant());
        var published = await server.CompleteAsync(completion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.CompleteAsync(completion));
        using var reconnect = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out);
        await Assert.ThrowsAnyAsync<Exception>(() => reconnect.ConnectAsync(500));
        Assert.True(File.Exists(published));
    }

    [Fact]
    public async Task BinaryTransferSerializesConcurrentCompletions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        await using var server = new BinaryTransferServer(directory);
        var bytes = Encoding.UTF8.GetBytes("concurrent");
        var offer = server.CreateOffer(bytes.Length, TimeSpan.FromSeconds(10));
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(bytes);
        }
        using var digest = SHA256.Create();
        var completion = new BinaryTransferCompletion(offer.TransferId, bytes.Length,
            Convert.ToHexString(digest.ComputeHash(bytes)).ToLowerInvariant());
        var completions = await Task.WhenAll(
            server.CompleteAsync(completion).ContinueWith(task => task),
            server.CompleteAsync(completion).ContinueWith(task => task));
        Assert.Equal(1, completions.Count(task => task.Status == TaskStatus.RanToCompletion));
        Assert.Equal(1, completions.Count(task => task.IsFaulted));
        Assert.Single(Directory.GetFiles(directory, "*.ready"));
    }

    [Fact]
    public async Task BinaryOfferRegisteredBeforeDisposeIsDrainedBeforeDisposeCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        using var registered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        await using var server = BinaryTransferServer.CreateWithLifecycleProbes(directory,
            onOfferRegistered: () =>
            {
                registered.Set();
                release.Wait();
            });

        var offerTask = Task.Run(() => server.CreateOffer(10, TimeSpan.FromSeconds(10)));
        Assert.True(registered.Wait(TimeSpan.FromSeconds(1)));
        var disposeTask = Task.Run(async () => await server.DisposeAsync());
        Assert.False(disposeTask.IsCompleted);
        release.Set();
        var offer = await offerTask;
        await disposeTask;
        Assert.Throws<ObjectDisposedException>(() => server.CreateOffer(10, TimeSpan.FromSeconds(1)));
        Assert.False(File.Exists(Path.Combine(directory, offer.TransferId + ".tmp")));
    }

    [Fact]
    public async Task BinaryOfferCountAndBudgetAreBoundedAndReleasedAfterSuccessAndFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        await using var server = new BinaryTransferServer(directory, maximumActiveOffers: 2,
            maximumReservedBytes: 10);
        var first = server.CreateOffer(6, TimeSpan.FromSeconds(10));
        var second = server.CreateOffer(4, TimeSpan.FromSeconds(10));
        Assert.Throws<InvalidOperationException>(() => server.CreateOffer(1, TimeSpan.FromSeconds(10)));

        var firstBytes = Encoding.UTF8.GetBytes("first");
        using (var client = new NamedPipeClientStream(".", first.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(firstBytes);
        }
        using var digest = SHA256.Create();
        await server.CompleteAsync(new BinaryTransferCompletion(first.TransferId, firstBytes.Length,
            Convert.ToHexString(digest.ComputeHash(firstBytes)).ToLowerInvariant()));

        var third = server.CreateOffer(6, TimeSpan.FromSeconds(10));
        var secondBytes = Encoding.UTF8.GetBytes("bad");
        using (var client = new NamedPipeClientStream(".", second.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(secondBytes);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.CompleteAsync(
            new BinaryTransferCompletion(second.TransferId, secondBytes.Length, new string('0', 64))));
        var fourth = server.CreateOffer(4, TimeSpan.FromSeconds(10));
        Assert.NotEqual(third.TransferId, fourth.TransferId);
    }

    [Fact]
    public async Task BinaryDisposeStartedBeforeOfferPreventsOfferEscape()
    {
        var directory = Path.Combine(Path.GetTempPath(), "motif-worker-" + Guid.NewGuid().ToString("N"));
        using var disposing = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        await using var server = BinaryTransferServer.CreateWithLifecycleProbes(directory,
            onDisposed: () =>
            {
                disposing.Set();
                release.Wait();
            });

        var disposeTask = Task.Run(async () => await server.DisposeAsync());
        Assert.True(disposing.Wait(TimeSpan.FromSeconds(1)));
        var offerTask = Task.Run(() => Record.Exception(() => server.CreateOffer(10, TimeSpan.FromSeconds(1))));
        await Task.Delay(20);
        Assert.False(offerTask.IsCompleted);
        release.Set();
        await disposeTask;
        Assert.IsType<ObjectDisposedException>(await offerTask);
    }

    private static Process StartWorkerProcess(int idleMilliseconds)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/SIL.Motif.Worker/bin/Debug/net10.0/SIL.Motif.Worker.exe"));
        var process = Process.Start(new ProcessStartInfo(path,
            "--idle-ms " + idleMilliseconds)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        return process ?? throw new InvalidOperationException("The worker process did not start.");
    }

    private static WorkerHandshakeRequest Handshake() => new WorkerHandshakeRequest(
        "test-client", "1.0.0", new ProtocolRange(1, 1), Array.Empty<string>());

    private static void AssertPipeRules(PipeSecurity security, string ownerSid)
    {
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>();
        Assert.Contains(rules, rule =>
            rule.IdentityReference.Value == ownerSid && rule.AccessControlType == AccessControlType.Allow &&
            rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
        Assert.Contains(rules, rule =>
            rule.IdentityReference.Value == "S-1-5-18" && rule.AccessControlType == AccessControlType.Allow &&
            rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
        Assert.Contains(rules, rule =>
            rule.IdentityReference.Value == "S-1-5-2" && rule.AccessControlType == AccessControlType.Deny &&
            rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
    }

    private static async Task WriteFrameAsync(Stream stream, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var prefix = new byte[] { (byte)payload.Length, (byte)(payload.Length >> 8),
            (byte)(payload.Length >> 16), (byte)(payload.Length >> 24) };
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var length = prefix[0] | prefix[1] << 8 | prefix[2] << 16 | prefix[3] << 24;
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task CompleteEventAsync(
        WorkerEventSink sink, ProjectLocator project, MemoryStream host, JsonElement payload,
        Func<ProjectLocator, JsonElement, CancellationToken, Task<WorkerEventResultEnvelope>> request,
        string expectedName)
    {
        var before = host.Length;
        var pending = request(project, payload, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => host.Length > before, TimeSpan.FromSeconds(1)));
        var bytes = host.ToArray();
        var length = bytes[(int)before] | bytes[(int)before + 1] << 8 |
            bytes[(int)before + 2] << 16 | bytes[(int)before + 3] << 24;
        using var eventDocument = JsonDocument.Parse(bytes.AsMemory((int)before + 4, length));
        var eventElement = eventDocument.RootElement;
        Assert.Equal(expectedName, eventElement.GetProperty("Event").GetString());
        var eventId = eventElement.GetProperty("EventId").GetString()!;
        sink.AcceptResult(new WorkerEventResultEnvelope(eventId, WorkerEventOutcome.Completed, payload, 1));
        await pending.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static string ReadEventId(MemoryStream host)
    {
        var bytes = host.ToArray();
        var length = bytes[0] | bytes[1] << 8 | bytes[2] << 16 | bytes[3] << 24;
        var payload = new byte[length];
        Array.Copy(bytes, 4, payload, 0, length);
        using var eventDocument = JsonDocument.Parse(payload);
        return eventDocument.RootElement.GetProperty("EventId").GetString()!;
    }

    private static ProjectLocator EventProject(string name) =>
        new($"C:\\MotifTests\\{name}.fwdata", name);

    private static string ReadLastEventId(MemoryStream host)
    {
        var bytes = host.ToArray();
        var offset = 0;
        string? result = null;
        while (offset + 4 <= bytes.Length)
        {
            var length = bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24;
            if (length <= 0 || offset + 4 + length > bytes.Length)
                break;
            using var document = JsonDocument.Parse(bytes.AsMemory(offset + 4, length));
            result = document.RootElement.GetProperty("EventId").GetString();
            offset += 4 + length;
        }
        return result ?? throw new InvalidOperationException("No worker event was written.");
    }

    private static List<string> ReadFrames(byte[] bytes)
    {
        var frames = new List<string>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            Assert.True(offset + 4 <= bytes.Length);
            var length = bytes[offset] | bytes[offset + 1] << 8 |
                bytes[offset + 2] << 16 | bytes[offset + 3] << 24;
            Assert.InRange(length, 1, WorkerWire.MaximumFrameBytes);
            Assert.True(offset + 4 + length <= bytes.Length);
            frames.Add(Encoding.UTF8.GetString(bytes, offset + 4, length));
            offset += 4 + length;
        }
        return frames;
    }

    private static string ReadEventId(ChunkingStream stream)
    {
        foreach (var frame in ReadFrames(stream.ToArray()))
        {
            using var document = JsonDocument.Parse(frame);
            if (document.RootElement.TryGetProperty("EventId", out var eventId))
                return eventId.GetString()!;
        }
        throw new InvalidOperationException("No event frame was written.");
    }

    private static async Task WriteSerializedAsync(
        SemaphoreSlim gate, Stream stream, object value, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await WorkerWire.WriteAsync(stream, value, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private sealed class TestWorkTracker : IWorkerWorkTracker
    {
        public TestWorkTracker(bool hasWork) => HasQueuedRunningOrWaitingWork = hasWork;

        public bool HasQueuedRunningOrWaitingWork { get; set; }
    }

    private sealed class ChunkingStream : Stream
    {
        private readonly object _gate = new object();
        private readonly List<byte> _bytes = new List<byte>();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length
        {
            get { lock (_gate) return _bytes.Count; }
        }
        public override long Position { get; set; }
        public byte[] ToArray()
        {
            lock (_gate)
                return _bytes.ToArray();
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                    _bytes.Add(buffer[offset + index]);
                await Task.Yield();
            }
        }
    }

    private sealed class BlockingStream : MemoryStream
    {
        public TaskCompletionSource<bool> WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            WriteStarted.TrySetResult(true);
            await ReleaseWrite.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }

    private sealed class BlockingHostRegistry : IProjectHostRegistry
    {
        private ProjectHostRegistration? _registration;
        private bool _lookupBlocked = true;

        public TaskCompletionSource<bool> LookupStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseLookup { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Register(ProjectLocator project, ProjectHostRegistration registration) =>
            _registration = registration;

        public bool Unregister(ProjectLocator project, string connectionId, string hostSessionId)
        {
            if (_registration is null || !StringComparer.Ordinal.Equals(_registration.ConnectionId, connectionId) ||
                !StringComparer.Ordinal.Equals(_registration.HostSessionId, hostSessionId)) return false;
            _registration = null;
            return true;
        }

        public bool TryGet(ProjectLocator project, out ProjectHostRegistration registration)
        {
            if (_lookupBlocked)
            {
                _lookupBlocked = false;
                LookupStarted.TrySetResult(true);
                ReleaseLookup.Task.GetAwaiter().GetResult();
            }
            registration = _registration!;
            return registration is not null;
        }

        public bool HasRegistration(string workspaceKey) => _registration is not null;

        public void Dispose() => _registration = null;
    }

    private sealed class ManualClock : IWorkerClock
    {
        private readonly object _gate = new object();
        private readonly System.Collections.Generic.List<Waiter> _waiters = new();
        private TaskCompletionSource<bool>? _delayRegistered;
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private TimeSpan _monotonic;

        public DateTimeOffset UtcNow => _now;
        public TimeSpan MonotonicNow => _monotonic;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (delay <= TimeSpan.Zero || _monotonic + delay <= _monotonic)
                    return Task.CompletedTask;
                var waiter = new Waiter(_monotonic + delay);
                _waiters.Add(waiter);
                _delayRegistered?.TrySetResult(true);
                _delayRegistered = null;
                if (cancellationToken.CanBeCanceled)
                    cancellationToken.Register(() => waiter.CompleteCanceled(cancellationToken));
                return waiter.Task;
            }
        }

        public Task WaitForDelayRegistrationAsync()
        {
            lock (_gate)
            {
                if (_waiters.Count != 0)
                    return Task.CompletedTask;
                _delayRegistered ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _delayRegistered.Task;
            }
        }

        public void Advance(TimeSpan by)
        {
            Waiter[] due;
            lock (_gate)
            {
                _now += by;
                _monotonic += by;
                due = _waiters.Where(waiter => waiter.Deadline <= _monotonic).ToArray();
                foreach (var waiter in due)
                    _waiters.Remove(waiter);
            }
            foreach (var waiter in due)
                waiter.Complete();
        }

        public void JumpWall(TimeSpan by) => _now += by;

        private sealed class Waiter
        {
            private readonly TaskCompletionSource<bool> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Waiter(TimeSpan deadline) => Deadline = deadline;
            public TimeSpan Deadline { get; }
            public Task Task => _completion.Task;
            public void Complete() => _completion.TrySetResult(true);
            public void CompleteCanceled(CancellationToken token) => _completion.TrySetCanceled(token);
        }
    }
}
