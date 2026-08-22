using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        var executable = Path.Combine(root.Path, "worker.exe");
        File.WriteAllText(executable, "worker");
        var worker = new InstalledWorker(new Version(1, 2, 3), executable,
            new ProtocolRange(1, 2), new[] { "jobs.v1" });
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));

        catalog.Register(worker);
        catalog.Register(worker);
        Assert.Single(catalog.List());

        var changed = worker with { ExecutablePath = Path.Combine(root.Path, "other.exe") };
        File.WriteAllText(changed.ExecutablePath, "worker");
        Assert.Throws<InvalidOperationException>(() => catalog.Register(changed));
    }

    [Fact]
    public async Task ExistingCompatibleWorkerIsConnectedWithoutStartingProcess()
    {
        var connector = new FakeConnector(FakeConnector.Connected());
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter);

        await launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None);

        Assert.Equal(1, connector.Attempts);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task AbsentEndpointStartsNewestCandidateHiddenAndConfirmsConnection()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "worker.exe");
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(3, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        catalog.Register(candidate);
        var connector = new FakeConnector(FakeConnector.Unavailable(), FakeConnector.Connected());
        var starter = new FakeStarter();
        var launcher = NewLauncher(connector, starter, catalog);

        await launcher.EnsureConnectedAsync(Client(new ProtocolRange(1, 1)), CancellationToken.None);

        var process = Assert.Single(starter.Started);
        Assert.Equal(candidate, process.Worker);
        Assert.True(process.Hidden);
        Assert.Equal(2, connector.Attempts);
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
        var executable = Path.Combine(root.Path, "worker.exe");
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        catalog.Register(new InstalledWorker(new Version(1, 0), executable,
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

    private static WorkerLauncher NewLauncher(FakeConnector connector, FakeStarter starter,
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

    private sealed class FakeConnector : IWorkerConnector
    {
        private readonly Queue<Func<Task<IWorkerConnection>>> _responses;

        public FakeConnector(params Func<Task<IWorkerConnection>>[] responses)
        {
            _responses = new Queue<Func<Task<IWorkerConnection>>>(responses);
        }

        public int Attempts { get; private set; }

        public Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Attempts++;
            return (_responses.Count == 0 ? Unavailable() : _responses.Dequeue())();
        }

        public static Func<Task<IWorkerConnection>> Connected() => () =>
            Task.FromResult<IWorkerConnection>(new FakeConnection());

        public static Func<Task<IWorkerConnection>> Unavailable() => () =>
            Task.FromException<IWorkerConnection>(new WorkerEndpointUnavailableException("endpoint absent"));

        public static Func<Task<IWorkerConnection>> Incompatible() => () =>
            Task.FromException<IWorkerConnection>(new WorkerEndpointIncompatibleException("endpoint incompatible"));
    }

    private sealed class FakeConnection : IWorkerConnection
    {
        public WorkerHandshakeResult Negotiated { get; } =
            new WorkerHandshakeResult(1, Array.Empty<string>());

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
