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
using SIL.Motif.Contract.Worker;
using SIL.Motif.Worker;
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

        clock.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(20);
        Assert.False(running.IsCompleted);

        tracker.HasQueuedRunningOrWaitingWork = false;
        clock.Advance(TimeSpan.FromSeconds(5));
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
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
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
        sink.RegisterLiveHost(host, 1);
        Assert.Throws<InvalidOperationException>(() => sink.RegisterLiveHost(new MemoryStream(), 1));
        using var document = JsonDocument.Parse("{}");
        await CompleteEventAsync(sink, host, document.RootElement.Clone(),
            sink.RequestBaselineRefreshAsync, WorkerCommands.BaselineRefreshRequested);
        await CompleteEventAsync(sink, host, document.RootElement.Clone(),
            sink.RequestApplyAsync, WorkerCommands.ApplyRequested);
        await CompleteEventAsync(sink, host, document.RootElement.Clone(),
            sink.RequestReconciliationAsync, WorkerCommands.ReconciliationRequested);
        using var cancellation = new CancellationTokenSource();
        var beforeCancellation = host.Length;
        var cancelled = sink.RequestCancellationAsync(document.RootElement.Clone(), cancellation.Token);
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
        sink.RegisterLiveHost(host, 1);
        using var document = JsonDocument.Parse("{}");
        var pending = sink.RequestApplyAsync(document.RootElement.Clone(), CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => host.Length >= 4, TimeSpan.FromSeconds(1)));
        sink.UnregisterLiveHost(host);
        await Assert.ThrowsAsync<IOException>(() => pending);
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
        using var document = JsonDocument.Parse("{\"roundTrip\":true}");
        TaskCompletionSource<WorkerEventEnvelope>? received = null;
        client.EventReceived += (_, value) => received?.TrySetResult(value);
        var requests = new (string Name, Func<JsonElement, CancellationToken, Task<WorkerEventResultEnvelope>> Request)[]
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
            var pending = request.Request(document.RootElement.Clone(), cancellation.Token);
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
    public async Task EventSinkCompletesOnlyForTheMatchingResult()
    {
        using var host = new MemoryStream();
        var sink = new WorkerEventSink();
        sink.RegisterLiveHost(host, 1);
        using var document = JsonDocument.Parse("{\"request\":true}");
        var pending = sink.RequestApplyAsync(document.RootElement.Clone(), CancellationToken.None);
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
        await using var server = new BinaryTransferServer(directory);
        var offer = server.CreateOffer(3, TimeSpan.FromSeconds(10));
        var temporaryPath = Path.Combine(directory, offer.TransferId + ".tmp");
        Directory.CreateDirectory(temporaryPath);
        using (var client = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
                   PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(new byte[] { 1, 2, 3 });
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
        Assert.Throws<InvalidOperationException>(() => server.CompleteAsync(completion).GetAwaiter().GetResult());
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
        WorkerEventSink sink, MemoryStream host, JsonElement payload,
        Func<JsonElement, CancellationToken, Task<WorkerEventResultEnvelope>> request,
        string expectedName)
    {
        var before = host.Length;
        var pending = request(payload, CancellationToken.None);
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

    private sealed class TestWorkTracker : IWorkerWorkTracker
    {
        public TestWorkTracker(bool hasWork) => HasQueuedRunningOrWaitingWork = hasWork;

        public bool HasQueuedRunningOrWaitingWork { get; set; }
    }

    private sealed class ManualClock : IWorkerClock
    {
        private readonly object _gate = new object();
        private readonly System.Collections.Generic.List<Waiter> _waiters = new();
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
                if (cancellationToken.CanBeCanceled)
                    cancellationToken.Register(() => waiter.CompleteCanceled(cancellationToken));
                return waiter.Task;
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
