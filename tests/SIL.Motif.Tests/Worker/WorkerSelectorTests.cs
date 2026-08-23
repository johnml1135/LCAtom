using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Client.Worker;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Launcher;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerSelectorTests
{
    [Fact]
    public void SelectsNewestProductVersionAfterCompatibilityFiltering()
    {
        var client = Client(new ProtocolRange(1, 2), "jobs.v1");
        var selected = WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("1.5.0", new ProtocolRange(1, 1), "jobs.v1"),
            Worker("2.0.0", new ProtocolRange(2, 4), "jobs.v1"),
            Worker("9.0.0", new ProtocolRange(7, 8), "jobs.v1")
        }, client);

        Assert.Equal(new Version(2, 0, 0), selected.ProductVersion);
    }

    [Fact]
    public void FiltersWorkersMissingRequiredCapabilities()
    {
        var client = Client(new ProtocolRange(1, 1), "jobs.v1", "baseline.v1");
        var selected = WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("5.0.0", new ProtocolRange(1, 1), "jobs.v1"),
            Worker("4.0.0", new ProtocolRange(1, 1), "jobs.v1", "baseline.v1")
        }, client);

        Assert.Equal(new Version(4, 0, 0), selected.ProductVersion);
    }

    [Fact]
    public void ThrowsWhenNoProtocolVersionOverlaps()
    {
        var client = Client(new ProtocolRange(1, 1));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkerSelector.SelectNewestCompatible(new[]
            {
                Worker("1.0.0", new ProtocolRange(2, 3))
            }, client));

        Assert.Contains("protocol", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductVersionAloneNeverRejectsCompatibleWorker()
    {
        var selected = WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("0.0.1", new ProtocolRange(1, 1))
        }, Client(new ProtocolRange(1, 1)));

        Assert.Equal(new Version(0, 0, 1), selected.ProductVersion);
    }

    [Fact]
    public void CatalogTreatsIdenticalRegistrationAsIdempotentAndRejectsMutation()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.2.3", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var worker = new InstalledWorker(new Version(1, 2, 3), executable,
            new ProtocolRange(1, 2), new[] { "jobs.v1" });
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));

        Register(catalog, worker);
        Register(catalog, worker);
        Assert.Single(catalog.List());

        var changed = worker with { ExecutablePath = Path.Combine(root.Path, "catalog", "1.2.3", "other.exe") };
        File.WriteAllText(changed.ExecutablePath, "worker");
        Assert.Throws<InvalidOperationException>(() => Register(catalog, changed));
    }

    [Fact]
    public void CatalogIgnoresVersionDirectoryUntilManifestIsPublished()
    {
        using var root = TemporaryDirectory.Create();
        var versionDirectory = Path.Combine(root.Path, "catalog", "2.0.0");
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(Path.Combine(versionDirectory, "manifest.json.tmp"), "partial");

        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));

        Assert.Empty(catalog.List());
    }

    [Fact]
    public void CatalogRejectsManifestMetadataMutationThroughDigest()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.2.3", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, new InstalledWorker(new Version(1, 2, 3), executable,
            new ProtocolRange(1, 2), new[] { "jobs.v1" }));
        var manifest = Path.Combine(root.Path, "catalog", "1.2.3", "manifest.json");
        var changed = File.ReadAllText(manifest).Replace("\"minimum\":1", "\"minimum\":2",
            StringComparison.Ordinal);
        File.WriteAllText(manifest, changed);

        Assert.Throws<InvalidDataException>(() => catalog.List());
    }

    [Fact]
    public void CatalogRejectsExecutableReparseAttributeThroughValidationSeam()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.2.3", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"), path =>
            string.Equals(path, executable, StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.ReparsePoint
                : FileAttributes.Normal);

        Assert.Throws<ArgumentException>(() => Register(catalog, new InstalledWorker(new Version(1, 2, 3),
            executable, new ProtocolRange(1, 2), Array.Empty<string>())));
    }

    [Fact]
    public void CatalogRejectsCorruptOversizedAndMismatchedFinalManifests()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.2.3", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, new InstalledWorker(new Version(1, 2, 3), executable,
            new ProtocolRange(1, 2), Array.Empty<string>()));
        var manifest = Path.Combine(root.Path, "catalog", "1.2.3", "manifest.json");
        var valid = File.ReadAllText(manifest);
        File.WriteAllText(manifest, new string('x', 65 * 1024));
        Assert.Throws<InvalidDataException>(() => catalog.List());

        File.WriteAllText(manifest, "{not-json");
        Assert.Throws<InvalidDataException>(() => catalog.List());

        File.WriteAllText(manifest, "{}");
        Assert.Throws<InvalidDataException>(() => catalog.List());

        var mismatchedDirectory = Path.Combine(root.Path, "catalog", "9.9.9");
        Directory.CreateDirectory(mismatchedDirectory);
        File.WriteAllText(manifest, valid);
        File.Copy(manifest, Path.Combine(mismatchedDirectory, "manifest.json"), true);
        Assert.Throws<InvalidDataException>(() => catalog.List());
    }

    [Fact]
    public void CatalogRejectsExecutableMutationAndOutsideRegistration()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.2.3", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, new InstalledWorker(new Version(1, 2, 3), executable,
            new ProtocolRange(1, 2), Array.Empty<string>()));
        File.AppendAllText(executable, "changed");
        Assert.Throws<InvalidDataException>(() => catalog.List());

        var outside = Path.Combine(root.Path, "outside.exe");
        File.WriteAllText(outside, "outside");
        Assert.Throws<ArgumentException>(() => Register(catalog, new InstalledWorker(new Version(2, 0),
            outside, new ProtocolRange(1, 2), Array.Empty<string>())));
    }

    [Fact]
    public void SelectorRejectsNullAndAmbiguousRegistrations()
    {
        Assert.Throws<ArgumentNullException>(() => WorkerSelector.SelectNewestCompatible(null!, Client(
            new ProtocolRange(1, 1))));
        Assert.Throws<ArgumentNullException>(() => WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("1.0.0", new ProtocolRange(1, 1))
        }, null!));
        Assert.Throws<InvalidOperationException>(() => WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("1.0.0", new ProtocolRange(1, 1)),
            Worker("1.0.0", new ProtocolRange(1, 1), "other")
        }, Client(new ProtocolRange(1, 1))));
        Assert.Throws<InvalidOperationException>(() => WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("1.0.0", new ProtocolRange(1, 1), "jobs.v1", "jobs.v1")
        }, Client(new ProtocolRange(1, 1))));
        Assert.Throws<InvalidOperationException>(() => WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("1.0.0", new ProtocolRange(1, 1), new string('x', 129))
        }, Client(new ProtocolRange(1, 1))));
        Assert.Throws<InvalidOperationException>(() => WorkerSelector.SelectNewestCompatible(new[]
        {
            new InstalledWorker(new Version(1, 0), "relative.exe", new ProtocolRange(1, 1),
                Array.Empty<string>())
        }, Client(new ProtocolRange(1, 1))));
        Assert.Throws<InvalidOperationException>(() => WorkerSelector.SelectNewestCompatible(new[]
        {
            new InstalledWorker(new Version(1, 0), "C:\\Motif\\worker.exe", null!, Array.Empty<string>())
        }, Client(new ProtocolRange(1, 1))));
    }

    [Fact]
    public async Task ExistingCompatibleWorkerIsConnectedWithoutStartingProcess()
    {
        var connector = new FakeConnector(FakeConnector.Connected(
            new WorkerHandshakeOffer("running", new ProtocolRange(1, 1), Array.Empty<string>(), "running")));
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter);
        var request = Client(new ProtocolRange(1, 1));

        await launcher.EnsureConnectedAsync(request, CancellationToken.None);

        Assert.Equal(1, connector.Attempts);
        Assert.Equal("motif-test-endpoint", connector.LastEndpoint);
        Assert.Same(request, connector.LastRequest);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task ExistingWorkerWithoutOfferIsRejectedWithoutStartingProcess()
    {
        var connector = new FakeConnector(FakeConnector.MissingOffer());
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter);

        await Assert.ThrowsAsync<WorkerEndpointIncompatibleException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None));

        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task AbsentEndpointStartsNewestCandidateHiddenAndConfirmsConnection()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(3, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        var connector = new FakeConnector(FakeConnector.Unavailable(), FakeConnector.Connected(
            new WorkerHandshakeOffer("3.0", new ProtocolRange(1, 1), Array.Empty<string>(), "id")));
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter, catalog);

        await launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None);

        var process = Assert.Single(starter.Started);
        Assert.Equal(candidate, process.Worker);
        Assert.True(process.Hidden);
        Assert.Equal(2, connector.Attempts);
        Assert.False(process.Terminated);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task StartedCandidateConnectionRequiresAFullOffer()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(3, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        var connector = new FakeConnector(FakeConnector.Unavailable(), FakeConnector.MissingOffer());
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter, catalog);

        await Assert.ThrowsAsync<WorkerLaunchException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None));

        var process = Assert.Single(starter.Started);
        Assert.True(process.Terminated);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task StartedCandidateConnectionRequiresMatchingOffer()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(3, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        var mismatched = new WorkerHandshakeOffer("4.0", new ProtocolRange(1, 1), Array.Empty<string>(), "id");
        var connector = new FakeConnector(FakeConnector.Unavailable(), FakeConnector.Connected(mismatched));
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter, catalog);

        await Assert.ThrowsAsync<WorkerLaunchException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None));

        var process = Assert.Single(starter.Started);
        Assert.True(process.Terminated);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task StartedCandidateConnectionSucceedsWithMatchingOffer()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(3, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        var offer = new WorkerHandshakeOffer("3.0", new ProtocolRange(1, 1), Array.Empty<string>(), "id");
        var connector = new FakeConnector(FakeConnector.Unavailable(), FakeConnector.Connected(offer));
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter, catalog);

        await launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None);

        var process = Assert.Single(starter.Started);
        Assert.False(process.Terminated);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CancelledCandidateStartupTerminatesAndDisposesProcess()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(3, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        var connector = new FakeConnector(FakeConnector.Unavailable(), FakeConnector.WaitForCancellation());
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter, catalog);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), cancellation.Token));

        var process = Assert.Single(starter.Started);
        Assert.True(process.Terminated);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task TimedOutCandidateStartupTerminatesAndDisposesProcess()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(3, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        var starter = new FakeStarter();
        var launcher = NewLauncher(new FakeConnector(FakeConnector.Unavailable()), starter, catalog);

        await Assert.ThrowsAsync<WorkerLaunchException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None));

        var process = Assert.Single(starter.Started);
        Assert.True(process.Terminated);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task CandidateConnectionFailureTerminatesAndDisposesProcess()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(3, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        var connector = new FakeConnector(FakeConnector.Unavailable(), FakeConnector.ConnectedFailure());
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter, catalog);

        await Assert.ThrowsAsync<WorkerEndpointIncompatibleException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None));

        var process = Assert.Single(starter.Started);
        Assert.True(process.Terminated);
        Assert.True(process.Disposed);
    }

    [Fact]
    public async Task RejectedCandidateExitsBeforeLauncherFailureReturns()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "3.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(3, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        var state = new CandidateEndpointState();
        var connector = new CandidateEndpointConnector(state);
        var starter = new BlockingTerminationStarter(state);
        var launcher = NewLauncher(connector, starter, catalog);

        var firstAttempt = Task.Run(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None));
        var process = await starter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await process.TerminationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(firstAttempt.IsCompleted);
        process.AllowTermination();
        await Assert.ThrowsAsync<WorkerLaunchException>(() => firstAttempt);

        var laterStarter = new FakeStarter();
        await NewLauncher(connector, laterStarter, catalog)
            .EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None);
        Assert.Equal(0, connector.RejectedCandidateReconnects);
        Assert.Empty(laterStarter.Started);
    }

    [Fact]
    public async Task IncompatibleExistingEndpointFailsWithoutStartingAnotherWorker()
    {
        var connector = new FakeConnector(FakeConnector.Incompatible());
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter);

        var exception = await Assert.ThrowsAsync<WorkerEndpointIncompatibleException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None));

        Assert.Contains("incompatible", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task ConnectedEndpointFailureIsNotTreatedAsAbsent()
    {
        var connector = new FakeConnector(FakeConnector.ConnectedFailure());
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter);

        await Assert.ThrowsAsync<WorkerEndpointIncompatibleException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None));

        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task NoCompatibleInstallProvidesInstallOrUpdateGuidance()
    {
        using var root = TemporaryDirectory.Create();
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        var launcher = NewLauncher(new FakeConnector(FakeConnector.Unavailable()), new FakeStarter(), catalog);

        var exception = await Assert.ThrowsAsync<WorkerLaunchException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(9, 9)), CancellationToken.None));

        Assert.Contains("install", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartupFailureReportsActionableErrorWithoutRetryStorm()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, new InstalledWorker(new Version(1, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>()));
        var connector = new FakeConnector(FakeConnector.Unavailable());
        var starter = new FakeStarter(exited: true, exitCode: 17);
        var launcher = NewLauncher(connector, starter, catalog);

        var exception = await Assert.ThrowsAsync<WorkerLaunchException>(() =>
            launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None));

        Assert.Contains("start", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("install", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(connector.Attempts, 1, 2);
    }

    private static WorkerLauncher NewLauncher(IWorkerConnector connector, IWorkerProcessStarter starter,
        InstalledWorkerCatalog? catalog = null)
    {
        return new WorkerLauncher(catalog ?? new InstalledWorkerCatalog(Path.Combine(Path.GetTempPath(),
            "motif-launcher-tests", Guid.NewGuid().ToString("N"))), connector, starter,
            "motif-test-endpoint", TimeSpan.FromMilliseconds(250));
    }

    private static WorkerHandshakeRequest Client(ProtocolRange protocols, params string[] capabilities) =>
        new WorkerHandshakeRequest("test-client", "0.0.1", protocols, capabilities);

    private static InstalledWorker Worker(string version, ProtocolRange protocols, params string[] capabilities) =>
        new InstalledWorker(Version.Parse(version), "C:\\Motif\\worker.exe", protocols, capabilities);

    private static InstalledWorker Register(InstalledWorkerCatalog catalog, InstalledWorker worker)
    {
        var directory = System.IO.Path.GetDirectoryName(worker.ExecutablePath)!;
        Directory.CreateDirectory(directory);
        var metadata = new WorkerBuildMetadata(worker.ProductVersion.ToString(), worker.Protocols,
            worker.Capabilities);
        File.WriteAllText(Path.Combine(directory, WorkerCommands.BuildMetadataFileName),
            metadata.ToCanonicalJson());
        return catalog.Register(worker);
    }

    private sealed class FakeConnector : IWorkerConnector
    {
        private readonly Queue<Func<CancellationToken, Task<IWorkerConnection>>> _responses;

        public FakeConnector(params Func<CancellationToken, Task<IWorkerConnection>>[] responses)
        {
            _responses = new Queue<Func<CancellationToken, Task<IWorkerConnection>>>(responses);
        }

        public int Attempts { get; private set; }
        public string? LastEndpoint { get; private set; }
        public WorkerHandshakeRequest? LastRequest { get; private set; }

        public Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Attempts++;
            LastEndpoint = endpointName;
            LastRequest = request;
            return (_responses.Count == 0 ? Unavailable() : _responses.Dequeue())(cancellationToken);
        }

        public static Func<CancellationToken, Task<IWorkerConnection>> Connected(WorkerHandshakeOffer offer) => _ =>
            Task.FromResult<IWorkerConnection>(new FakeConnection(offer));

        public static Func<CancellationToken, Task<IWorkerConnection>> MissingOffer() => _ =>
            Task.FromResult<IWorkerConnection>(new FakeConnection(null!));

        public static Func<CancellationToken, Task<IWorkerConnection>> WaitForCancellation() => async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        };

        public static Func<CancellationToken, Task<IWorkerConnection>> Unavailable() => _ =>
            Task.FromException<IWorkerConnection>(new WorkerEndpointUnavailableException("endpoint absent"));

        public static Func<CancellationToken, Task<IWorkerConnection>> Incompatible() => _ =>
            Task.FromException<IWorkerConnection>(new WorkerEndpointIncompatibleException("endpoint incompatible"));

        public static Func<CancellationToken, Task<IWorkerConnection>> ConnectedFailure() => _ =>
            Task.FromException<IWorkerConnection>(new WorkerConnectionFailureException(
                WorkerConnectionFailureStage.AfterPeerConnection, "connected endpoint failed"));
    }

    private sealed class FakeConnection : IWorkerConnection
    {
        public FakeConnection(WorkerHandshakeOffer? offer)
        {
            Offer = offer!;
        }

        public WorkerHandshakeResult Negotiated { get; } =
            new WorkerHandshakeResult(1, Array.Empty<string>());

        public WorkerHandshakeOffer Offer { get; }

        public void Dispose() { }
    }

    private sealed class FakeStarter : IWorkerProcessStarter
    {
        private readonly bool _exited;
        private readonly int _exitCode;

        public FakeStarter(bool exited = false, int exitCode = 0)
        {
            _exited = exited;
            _exitCode = exitCode;
        }

        public List<FakeProcess> Started { get; } = new List<FakeProcess>();

        public IWorkerProcess Start(InstalledWorker worker)
        {
            var process = new FakeProcess(worker, _exited, _exitCode);
            Started.Add(process);
            return process;
        }
    }

    private sealed class CandidateEndpointState
    {
        public bool Started { get; set; }
        public bool Exited { get; set; }
    }

    private sealed class CandidateEndpointConnector : IWorkerConnector
    {
        private readonly CandidateEndpointState _state;
        private bool _rejectedOfferReturned;

        public CandidateEndpointConnector(CandidateEndpointState state) => _state = state;

        public int RejectedCandidateReconnects { get; private set; }

        public Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (!_state.Started)
                return Task.FromException<IWorkerConnection>(
                    new WorkerEndpointUnavailableException("endpoint absent"));
            if (!_state.Exited)
            {
                if (_rejectedOfferReturned)
                    RejectedCandidateReconnects++;
                _rejectedOfferReturned = true;
                return Task.FromResult<IWorkerConnection>(new FakeConnection(
                    new WorkerHandshakeOffer("4.0", new ProtocolRange(1, 1), Array.Empty<string>(), "rejected")));
            }
            return Task.FromResult<IWorkerConnection>(new FakeConnection(
                new WorkerHandshakeOffer("running", new ProtocolRange(1, 1), Array.Empty<string>(), "stable")));
        }
    }

    private sealed class BlockingTerminationStarter : IWorkerProcessStarter
    {
        private readonly CandidateEndpointState _state;

        public BlockingTerminationStarter(CandidateEndpointState state) => _state = state;

        public TaskCompletionSource<BlockingTerminationProcess> Started { get; } =
            new TaskCompletionSource<BlockingTerminationProcess>(TaskCreationOptions.RunContinuationsAsynchronously);

        public IWorkerProcess Start(InstalledWorker worker)
        {
            _state.Started = true;
            var process = new BlockingTerminationProcess(_state);
            Started.TrySetResult(process);
            return process;
        }
    }

    private sealed class BlockingTerminationProcess : IWorkerProcess
    {
        private readonly CandidateEndpointState _state;
        private readonly ManualResetEventSlim _allowTermination = new ManualResetEventSlim();

        public BlockingTerminationProcess(CandidateEndpointState state) => _state = state;

        public TaskCompletionSource<bool> TerminationStarted { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasExited => _state.Exited;
        public int ExitCode => 0;
        public ProcessStartInfo StartInfo { get; } = new ProcessStartInfo();

        public void AllowTermination() => _allowTermination.Set();

        public void Terminate()
        {
            TerminationStarted.TrySetResult(true);
            if (!_allowTermination.Wait(TimeSpan.FromSeconds(5)))
                throw new WorkerLaunchException("The fake worker did not receive its termination release.");
            _state.Exited = true;
        }

        public void Dispose() => _allowTermination.Dispose();
    }

    private sealed class FakeProcess : IWorkerProcess
    {
        public FakeProcess(InstalledWorker worker, bool hidden, int exitCode)
        {
            Worker = worker;
            Hidden = true;
            HasExited = hidden;
            ExitCode = exitCode;
        }

        public InstalledWorker Worker { get; }
        public bool Hidden { get; }
        public bool HasExited { get; }
        public int ExitCode { get; }
        public ProcessStartInfo StartInfo { get; } = new ProcessStartInfo();
        public bool Terminated { get; private set; }
        public bool Disposed { get; private set; }
        public void Terminate() => Terminated = true;
        public void Dispose() => Disposed = true;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) { Path = path; Directory.CreateDirectory(path); }
        public string Path { get; }
        public static TemporaryDirectory Create() => new TemporaryDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "motif-launcher-tests", Guid.NewGuid().ToString("N")));
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
