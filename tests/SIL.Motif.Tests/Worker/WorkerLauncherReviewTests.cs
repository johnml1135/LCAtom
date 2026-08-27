using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Runner;
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
            new Version(1, 0), executable, 7));

        Assert.NotNull(captured);
        Assert.Equal(executable, captured!.FileName);
        Assert.Equal(string.Empty, captured.Arguments);
        Assert.Empty(captured.ArgumentList);
        Assert.False(process.StartInfo.UseShellExecute);
        Assert.True(process.StartInfo.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, process.StartInfo.WindowStyle);
    }

    [Fact]
    public async Task SeparateCatalogInstancesRejectMetadataMismatch()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var first = new InstalledWorker(new Version(1, 0), executable, 7);
        var second = first with { SupportedSchema = 8 };
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
        var presence = new AbsentPresence();
        var clock = new AdvancingClock();
        var launcher = new WorkerLauncher(catalog, presence, new NoopStarter(), @"Local\motif-review-runner", TimeSpan.FromMilliseconds(5),
            clock, new NoopDelay());

        await Assert.ThrowsAsync<WorkerLaunchException>(() => launcher.EnsureRunningAsync(7));

        Assert.InRange(presence.Attempts, 1, 8);
    }

    [Fact]
    public async Task LauncherDoesNotStartWhenValidationReachesExactDeadline()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, new InstalledWorker(new Version(1, 0), executable, 7));
        var starter = new CountingStarter();
        var launcher = new WorkerLauncher(catalog, new AbsentPresence(), starter, "endpoint",
            TimeSpan.FromSeconds(10), new ExactDeadlineClock(), new NoopDelay());

        await Assert.ThrowsAsync<WorkerLaunchException>(() => launcher.EnsureRunningAsync(7));

        Assert.Equal(0, starter.Starts);
    }

    [Fact]
    public async Task LauncherDoesNotStartWhenInstalledSidecarWasDeleted()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var candidate = new InstalledWorker(new Version(1, 0), executable, 7);
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, candidate);
        File.Delete(Path.Combine(Path.GetDirectoryName(executable)!, InstalledWorkerCatalog.RunnerMetadataFileName));
        var starter = new CountingStarter();
        var launcher = NewLauncher(catalog, new AbsentPresence(), starter);

        await Assert.ThrowsAsync<WorkerCatalogException>(() =>
            launcher.EnsureRunningAsync(7, CancellationToken.None));

        Assert.Equal(0, starter.Starts);
    }

    [Fact]
    public async Task ProgramReturnsDistinctCodesForLauncherOutcomes()
    {
        using var root = TemporaryDirectory.Create();
        var emptyCatalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "empty"));
        Assert.Equal(0, await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(emptyCatalog, new RunningPresence(), new NoopStarter())));
        Assert.Equal(2, await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(emptyCatalog, new AbsentPresence(), new NoopStarter())));

        var executable = Path.Combine(root.Path, "startup", "1.0", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var startupCatalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "startup"));
        Register(startupCatalog, new InstalledWorker(new Version(1, 0), executable, 7));
        Assert.Equal(4, await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(startupCatalog, new AbsentPresence(), new ExitedStarter())));

        var corruptRoot = Path.Combine(root.Path, "corrupt", "1.0");
        Directory.CreateDirectory(corruptRoot);
        File.WriteAllText(Path.Combine(corruptRoot, "manifest.json"), "{}");
        Assert.Equal(5, await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(new InstalledWorkerCatalog(Path.Combine(root.Path, "corrupt")),
                new AbsentPresence(), new NoopStarter())));
    }

    [Fact]
    public async Task ProgramWritesBoundedActionableMessageForNoCompatibleInstall()
    {
        using var root = TemporaryDirectory.Create();
        var error = new StringWriter(new StringBuilder(), System.Globalization.CultureInfo.InvariantCulture);
        var code = await SIL.Motif.Launcher.Program.RunAsync(Array.Empty<string>(),
            NewLauncher(new InstalledWorkerCatalog(Path.Combine(root.Path, "empty")),
                new AbsentPresence(), new NoopStarter()), TextWriter.Null, error);

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
        Register(catalog, new InstalledWorker(new Version(1, 0), executable, 7));
        var presence = new ConvergingPresence();
        var starter = new CountingStarter();
        var first = NewLauncher(catalog, presence, starter);
        var second = NewLauncher(catalog, presence, starter);

        // The presence probe is synchronous, so each call must own a thread or the second never starts.
        await Task.WhenAll(Task.Run(() => first.EnsureRunningAsync(7)),
            Task.Run(() => second.EnsureRunningAsync(7)));

        Assert.Equal(2, starter.Starts);
        Assert.Equal(4, presence.Attempts);
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
        var metadata = new RunnerBuildMetadata(worker.ProductVersion.ToString(), worker.SupportedSchema);
        File.WriteAllText(Path.Combine(directory, InstalledWorkerCatalog.RunnerMetadataFileName),
            metadata.ToCanonicalJson());
    }

    private static WorkerLauncher NewLauncher(InstalledWorkerCatalog catalog, IRunnerPresence presence,
        IWorkerProcessStarter starter) => new WorkerLauncher(catalog, presence, starter,
        @"Local\motif-review-runner", TimeSpan.FromSeconds(1));

    /// Never present, and counts how often it was asked, so deadline discipline is observable.
    private sealed class AbsentPresence : IRunnerPresence
    {
        public int Attempts { get; private set; }

        public bool IsRunning(string ownerMutexName)
        {
            Attempts++;
            return false;
        }
    }

    private sealed class RunningPresence : IRunnerPresence
    {
        public bool IsRunning(string ownerMutexName) => true;
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
        public void Terminate() { }
        public void Dispose() { }
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

    /// Absent for both launchers' first probe, present afterwards: two racing starts converge on one runner.
    private sealed class ConvergingPresence : IRunnerPresence
    {
        private int _attempts;
        private int _initialProbes;
        private readonly ManualResetEventSlim _bothProbed = new ManualResetEventSlim();

        public int Attempts => _attempts;

        public bool IsRunning(string ownerMutexName)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt > 2)
                return true;
            if (Interlocked.Increment(ref _initialProbes) == 2)
                _bothProbed.Set();
            _bothProbed.Wait(TimeSpan.FromSeconds(5));
            return false;
        }
    }

    private sealed class NoopProcess : IWorkerProcess
    {
        public bool HasExited => false;
        public int ExitCode => 0;
        public ProcessStartInfo StartInfo { get; } = new ProcessStartInfo();
        public void Terminate() { }
        public void Dispose() { }
    }

    /// Scripted to the launcher's own clock reads, so the last one lands exactly on the deadline.
    private sealed class ExactDeadlineClock : ILauncherClock
    {
        private readonly Queue<long> _timestamps = new Queue<long>(new[] { 0L, 1L, 1L, 10L });

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
