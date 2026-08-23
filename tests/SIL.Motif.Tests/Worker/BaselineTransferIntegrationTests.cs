using System.Security.Cryptography;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker;
using SIL.Motif.Worker.Baselines;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Projects;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class BaselineTransferIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "motif-baseline-transfer-" + Guid.NewGuid().ToString("N"));

    public BaselineTransferIntegrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RegistryBindsCompletionAndClaimToConnectionAndCanonicalProject()
    {
        var transferRoot = Path.Combine(_root, "transfers");
        var clock = new ManualClock();
        await using var binary = new BinaryTransferServer(transferRoot, clock);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary, clock);
        var project = Project("bound");
        var equivalent = new ProjectLocator(project.FullFwDataPath.Replace("bound.fwdata", ".\\bound.fwdata"),
            project.FieldWorksProjectIdentity);
        var different = Project("different");
        var bytes = Encoding.UTF8.GetBytes("bound bytes");
        var offer = registry.CreateOffer("connection-a", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        var completion = Completion(offer, bytes);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.CompleteAsync("connection-b", completion, CancellationToken.None));
        await registry.CompleteAsync("connection-a", completion, CancellationToken.None);
        Assert.Throws<InvalidOperationException>(() => registry.Claim("connection-b", project, offer.TransferId));
        Assert.Throws<InvalidOperationException>(() => registry.Claim("connection-a", different, offer.TransferId));

        var verified = registry.Claim("connection-a", equivalent, offer.TransferId);
        Assert.Equal(bytes.Length, verified.ByteCount);
        Assert.Equal(completion.Sha256, verified.Sha256);
        Assert.EndsWith(".ready", verified.TemporaryPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RegistryRejectsCompletionWithWrongDigestOrLength(bool wrongDigest)
    {
        var transferRoot = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        await using var binary = new BinaryTransferServer(transferRoot);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary);
        var bytes = Encoding.UTF8.GetBytes("verified");
        var offer = registry.CreateOffer("connection", Project("invalid"), bytes.Length,
            TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        var valid = Completion(offer, bytes);
        var invalid = wrongDigest
            ? new BinaryTransferCompletion(offer.TransferId, valid.ByteCount, new string('0', 64))
            : new BinaryTransferCompletion(offer.TransferId, valid.ByteCount + 1, valid.Sha256);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.CompleteAsync("connection", invalid, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(transferRoot, offer.TransferId + ".*"));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Claim("connection", Project("invalid"), offer.TransferId));
    }

    [Fact]
    public async Task RegistryClaimIsOneUseAndRechecksReadyFileBytes()
    {
        var transferRoot = Path.Combine(_root, "one-use");
        await using var binary = new BinaryTransferServer(transferRoot);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary);
        var project = Project("one-use");
        var bytes = Encoding.UTF8.GetBytes("original");
        var offer = registry.CreateOffer("connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await registry.CompleteAsync("connection", Completion(offer, bytes), CancellationToken.None);
        var readyPath = Path.Combine(transferRoot, offer.TransferId + ".ready");
        await File.WriteAllTextAsync(readyPath, "tampered");

        Assert.Throws<InvalidOperationException>(() => registry.Claim("connection", project, offer.TransferId));
        Assert.False(File.Exists(readyPath));
        Assert.Throws<InvalidOperationException>(() => registry.Claim("connection", project, offer.TransferId));

        var secondBytes = Encoding.UTF8.GetBytes("second");
        var second = registry.CreateOffer("connection", project, secondBytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(second, secondBytes);
        await registry.CompleteAsync("connection", Completion(second, secondBytes), CancellationToken.None);
        _ = registry.Claim("connection", project, second.TransferId);
        Assert.Throws<InvalidOperationException>(() => registry.Claim("connection", project, second.TransferId));
    }

    [Fact]
    public async Task DisconnectDeletesReceivingAndReadyTransfersForOnlyThatConnection()
    {
        var transferRoot = Path.Combine(_root, "disconnect");
        await using var binary = new BinaryTransferServer(transferRoot);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary);
        var project = Project("disconnect");
        var receiving = registry.CreateOffer("connection-a", project, 10, TimeSpan.FromMinutes(1));
        var bytes = Encoding.UTF8.GetBytes("ready");
        var ready = registry.CreateOffer("connection-a", project, bytes.Length, TimeSpan.FromMinutes(1));
        var retained = registry.CreateOffer("connection-b", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(ready, bytes);
        await registry.CompleteAsync("connection-a", Completion(ready, bytes), CancellationToken.None);
        await UploadAsync(retained, bytes);
        await registry.CompleteAsync("connection-b", Completion(retained, bytes), CancellationToken.None);

        await registry.ReleaseConnectionAsync("connection-a");

        Assert.False(File.Exists(Path.Combine(transferRoot, receiving.TransferId + ".tmp")));
        Assert.False(File.Exists(Path.Combine(transferRoot, ready.TransferId + ".ready")));
        Assert.True(File.Exists(Path.Combine(transferRoot, retained.TransferId + ".ready")));
    }

    [Fact]
    public async Task ExpiredReadyTransferCannotBeClaimedAndIsDeleted()
    {
        var transferRoot = Path.Combine(_root, "expiry");
        var clock = new ManualClock();
        await using var binary = new BinaryTransferServer(transferRoot, clock);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary, clock);
        var project = Project("expiry");
        var bytes = Encoding.UTF8.GetBytes("expires");
        var offer = registry.CreateOffer("connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await registry.CompleteAsync("connection", Completion(offer, bytes), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Throws<InvalidOperationException>(() => registry.Claim("connection", project, offer.TransferId));
        Assert.False(File.Exists(Path.Combine(transferRoot, offer.TransferId + ".ready")));
    }

    [Fact]
    public async Task ReadyTransferIsDeletedWhenItsOfferExpiresWithoutAClaim()
    {
        var transferRoot = Path.Combine(_root, "expiry-cleanup");
        var clock = new ManualClock();
        await using var binary = new BinaryTransferServer(transferRoot, clock);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary, clock);
        var bytes = Encoding.UTF8.GetBytes("expires unattended");
        var offer = registry.CreateOffer("connection", Project("expiry-cleanup"), bytes.Length,
            TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await registry.CompleteAsync("connection", Completion(offer, bytes), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.True(SpinWait.SpinUntil(
            () => !File.Exists(Path.Combine(transferRoot, offer.TransferId + ".ready")),
            TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ServerComposesOfferBesideJobStatusAndAcceptsRawCompletionWithUnknownProperties()
    {
        var workerRoot = Path.Combine(_root, "composed");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("composed");
        _ = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, new[] { "baseline.v1", "jobs.v1" },
            cancellation.Token);
        var handshake = WorkerWire.Deserialize<WorkerHandshakeOffer>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var offerRequest = Envelope("offer", WorkerCommands.BaselineOffer, new BaselineOfferRequest(project));
        await WorkerWire.WriteAsync(pipe, offerRequest, cancellation.Token);
        var offerEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var response = JsonSerializer.Deserialize<BaselineOfferResponse>(
            offerEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!;
        var bytes = Encoding.UTF8.GetBytes("raw completion");
        await UploadAsync(response.Offer!, bytes);
        var completion = Completion(response.Offer!, bytes);
        await WriteJsonFrameAsync(pipe,
            $$"""{"TransferId":"{{completion.TransferId}}","ByteCount":{{completion.ByteCount}},"Sha256":"{{completion.Sha256}}","Future":true}""",
            cancellation.Token);
        await WorkerWire.WriteAsync(pipe, Envelope("status", WorkerCommands.JobStatus,
            new JobStatusRequest(project, "missing")), cancellation.Token);

        var statusEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var status = JsonSerializer.Deserialize<JobStatusResponse>(
            statusEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!;
        var verified = server.BaselineTransfers.Claim(handshake.ConnectionId!, project, response.Offer!.TransferId);
        Assert.False(status.Found);
        Assert.Equal(bytes.Length, verified.ByteCount);

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RawCompletionMissingARequiredPropertyClosesTheConnection()
    {
        var workerRoot = Path.Combine(_root, "malformed");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-malformed-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, new[] { "baseline.v1" }, cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        await WriteJsonFrameAsync(pipe, "{\"TransferId\":\"transfer\",\"ByteCount\":0}", cancellation.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => WorkerWire.ReadAsync(pipe, cancellation.Token));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BaselineOfferRequiresNegotiatedCapability()
    {
        var workerRoot = Path.Combine(_root, "capability");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-capability-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, Array.Empty<string>(), cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        await WorkerWire.WriteAsync(pipe, Envelope("offer", WorkerCommands.BaselineOffer,
            new BaselineOfferRequest(Project("capability"))), cancellation.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => WorkerWire.ReadAsync(pipe, cancellation.Token));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StartupCleanupRunsAfterOwnershipAndDeletesOnlyTopLevelTransferOrphans()
    {
        var workerRoot = Path.Combine(_root, "startup");
        var transferRoot = Path.Combine(workerRoot, "transfers");
        var nested = Path.Combine(transferRoot, "nested.ready");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(transferRoot);
        var temporary = Path.Combine(transferRoot, "orphan.tmp");
        var ready = Path.Combine(transferRoot, "orphan.ready");
        var unrelated = Path.Combine(transferRoot, "keep.bin");
        var outside = Path.Combine(workerRoot, "outside.ready");
        File.WriteAllText(temporary, "delete");
        File.WriteAllText(ready, "delete");
        File.WriteAllText(unrelated, "keep");
        File.WriteAllText(outside, "keep");
        File.WriteAllText(Path.Combine(nested, "sentinel.tmp"), "keep");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-startup-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        Assert.True(File.Exists(temporary));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var runtimes = ComposeRuntime(server, workerRoot);

        var running = server.StartAsync(cancellation.Token);

        Assert.True(SpinWait.SpinUntil(() => !File.Exists(temporary) && !File.Exists(ready),
            TimeSpan.FromSeconds(2)));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(Path.Combine(nested, "sentinel.tmp")));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void StartupCleanupRefusesAReparseTransferRootWithoutTouchingTarget()
    {
        var outside = Path.Combine(_root, "outside-target");
        var transferRoot = Path.Combine(_root, "transfer-link");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.ready");
        File.WriteAllText(sentinel, "keep");
        try { Directory.CreateSymbolicLink(transferRoot, outside); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<InvalidOperationException>(() => BaselineTransferRegistry.CleanupStartup(transferRoot));
        Assert.True(File.Exists(sentinel));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private ProjectLocator Project(string name) =>
        new(Path.Combine(_root, name + ".fwdata"), name);

    private static WorkerEnvelope Envelope<T>(string id, string command, T payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, WorkerJson.CreateOptions()));
        return new WorkerEnvelope(id, command, document.RootElement.Clone(), 1);
    }

    private static ProjectRuntimeRegistry ComposeRuntime(WorkerServer server, string workerRoot)
    {
        var ownership = WorkspaceOwnership.Bootstrap(workerRoot);
        var catalog = new ProjectDatabaseCatalog(MotifSchema.CurrentSchema, new Version(1, 0));
        return server.CreateRuntimeRegistry(catalog,
            (jobs, key) => new WorkerRecoveryCoordinator(new WorkerRecovery(jobs),
                new WorkspaceCleaner(ownership)));
    }

    private static async Task<NamedPipeClientStream> ConnectRawAsync(string pipeName,
        IReadOnlyList<string> capabilities, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, cancellationToken);
        await WorkerWire.WriteAsync(pipe, new WorkerHandshakeRequest(
            "test", "1.0.0", new ProtocolRange(1, 1), capabilities), cancellationToken);
        return pipe;
    }

    private static async Task UploadAsync(BinaryTransferOffer offer, byte[] bytes)
    {
        await using var pipe = new NamedPipeClientStream(".", offer.PipeName, PipeDirection.Out,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await pipe.WriteAsync(bytes);
    }

    private static BinaryTransferCompletion Completion(BinaryTransferOffer offer, byte[] bytes) =>
        new(offer.TransferId, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static async Task WriteJsonFrameAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var prefix = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private sealed class ManualClock : IWorkerClock
    {
        private readonly List<Waiter> _waiters = new();
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;
        public TimeSpan MonotonicNow { get; private set; }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (_waiters)
            {
                var waiter = new Waiter(MonotonicNow + delay);
                _waiters.Add(waiter);
                cancellationToken.Register(() => waiter.Completion.TrySetCanceled(cancellationToken));
                return waiter.Completion.Task;
            }
        }

        public void Advance(TimeSpan by)
        {
            UtcNow += by;
            MonotonicNow += by;
            Waiter[] due;
            lock (_waiters)
            {
                due = _waiters.Where(item => item.Deadline <= MonotonicNow).ToArray();
                foreach (var waiter in due)
                    _waiters.Remove(waiter);
            }
            foreach (var waiter in due)
                waiter.Completion.TrySetResult();
        }

        private sealed record Waiter(TimeSpan Deadline)
        {
            public TaskCompletionSource Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
