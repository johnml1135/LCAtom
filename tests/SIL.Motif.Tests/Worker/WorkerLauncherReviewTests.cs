using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        var process = new WorkerProcessStarter(_ => Process.GetCurrentProcess()).Start(new InstalledWorker(
            new Version(1, 0), executable, new ProtocolRange(1, 1), Array.Empty<string>()));

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
        var catalogs = new[] { new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog")),
            new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog")) };

        var results = await Task.WhenAll(
            Task.Run(() => TryRegister(catalogs[0], first)),
            Task.Run(() => TryRegister(catalogs[1], second)));

        Assert.Single(results.Where(result => result is null));
        Assert.Single(results.Where(result => result is InvalidOperationException));
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
    public async Task ConcurrentLaunchersConvergeOnTheStableEndpoint()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        catalog.Register(new InstalledWorker(new Version(1, 0), executable,
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

    private sealed class NoopStarter : IWorkerProcessStarter
    {
        public IWorkerProcess Start(InstalledWorker worker) => new NoopProcess();
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
        public int Attempts => _attempts;
        public Task<IWorkerConnection> ConnectAsync(string endpointName, WorkerHandshakeRequest request,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            return attempt <= 2
                ? Task.FromException<IWorkerConnection>(new WorkerEndpointUnavailableException("absent"))
                : Task.FromResult<IWorkerConnection>(new ConnectedConnection());
        }
    }

    private sealed class ConnectedConnection : IWorkerConnection
    {
        public WorkerHandshakeResult Negotiated { get; } = new WorkerHandshakeResult(1, Array.Empty<string>());
        public void Dispose() { }
    }

    private sealed class NoopProcess : IWorkerProcess
    {
        public bool HasExited => false;
        public int ExitCode => 0;
        public ProcessStartInfo StartInfo { get; } = new ProcessStartInfo();
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
