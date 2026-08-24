using System.Security.Cryptography;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SIL.Motif.Contract.Baselines;
using SIL.Motif.Contract.Jobs;
using SIL.Motif.Contract.Projects;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Host.Store;
using SIL.Motif.Tests.TestFixtures;
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
    public async Task ClaimedTransferReleaseRetriesUntilAFileLockClears()
    {
        var transferRoot = Path.Combine(_root, "claim-release-retry");
        using var firstAttemptFinished = new ManualResetEventSlim(false);
        using var allowRetry = new ManualResetEventSlim(false);
        var attempts = 0;
        await using var binary = new BinaryTransferServer(transferRoot);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary,
            onClaimReleaseAttempt: attempt =>
            {
                attempts = attempt;
                if (attempt != 1) return;
                firstAttemptFinished.Set();
                allowRetry.Wait();
            });
        var project = Project("claim-release-retry");
        var bytes = Encoding.UTF8.GetBytes("claimed bytes");
        var offer = registry.CreateOffer("connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await registry.CompleteAsync("connection", Completion(offer, bytes), CancellationToken.None);
        var claimed = registry.Claim("connection", project, offer.TransferId);
        using var locked = new FileStream(claimed.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var release = Task.Run(() => registry.ReleaseClaimAsync("connection", claimed.TransferId));
        Assert.True(firstAttemptFinished.Wait(TimeSpan.FromSeconds(1)));
        Assert.False(release.IsCompleted);
        locked.Dispose();
        allowRetry.Set();

        Assert.True(await release);
        Assert.True(attempts >= 2);
        Assert.False(File.Exists(claimed.TemporaryPath));
    }

    [Fact]
    public async Task PersistentlyLockedClaimRemainsForStartupRecoveryAfterDisconnectAndShutdown()
    {
        var transferRoot = Path.Combine(_root, "claim-startup-recovery");
        await using var binary = new BinaryTransferServer(transferRoot);
        var registry = new BaselineTransferRegistry(transferRoot, binary);
        var project = Project("claim-startup-recovery");
        var bytes = Encoding.UTF8.GetBytes("claimed bytes");
        var offer = registry.CreateOffer("connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await registry.CompleteAsync("connection", Completion(offer, bytes), CancellationToken.None);
        var claimed = registry.Claim("connection", project, offer.TransferId);
        using var locked = new FileStream(claimed.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(await registry.ReleaseClaimAsync("connection", claimed.TransferId));
        await registry.ReleaseConnectionAsync("connection");
        await registry.DisposeAsync();
        Assert.True(File.Exists(claimed.TemporaryPath));
        locked.Dispose();

        Assert.True(File.Exists(claimed.TemporaryPath));
        BaselineTransferRegistry.CleanupStartup(transferRoot);
        Assert.False(File.Exists(claimed.TemporaryPath));
    }

    [Fact]
    public async Task BackgroundCleanupDeletesAClaimAfterItsLockClearsDuringTheWorkerLifetime()
    {
        var transferRoot = Path.Combine(_root, "claim-background-recovery");
        await using var binary = new BinaryTransferServer(transferRoot);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary);
        var project = Project("claim-background-recovery");
        var bytes = Encoding.UTF8.GetBytes("claimed bytes");
        var offer = registry.CreateOffer("connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await registry.CompleteAsync("connection", Completion(offer, bytes), CancellationToken.None);
        var claimed = registry.Claim("connection", project, offer.TransferId);
        using var locked = new FileStream(claimed.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(await registry.ReleaseClaimAsync("connection", claimed.TransferId));
        await registry.ReleaseConnectionAsync("connection");
        Assert.True(File.Exists(claimed.TemporaryPath));
        locked.Dispose();

        Assert.True(SpinWait.SpinUntil(() => !File.Exists(claimed.TemporaryPath),
            TimeSpan.FromSeconds(3)));
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
    public async Task ClaimFailsOnlyAfterDisconnectHasDeletedReadyFile()
    {
        var transferRoot = Path.Combine(_root, "release-race");
        using var releaseOwns = new ManualResetEventSlim(false);
        using var allowCleanup = new ManualResetEventSlim(false);
        await using var binary = new BinaryTransferServer(transferRoot);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary,
            onReleaseRemoved: () =>
            {
                releaseOwns.Set();
                allowCleanup.Wait();
            });
        var project = Project("release-race");
        var bytes = Encoding.UTF8.GetBytes("claimed or cleaned");
        var offer = registry.CreateOffer("connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await registry.CompleteAsync("connection", Completion(offer, bytes), CancellationToken.None);
        var readyPath = Path.Combine(transferRoot, offer.TransferId + ".ready");
        var releasing = Task.Run(() => registry.ReleaseConnectionAsync("connection"));
        Assert.True(releaseOwns.Wait(TimeSpan.FromSeconds(1)));

        var claiming = Task.Run(() => registry.Claim("connection", project, offer.TransferId));

        try
        {
            Assert.False(File.Exists(readyPath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => claiming);
        }
        finally
        {
            allowCleanup.Set();
        }
        await releasing;
        Assert.False(File.Exists(readyPath));
    }

    [Fact]
    public async Task DisconnectRetriesCleanupWhenClaimWinsOwnership()
    {
        var transferRoot = Path.Combine(_root, "claim-race");
        using var releaseCandidate = new ManualResetEventSlim(false);
        using var allowRelease = new ManualResetEventSlim(false);
        await using var binary = new BinaryTransferServer(transferRoot);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary,
            onReleaseCandidate: () =>
            {
                releaseCandidate.Set();
                allowRelease.Wait();
            });
        var project = Project("claim-race");
        var bytes = Encoding.UTF8.GetBytes("claim owns these bytes");
        var offer = registry.CreateOffer("connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await registry.CompleteAsync("connection", Completion(offer, bytes), CancellationToken.None);
        var releasing = Task.Run(() => registry.ReleaseConnectionAsync("connection"));
        Assert.True(releaseCandidate.Wait(TimeSpan.FromSeconds(1)));

        var claimed = registry.Claim("connection", project, offer.TransferId);

        try
        {
            Assert.True(File.Exists(claimed.TemporaryPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(claimed.TemporaryPath));
        }
        finally
        {
            allowRelease.Set();
        }
        await releasing;
        Assert.False(File.Exists(claimed.TemporaryPath));
    }

    [RequiresSymbolicLinkFact]
    public async Task CleanupRefusesDeletionAfterTransferRootBecomesAReparsePoint()
    {
        var transferRoot = Path.Combine(_root, "replace-root");
        var parkedRoot = Path.Combine(_root, "parked-root");
        var outside = Path.Combine(_root, "outside-delete-target");
        await using var binary = new BinaryTransferServer(transferRoot);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary);
        var project = Project("replace-root");
        var bytes = Encoding.UTF8.GetBytes("verified before replacement");
        var offer = registry.CreateOffer("connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await registry.CompleteAsync("connection", Completion(offer, bytes), CancellationToken.None);
        Directory.Move(transferRoot, parkedRoot);
        Directory.CreateDirectory(outside);
        var outsideSentinel = Path.Combine(outside, offer.TransferId + ".ready");
        await File.WriteAllTextAsync(outsideSentinel, "outside");
        Directory.CreateSymbolicLink(transferRoot, outside);

        try
        {
            await registry.ReleaseConnectionAsync("connection");
            Assert.True(File.Exists(outsideSentinel));
            Assert.True(File.Exists(Path.Combine(parkedRoot, offer.TransferId + ".ready")));
        }
        finally
        {
            if (Directory.Exists(transferRoot) &&
                (File.GetAttributes(transferRoot) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(transferRoot);
            if (!Directory.Exists(transferRoot))
                Directory.Move(parkedRoot, transferRoot);
        }
    }

    [Fact]
    public async Task OfferHandlerDoesNotReportDisposedRegistryAsCapacityExhaustion()
    {
        var transferRoot = Path.Combine(_root, "disposed-handler");
        await using var binary = new BinaryTransferServer(transferRoot);
        var registry = new BaselineTransferRegistry(transferRoot, binary);
        var handler = new BaselineTransferOfferCommandHandler(registry, "connection");
        await registry.DisposeAsync();
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(
            new BaselineOfferRequest(Project("disposed-handler")), WorkerJson.CreateOptions()));

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            handler.HandleAsync(request.RootElement, CancellationToken.None));
    }

    [Fact]
    public async Task OfferHandlerReportsOnlyTransferEnvelopeExhaustionAsCapacityUnavailable()
    {
        var transferRoot = Path.Combine(_root, "capacity-handler");
        await using var binary = new BinaryTransferServer(transferRoot, maximumActiveOffers: 1);
        await using var registry = new BaselineTransferRegistry(transferRoot, binary);
        var handler = new BaselineTransferOfferCommandHandler(registry, "connection");
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(
            new BaselineOfferRequest(Project("capacity-handler")), WorkerJson.CreateOptions()));
        var first = await handler.HandleAsync(request.RootElement, CancellationToken.None);

        var second = await handler.HandleAsync(request.RootElement, CancellationToken.None);

        var firstResponse = JsonSerializer.Deserialize<BaselineOfferResponse>(
            first.GetRawText(), WorkerJson.CreateOptions())!;
        var secondResponse = JsonSerializer.Deserialize<BaselineOfferResponse>(
            second.GetRawText(), WorkerJson.CreateOptions())!;
        Assert.NotNull(firstResponse.Offer);
        Assert.Equal(BaselineFailureCode.CapacityUnavailable, secondResponse.Failure?.Code);
        Assert.True(secondResponse.Failure?.Retryable);
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
    public async Task ClaimIoFailureReturnsPathFreeTransferInvalidWithoutClosingThePipe()
    {
        var workerRoot = Path.Combine(_root, "claim-io");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-claim-io-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("claim-io");
        _ = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, new[] { "baseline.v1" },
            cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        var bytes = BundleBytes();
        await WorkerWire.WriteAsync(pipe, Envelope("offer", WorkerCommands.BaselineOffer,
            new BaselineOfferRequest(project)), cancellation.Token);
        var offerEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var offer = JsonSerializer.Deserialize<BaselineOfferResponse>(
            offerEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!.Offer!;
        await UploadAsync(offer, bytes);
        await WorkerWire.WriteAsync(pipe, Completion(offer, bytes), cancellation.Token);
        var ready = Path.Combine(workerRoot, "transfers", offer.TransferId + ".ready");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(ready), TimeSpan.FromSeconds(2)));
        using var locked = new FileStream(ready, FileMode.Open, FileAccess.Read, FileShare.Read);
        var publish = new BaselinePublishRequest(project, offer.TransferId, Token(project, bytes));
        await WorkerWire.WriteAsync(pipe, Envelope("publish", WorkerCommands.BaselinePublish, publish),
            cancellation.Token);

        var responseEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var response = JsonSerializer.Deserialize<BaselinePublishResponse>(
            responseEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!;
        Assert.Equal(BaselineFailureCode.TransferInvalid, response.Failure!.Code);
        Assert.DoesNotContain(workerRoot, response.Failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".ready", response.Failure.Message, StringComparison.OrdinalIgnoreCase);
        locked.Dispose();

        await WorkerWire.WriteAsync(pipe, Envelope("retry", WorkerCommands.BaselinePublish, publish),
            cancellation.Token);
        var retryEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var retry = JsonSerializer.Deserialize<BaselinePublishResponse>(
            retryEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!;
        Assert.Equal(BaselineFailureCode.TransferUnknown, retry.Failure!.Code);
        Assert.False(File.Exists(ready));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServerPublishesAndRecordsThroughTheReadyProjectRuntime()
    {
        var workerRoot = Path.Combine(_root, "publication");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-publication-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("publication");
        var runtime = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, new[] { "baseline.v1" },
            cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        await WorkerWire.WriteAsync(pipe, Envelope("offer", WorkerCommands.BaselineOffer,
            new BaselineOfferRequest(project)), cancellation.Token);
        var offerEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var offer = JsonSerializer.Deserialize<BaselineOfferResponse>(
            offerEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!.Offer!;
        var bytes = BundleBytes();
        await UploadAsync(offer, bytes);
        var completion = Completion(offer, bytes);
        await WorkerWire.WriteAsync(pipe, completion, cancellation.Token);
        var token = new BaselineToken(project.FieldWorksProjectIdentity,
            "sha256:" + new string('1', 64), "projection-v1", "2026-08-23T00:00:00Z",
            "sha256:" + completion.Sha256);
        await WorkerWire.WriteAsync(pipe, Envelope("publish", WorkerCommands.BaselinePublish,
            new BaselinePublishRequest(project, offer.TransferId, token)), cancellation.Token);

        var publishEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var response = JsonSerializer.Deserialize<BaselinePublishResponse>(
            publishEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!;

        Assert.Null(response.Failure);
        Assert.Equal(runtime.WorkspaceKey, response.Publication!.ProjectKey);
        Assert.Equal(token, runtime.Baselines.GetCurrent(runtime.WorkspaceKey)!.Token);
        Assert.Single(Directory.GetFiles(_root, "*.motif.db", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.GetFiles(Path.Combine(workerRoot, "transfers"), "*.ready"));

        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServerReturnsTheDurableTokenOnAnExactRetry()
    {
        var workerRoot = Path.Combine(_root, "token-retry");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-token-retry-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("token-retry");
        var runtime = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, new[] { "baseline.v1" },
            cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        var bytes = BundleBytes();
        var token = Token(project, bytes);

        var first = await PublishThroughPipeAsync(pipe, project, bytes, token, cancellation.Token);
        var exactRetry = await PublishThroughPipeAsync(pipe, project, bytes, token, cancellation.Token);

        Assert.Null(first.Failure);
        Assert.Equal(token, exactRetry.Publication!.Token);
        Assert.Equal(token, runtime.Baselines.GetCurrent(runtime.WorkspaceKey)!.Token);
        Assert.Empty(Directory.GetFiles(Path.Combine(workerRoot, "transfers"), "*.ready"));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServerRejectsAnotherTokenForTheSameDigestAndDeletesItsArchive()
    {
        var workerRoot = Path.Combine(_root, "token-mismatch");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-token-mismatch-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("token-mismatch");
        var runtime = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, new[] { "baseline.v1" },
            cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        var bytes = BundleBytes();
        var token = Token(project, bytes);

        var first = await PublishThroughPipeAsync(pipe, project, bytes, token, cancellation.Token);
        var otherToken = new BaselineToken(project.FieldWorksProjectIdentity,
            "sha256:" + new string('2', 64), token.ProjectionVersion, token.CapturedUtc, token.BundleDigest);
        var mismatchedRetry = await PublishThroughPipeAsync(
            pipe, project, bytes, otherToken, cancellation.Token);

        Assert.Null(first.Failure);
        Assert.Equal(BaselineFailureCode.BundleInvalid, mismatchedRetry.Failure!.Code);
        Assert.Equal(token, runtime.Baselines.GetCurrent(runtime.WorkspaceKey)!.Token);
        Assert.Empty(Directory.GetFiles(Path.Combine(workerRoot, "transfers"), "*.ready"));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServerDoesNotOpenAMissingRuntimeForPublication()
    {
        var workerRoot = Path.Combine(_root, "admission");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-admission-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("admission");
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, new[] { "baseline.v1" },
            cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        await WorkerWire.WriteAsync(pipe, Envelope("offer", WorkerCommands.BaselineOffer,
            new BaselineOfferRequest(project)), cancellation.Token);
        var offerEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var offer = JsonSerializer.Deserialize<BaselineOfferResponse>(
            offerEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!.Offer!;
        var bytes = BundleBytes();
        await UploadAsync(offer, bytes);
        var completion = Completion(offer, bytes);
        await WorkerWire.WriteAsync(pipe, completion, cancellation.Token);
        await WorkerWire.WriteAsync(pipe, Envelope("publish", WorkerCommands.BaselinePublish,
            new BaselinePublishRequest(project, offer.TransferId, Token(project, bytes))), cancellation.Token);

        var envelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var response = JsonSerializer.Deserialize<BaselinePublishResponse>(
            envelope.Payload.GetRawText(), WorkerJson.CreateOptions())!;

        Assert.Equal(BaselineFailureCode.ProjectRuntimeUnavailable, response.Failure!.Code);
        Assert.False(File.Exists(ProjectDatabaseCatalog.DatabasePathFor(project)));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServerRefusesAnExistingRuntimeThatIsNotReady()
    {
        var workerRoot = Path.Combine(_root, "non-ready-admission");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-non-ready-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("non-ready-admission");
        var runtime = runtimes.GetOrOpen(project);
        runtime.Dispose();
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, new[] { "baseline.v1" },
            cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        var bytes = BundleBytes();

        var response = await PublishThroughPipeAsync(
            pipe, project, bytes, Token(project, bytes), cancellation.Token);

        Assert.Equal(BaselineFailureCode.ProjectRuntimeUnavailable, response.Failure!.Code);
        Assert.True(File.Exists(ProjectDatabaseCatalog.DatabasePathFor(project)));
        Assert.Single(Directory.GetFiles(_root, "*.motif.db", SearchOption.TopDirectoryOnly));
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ServerWaitsForTheExclusiveLeaseBeforeClaimingTheTransfer()
    {
        var workerRoot = Path.Combine(_root, "lease");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-lease-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("lease");
        var runtime = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, new[] { "baseline.v1" },
            cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        var bytes = BundleBytes();
        await WorkerWire.WriteAsync(pipe, Envelope("offer", WorkerCommands.BaselineOffer,
            new BaselineOfferRequest(project)), cancellation.Token);
        var offerEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellation.Token));
        var offer = JsonSerializer.Deserialize<BaselineOfferResponse>(
            offerEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!.Offer!;
        await UploadAsync(offer, bytes);
        await WorkerWire.WriteAsync(pipe, Completion(offer, bytes), cancellation.Token);
        var ready = Path.Combine(workerRoot, "transfers", offer.TransferId + ".ready");
        Task<byte[]> pending;
        using (await runtime.AcquireOperationAsync(cancellation.Token))
        {
            await WorkerWire.WriteAsync(pipe, Envelope("publish", WorkerCommands.BaselinePublish,
                new BaselinePublishRequest(project, offer.TransferId, Token(project, bytes))),
                cancellation.Token);
            pending = WorkerWire.ReadAsync(pipe, cancellation.Token);
            await Task.Delay(100, cancellation.Token);
            Assert.False(pending.IsCompleted);
            Assert.True(File.Exists(ready));
        }
        var envelope = WorkerWire.Deserialize<WorkerEnvelope>(await pending);
        var response = JsonSerializer.Deserialize<BaselinePublishResponse>(
            envelope.Payload.GetRawText(), WorkerJson.CreateOptions())!;
        Assert.Null(response.Failure);
        cancellation.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DurableRecordFailureRemovesOnlyTheNewUnreferencedPublication()
    {
        var workerRoot = Path.Combine(_root, "record-failure");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-record-failure-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var runtimes = ComposeRuntime(server, workerRoot);
        var ownership = WorkspaceOwnership.Bootstrap(workerRoot);
        var project = Project("record-failure");
        var runtime = runtimes.GetOrOpen(project);
        var target = new BaselineWorkspaceCatalog(ownership).For(runtime);
        var oldToken = new BaselineToken(project.FieldWorksProjectIdentity,
            "sha256:" + new string('1', 64), "projection-v1", "2026-08-23T00:00:00Z",
            "sha256:" + new string('a', 64));
        var oldRoot = Path.Combine(target.BaselineRoot, new string('a', 64));
        Directory.CreateDirectory(Path.Combine(oldRoot, "WritingSystemStore"));
        File.WriteAllText(Path.Combine(oldRoot, "project.fwdata"), "old");
        File.WriteAllText(Path.Combine(oldRoot, "WritingSystemStore", "en.ldml"), "old");
        runtime.Baselines.Record(runtime.WorkspaceKey,
            new BaselinePublication(oldRoot, Path.Combine(oldRoot, "project.fwdata"), oldToken),
            DateTimeOffset.Parse("2026-08-23T01:00:00Z"));
        var bytes = BundleBytes();
        var offer = server.BaselineTransfers.CreateOffer(
            "connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await server.BaselineTransfers.CompleteAsync(
            "connection", Completion(offer, bytes), CancellationToken.None);
        var handler = Handler(server, runtimes, workerRoot, "connection", record:
            (_, _, _, _) => throw new IOException("injected durable failure"));

        var response = ReadPublishResponse(await handler.HandleAsync(Payload(
            new BaselinePublishRequest(project, offer.TransferId, Token(project, bytes))),
            CancellationToken.None));

        Assert.Equal(BaselineFailureCode.PublicationFailed, response.Failure!.Code);
        Assert.Equal(oldToken, runtime.Baselines.GetCurrent(runtime.WorkspaceKey)!.Token);
        Assert.True(Directory.Exists(oldRoot));
        Assert.False(Directory.Exists(Path.Combine(target.BaselineRoot,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant())));
        Assert.Empty(Directory.GetFiles(Path.Combine(workerRoot, "transfers"), "*.ready"));
    }

    [Fact]
    public async Task DurableRecordFailurePreservesAPublicationThatWonTheMoveRace()
    {
        var workerRoot = Path.Combine(_root, "record-race");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-record-race-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("record-race");
        var runtime = runtimes.GetOrOpen(project);
        var destination = string.Empty;
        var receiver = new BaselineBundleReceiver(beforePublicationMove: path =>
        {
            destination = path;
            Directory.CreateDirectory(Path.Combine(path, "WritingSystemStore"));
            File.WriteAllText(Path.Combine(path, "external.fwdata"), "external");
            File.WriteAllText(Path.Combine(path, "WritingSystemStore", "en.ldml"), "external");
        });
        var bytes = BundleBytes();
        var offer = server.BaselineTransfers.CreateOffer(
            "connection", project, bytes.Length, TimeSpan.FromMinutes(1));
        await UploadAsync(offer, bytes);
        await server.BaselineTransfers.CompleteAsync(
            "connection", Completion(offer, bytes), CancellationToken.None);
        var handler = Handler(server, runtimes, workerRoot, "connection", receiver,
            (_, _, _, _) => throw new IOException("injected durable failure"));

        var response = ReadPublishResponse(await handler.HandleAsync(Payload(
            new BaselinePublishRequest(project, offer.TransferId, Token(project, bytes))),
            CancellationToken.None));

        Assert.Equal(BaselineFailureCode.PublicationFailed, response.Failure!.Code);
        Assert.Null(runtime.Baselines.GetCurrent(runtime.WorkspaceKey));
        Assert.Equal("external", File.ReadAllText(Path.Combine(destination, "external.fwdata")));
        Assert.Empty(Directory.GetFiles(Path.Combine(workerRoot, "transfers"), "*.ready"));
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
    public async Task BaselinePublishRequiresNegotiatedCapability()
    {
        var workerRoot = Path.Combine(_root, "publish-capability");
        await using var server = WorkerServer.CreateForTests(
            "worker-baseline-publish-capability-" + Guid.NewGuid().ToString("N"), false, workerRoot);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var runtimes = ComposeRuntime(server, workerRoot);
        var project = Project("publish-capability");
        _ = runtimes.GetOrOpen(project);
        var running = server.StartAsync(cancellation.Token);
        await using var pipe = await ConnectRawAsync(server.EndpointName, Array.Empty<string>(), cancellation.Token);
        _ = await WorkerWire.ReadAsync(pipe, cancellation.Token);
        var bytes = BundleBytes();
        await WorkerWire.WriteAsync(pipe, Envelope("publish", WorkerCommands.BaselinePublish,
            new BaselinePublishRequest(project, "transfer", Token(project, bytes))), cancellation.Token);

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

    [RequiresSymbolicLinkFact]
    public void StartupCleanupRefusesAReparseTransferRootWithoutTouchingTarget()
    {
        var outside = Path.Combine(_root, "outside-target");
        var transferRoot = Path.Combine(_root, "transfer-link");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.ready");
        File.WriteAllText(sentinel, "keep");
        Directory.CreateSymbolicLink(transferRoot, outside);

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

    private static JsonElement Payload<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value, WorkerJson.CreateOptions()));
        return document.RootElement.Clone();
    }

    private static BaselinePublishResponse ReadPublishResponse(JsonElement payload) =>
        JsonSerializer.Deserialize<BaselinePublishResponse>(
            payload.GetRawText(), WorkerJson.CreateOptions())!;

    private static async Task<BaselinePublishResponse> PublishThroughPipeAsync(
        Stream pipe, ProjectLocator project, byte[] bytes, BaselineToken token,
        CancellationToken cancellationToken)
    {
        await WorkerWire.WriteAsync(pipe, Envelope(Guid.NewGuid().ToString("N"),
            WorkerCommands.BaselineOffer, new BaselineOfferRequest(project)), cancellationToken);
        var offerEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellationToken));
        var offer = JsonSerializer.Deserialize<BaselineOfferResponse>(
            offerEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!.Offer!;
        await UploadAsync(offer, bytes);
        await WorkerWire.WriteAsync(pipe, Completion(offer, bytes), cancellationToken);
        await WorkerWire.WriteAsync(pipe, Envelope(Guid.NewGuid().ToString("N"),
            WorkerCommands.BaselinePublish,
            new BaselinePublishRequest(project, offer.TransferId, token)), cancellationToken);
        var publishEnvelope = WorkerWire.Deserialize<WorkerEnvelope>(
            await WorkerWire.ReadAsync(pipe, cancellationToken));
        return JsonSerializer.Deserialize<BaselinePublishResponse>(
            publishEnvelope.Payload.GetRawText(), WorkerJson.CreateOptions())!;
    }

    private static BaselineToken Token(ProjectLocator project, byte[] bytes) => new(
        project.FieldWorksProjectIdentity, "sha256:" + new string('1', 64), "projection-v1",
        "2026-08-23T00:00:00Z",
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

    private static BaselinePublishCommandHandler Handler(WorkerServer server,
        ProjectRuntimeRegistry runtimes, string workerRoot, string connectionId,
        BaselineBundleReceiver? receiver = null,
        Func<ProjectRuntime, string, BaselinePublication, DateTimeOffset, BaselineRecord>? record = null) =>
        new(runtimes, server.BaselineTransfers,
            new BaselineWorkspaceCatalog(WorkspaceOwnership.Bootstrap(workerRoot)),
            connectionId, receiver, record);

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

    private static byte[] BundleBytes()
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("project.fwdata").Open()))
                writer.Write("model");
            using (var writer = new StreamWriter(
                       archive.CreateEntry("WritingSystemStore/en.ldml").Open()))
                writer.Write("<ldml/>");
        }
        return bytes.ToArray();
    }

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
