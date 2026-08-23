using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Worker;
using SIL.Motif.Launcher;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerLauncherReviewTests
{
    [Fact]
    public void RealProcessStarterUsesHiddenShellFreeConfiguration()
    {
        var executable = Environment.ProcessPath!;
        ProcessStartInfo? captured = null;
        var process = new WorkerProcessStarter(info =>
        {
            captured = info;
            return Process.GetCurrentProcess();
        }).Start(new InstalledWorker(
            new Version(1, 0), executable, new ProtocolRange(1, 1), Array.Empty<string>()));

        Assert.NotNull(captured);
        Assert.Equal(executable, captured!.FileName);
        Assert.Equal(string.Empty, captured.Arguments);
        Assert.Empty(captured.ArgumentList);
        Assert.False(process.StartInfo.UseShellExecute);
        Assert.True(process.StartInfo.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, process.StartInfo.WindowStyle);
    }

    [Fact]
    public async Task SeparateCatalogInstancesRefuseRacingMetadataChanges()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var first = new InstalledWorker(new Version(1, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var second = first with { Protocols = new ProtocolRange(1, 2) };
        WriteSidecar(first);
        var catalogs = new[] { new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog")),
            new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog")) };

        var results = await Task.WhenAll(
            Task.Run(() => TryRegister(catalogs[0], first)),
            Task.Run(() => TryRegister(catalogs[1], second)));

        Assert.Single(results.Where(result => result is null));
        Assert.Single(results.Where(result => result is InvalidDataException));
        Assert.Single(catalogs[0].List());
        Assert.Empty(Directory.GetFiles(catalogs[0].Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LauncherUsesOneOverallDeadlineForAllProbes()
    {
        using var root = TemporaryDirectory.Create();
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        var connector = new UnavailableConnector();
        var clock = new AdvancingClock();
        var launcher = new WorkerLauncher(catalog, connector, new NoopStarter(), "endpoint", TimeSpan.FromMilliseconds(5),
            clock, new NoopDelay());

        await Assert.ThrowsAsync<WorkerLaunchException>(() => launcher.EnsureConnectedAsync(Client()));

        Assert.InRange(connector.Attempts, 1, 8);
    }

    [Fact]
    public async Task LauncherDoesNotStartWhenValidationReachesExactDeadline()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, new InstalledWorker(new Version(1, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>()));
        var starter = new CountingStarter();
        var launcher = new WorkerLauncher(catalog, new UnavailableConnector(), starter, "endpoint",
            TimeSpan.FromSeconds(10), new ExactDeadlineClock(), new NoopDelay());

        await Assert.ThrowsAsync<WorkerLaunchException>(() => launcher.EnsureConnectedAsync(Client()));

        Assert.Equal(0, starter.Starts);
    }

    [Fact]
    public async Task LauncherDoesNotStartWhenInstalledSidecarWasDeleted()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(1, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>());
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        File.Delete(Path.Combine(Path.GetDirectoryName(executable)!, WorkerCommands.BuildMetadataFileName));
        var starter = new CountingStarter();
        var launcher = NewLauncher(catalog, new UnavailableConnector(), starter);

        await Assert.ThrowsAsync<WorkerCatalogException>(() =>
            launcher.EnsureConnectedAsync(Client(), CancellationToken.None));

        Assert.Equal(0, starter.Starts);
    }

    [Fact]
    public async Task ProgramReturnsDistinctCodesForLauncherOutcomes()
    {
        using var root = TemporaryDirectory.Create();
        var emptyCatalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "empty"));
        Assert.Equal(0, await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(emptyCatalog, new ConnectedConnector(), new NoopStarter())));
        Assert.Equal(2, await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(emptyCatalog, new UnavailableConnector(), new NoopStarter())));
        Assert.Equal(3, await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(emptyCatalog, new IncompatibleConnector(), new NoopStarter())));

        var executable = Path.Combine(root.Path, "startup", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var startupCatalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "startup"));
        Register(startupCatalog, new InstalledWorker(new Version(1, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>()));
        Assert.Equal(4, await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(startupCatalog, new UnavailableConnector(), new ExitedStarter())));

        var corruptRoot = Path.Combine(root.Path, "corrupt", "1.0");
        Directory.CreateDirectory(corruptRoot);
        File.WriteAllText(Path.Combine(corruptRoot, "manifest.json"), "{}");
        Assert.Equal(5, await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(new InstalledWorkerCatalog(Path.Combine(root.Path, "corrupt")),
                new UnavailableConnector(), new NoopStarter())));
    }

    [Fact]
    public async Task ProgramWritesBoundedActionableMessageForNoCompatibleInstall()
    {
        using var root = TemporaryDirectory.Create();
        var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var code = await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(new InstalledWorkerCatalog(Path.Combine(root.Path, "empty")),
                new UnavailableConnector(), new NoopStarter()), TextWriter.Null, error);

        Assert.Equal(2, code);
        Assert.Contains("install", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("update", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.InRange(error.ToString().Length, 1, 512);
    }

    [Fact]
    public async Task ConcurrentLaunchersConvergeOnTheStableEndpoint()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, new InstalledWorker(new Version(1, 0), executable,
            new ProtocolRange(1, 1), Array.Empty<string>()));
        var connector = new ConvergingConnector();
        var starter = new CountingStarter();
        var first = NewLauncher(catalog, connector, starter);
        var second = NewLauncher(catalog, connector, starter);

        await Task.WhenAll(first.EnsureConnectedAsync(Client()), second.EnsureConnectedAsync(Client()));

        Assert.Equal(2, starter.Starts);
        Assert.Equal(4, connector.Attempts);
    }

    [Fact]
    public async Task ProgramReportsBoundedInvalidRequest()
    {
        var code = await SIL.Motif.Launcher.Program.RunAsync(new[] { "--unknown" }, null!);

        Assert.Equal(4, code);
    }

    private static Exception? TryRegister(InstalledWorkerCatalog catalog, InstalledWorker worker)
    {
        try
        {
            catalog.Register(worker);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static InstalledWorker Register(InstalledWorkerCatalog catalog, InstalledWorker worker)
    {
        WriteSidecar(worker);
        return catalog.Register(worker);
    }

    private static void WriteSidecar(InstalledWorker worker)
    {
        var directory = Path.GetDirectoryName(worker.ExecutablePath)!;
        Directory.CreateDirectory(directory);
        var metadata = new WorkerBuildMetadata(worker.ProductVersion.ToString(), worker.Protocols,
            worker.Capabilities);
        File.WriteAllText(Path.Combine(directory, WorkerCommands.BuildMetadataFileName),
            metadata.ToCanonicalJson());
    }

    private static WorkerHandshakeRequest Client() => new WorkerHandshakeRequest(
        "review", "1.0", new ProtocolRange(1, 1), Array.Empty<string>());

    private static WorkerLauncher NewLauncher(InstalledWorkerCatalog catalog, IWorkerConnector connector,
        IWorkerProcessStarter starter) => new WorkerLauncher(catalog, connector, starter, "endpoint",
        TimeSpan.FromSeconds(1));

    private sealed class UnavailableConnector : IWorkerConnector
    {
        public int Attempts { get; private set; }

        public Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromException<IWorkerConnection>(new WorkerEndpointUnavailableException("absent"));
        }
    }

    private sealed class ConnectedConnector : IWorkerConnector
    {
        public Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult<IWorkerConnection>(new ConnectedConnection(
                new WorkerHandshakeOffer("running", new ProtocolRange(1, 1), Array.Empty<string>(), "running")));
    }

    private sealed class IncompatibleConnector : IWorkerConnector
    {
        public Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
            TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromException<IWorkerConnection>(new WorkerEndpointIncompatibleException("incompatible"));
    }

    private sealed class NoopStarter : IWorkerProcessStarter
    {
        public IWorkerProcess Start(InstalledWorker worker) => new NoopProcess();
    }

    private sealed class ExitedStarter : IWorkerProcessStarter
    {
        public IWorkerProcess Start(InstalledWorker worker) => new ExitedProcess();
    }

    private sealed class ExitedProcess : IWorkerProcess
    {
        public bool HasExited => true;
        public int ExitCode => 17;
        public ProcessStartInfo StartInfo { get; } = new ProcessStartInfo();
    }

    private sealed class CountingStarter : IWorkerProcessStarter
    {
        private int _starts;
        public int Starts => _starts;
        public IWorkerProcess Start(InstalledWorker worker)
        {
            Interlocked.Increment(ref _starts);
            return new NoopProcess();
        }
    }

    private sealed class ConvergingConnector : IWorkerConnector
    {
        private int _attempts;
        private int _initialProbes;
        private readonly TaskCompletionSource<bool> _bothInitialProbes =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts => _attempts;
        public async Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= 2)
            {
                if (Interlocked.Increment(ref _initialProbes) == 2)
                    _bothInitialProbes.TrySetResult(true);
                await _bothInitialProbes.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                throw new WorkerEndpointUnavailableException("absent");
            }
            return new ConnectedConnection(
                new WorkerHandshakeOffer("1.0", new ProtocolRange(1, 1), Array.Empty<string>(), "running"));
        }
    }

    private sealed class ConnectedConnection : IWorkerConnection
    {
        public ConnectedConnection(WorkerHandshakeOffer offer) { Offer = offer; }

        public WorkerHandshakeResult Negotiated { get; } = new WorkerHandshakeResult(1, Array.Empty<string>());
        public WorkerHandshakeOffer Offer { get; }
        public void Dispose() { }
    }

    private sealed class NoopProcess : IWorkerProcess
    {
        public bool HasExited => false;
        public int ExitCode => 0;
        public ProcessStartInfo StartInfo { get; } = new ProcessStartInfo();
    }

    private sealed class ExactDeadlineClock : ILauncherClock
    {
        private readonly Queue<long> _timestamps = new Queue<long>(new[] { 0L, 1L, 1L, 1L, 10L });

        public long Timestamp => _timestamps.Count == 0 ? 10 : _timestamps.Dequeue();
        public long Frequency => 1;
    }

    private sealed class AdvancingClock : ILauncherClock
    {
        private long _timestamp;
        public long Timestamp => ++_timestamp;
        public long Frequency => 1000;
    }

    private sealed class NoopDelay : ILauncherDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) { Path = path; Directory.CreateDirectory(path); }
        public string Path { get; }
        public static TemporaryDirectory Create() => new TemporaryDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "motif-launcher-review", Guid.NewGuid().ToString("N")));
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
