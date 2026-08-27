using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIL.Motif.Contract.Runner;
using SIL.Motif.Launcher;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerSelectorTests
{
    [Fact]
    public void SelectsNewestProductVersionThatReachesTheRequiredSchema()
    {
        var selected = WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("1.5.0", supportedSchema: 7),
            Worker("2.0.0", supportedSchema: 7),
            Worker("1.0.0", supportedSchema: 9)
        }, requiredSchema: 7);

        Assert.Equal(new Version(2, 0, 0), selected.ProductVersion);
    }

    [Fact]
    public void FiltersRunnersThatCannotReachTheRequiredSchema()
    {
        // The newest build loses: a database at generation 7 is unopenable by a runner that stops at 6.
        var selected = WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("5.0.0", supportedSchema: 6),
            Worker("4.0.0", supportedSchema: 7)
        }, requiredSchema: 7);

        Assert.Equal(new Version(4, 0, 0), selected.ProductVersion);
    }

    [Fact]
    public void ThrowsWhenNoRunnerReachesTheRequiredSchema()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WorkerSelector.SelectNewestCompatible(new[]
            {
                Worker("1.0.0", supportedSchema: 3)
            }, requiredSchema: 8));

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunnerAheadOfTheDatabaseIsStillCompatible()
    {
        // Supporting a newer generation than the database is at is fine; it migrates forward, never back.
        var selected = WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("0.0.1", supportedSchema: 9)
        }, requiredSchema: 1);

        Assert.Equal(new Version(0, 0, 1), selected.ProductVersion);
    }

    [Fact]
    public void CatalogTreatsIdenticalRegistrationAsIdempotentAndRejectsMutation()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.2.3", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var worker = new InstalledWorker(new Version(1, 2, 3), executable, 7);
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
        Register(catalog, new InstalledWorker(new Version(1, 2, 3), executable, 7));
        var manifest = Path.Combine(root.Path, "catalog", "1.2.3", "manifest.json");
        var changed = File.ReadAllText(manifest).Replace("\"supportedSchema\":7", "\"supportedSchema\":8",
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

        Assert.Throws<ArgumentException>(() => Register(catalog, new InstalledWorker(new Version(1, 2, 3), executable, 7)));
    }

    [Fact]
    public void CatalogRejectsCorruptOversizedAndMismatchedFinalManifests()
    {
        using var root = TemporaryDirectory.Create();
        var executable = Path.Combine(root.Path, "catalog", "1.2.3", "worker.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "worker");
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        Register(catalog, new InstalledWorker(new Version(1, 2, 3), executable, 7));
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
        Register(catalog, new InstalledWorker(new Version(1, 2, 3), executable, 7));
        File.AppendAllText(executable, "changed");
        Assert.Throws<InvalidDataException>(() => catalog.List());

        var outside = Path.Combine(root.Path, "outside.exe");
        File.WriteAllText(outside, "outside");
        Assert.Throws<ArgumentException>(() => Register(catalog, new InstalledWorker(new Version(2, 0), outside, 7)));
    }

    [Fact]
    public void SelectorRejectsNullAmbiguousAndInvalidRegistrations()
    {
        Assert.Throws<ArgumentNullException>(() =>
            WorkerSelector.SelectNewestCompatible(null!, requiredSchema: 7));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkerSelector.SelectNewestCompatible(new[] { Worker("1.0.0", 7) }, requiredSchema: 0));
        // Two registrations at one product version leave no defensible way to pick between them.
        Assert.Throws<InvalidOperationException>(() => WorkerSelector.SelectNewestCompatible(new[]
        {
            Worker("1.0.0", 7),
            Worker("1.0.0", 9)
        }, requiredSchema: 7));
        Assert.Throws<InvalidOperationException>(() => WorkerSelector.SelectNewestCompatible(new[]
        {
            new InstalledWorker(new Version(1, 0), "relative.exe", 7)
        }, requiredSchema: 7));
        Assert.Throws<InvalidOperationException>(() => WorkerSelector.SelectNewestCompatible(new[]
        {
            new InstalledWorker(new Version(1, 0), @"C:\Motif\worker.exe", 0)
        }, requiredSchema: 1));
    }

    [Fact]
    public async Task AnAlreadyRunningRunnerIsNotStartedAgain()
    {
        var starter = new FakeStarter();
        var launcher = NewLauncher(new FakePresence(running: true), starter);

        await launcher.EnsureRunningAsync(requiredSchema: 7);

        Assert.Empty(starter.Started);
    }

    [Fact]
    public async Task AnAbsentRunnerStartsTheNewestCandidateHiddenAndShellFree()
    {
        using var root = TemporaryDirectory.Create();
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        var executable = Path.Combine(root.Path, "catalog", "2.0", "runner.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "runner");
        Register(catalog, new InstalledWorker(new Version(2, 0), executable, 7));
        var starter = new FakeStarter();
        // Absent, then present: the runner takes the mutex on its first poll after starting.
        var launcher = NewLauncher(new FakePresence(runningAfterCalls: 1), starter, catalog);

        await launcher.EnsureRunningAsync(requiredSchema: 7);

        var started = Assert.Single(starter.Started);
        Assert.Equal(new Version(2, 0), started.Worker.ProductVersion);
        Assert.False(started.Terminated);
    }

    [Fact]
    public async Task ARunnerThatExitsBeforeTakingOwnershipFails()
    {
        using var root = TemporaryDirectory.Create();
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        var executable = Path.Combine(root.Path, "catalog", "1.0", "runner.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "runner");
        Register(catalog, new InstalledWorker(new Version(1, 0), executable, 7));
        var launcher = NewLauncher(new FakePresence(running: false), new FakeStarter(exited: true), catalog);

        var exception = await Assert.ThrowsAsync<WorkerLaunchException>(
            () => launcher.EnsureRunningAsync(requiredSchema: 7));

        Assert.Contains("exited before it took ownership", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunnerThatNeverTakesOwnershipTimesOutAndIsTerminated()
    {
        using var root = TemporaryDirectory.Create();
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        var executable = Path.Combine(root.Path, "catalog", "1.0", "runner.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "runner");
        Register(catalog, new InstalledWorker(new Version(1, 0), executable, 7));
        var starter = new FakeStarter();
        var launcher = NewLauncher(new FakePresence(running: false), starter, catalog);

        var exception = await Assert.ThrowsAsync<WorkerLaunchException>(
            () => launcher.EnsureRunningAsync(requiredSchema: 7));

        Assert.Contains("did not take ownership", exception.Message, StringComparison.Ordinal);
        // A candidate that never became the runner must not be left behind.
        Assert.True(Assert.Single(starter.Started).Terminated);
    }

    [Fact]
    public async Task NoInstalledRunnerReachingTheSchemaGivesInstallGuidance()
    {
        using var root = TemporaryDirectory.Create();
        var catalog = new InstalledWorkerCatalog(Path.Combine(root.Path, "catalog"));
        var executable = Path.Combine(root.Path, "catalog", "1.0", "runner.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "runner");
        Register(catalog, new InstalledWorker(new Version(1, 0), executable, 3));
        var launcher = NewLauncher(new FakePresence(running: false), new FakeStarter(), catalog);

        var exception = await Assert.ThrowsAsync<WorkerLaunchException>(
            () => launcher.EnsureRunningAsync(requiredSchema: 9));

        Assert.True(exception.NoCompatibleWorker);
        Assert.Contains("install or update Motif", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkerLauncher NewLauncher(IRunnerPresence presence, IWorkerProcessStarter starter,
        InstalledWorkerCatalog? catalog = null)
    {
        return new WorkerLauncher(catalog ?? new InstalledWorkerCatalog(Path.Combine(Path.GetTempPath(),
            "motif-launcher-tests", Guid.NewGuid().ToString("N"))), presence, starter,
            @"Local\motif-test-runner", TimeSpan.FromMilliseconds(250));
    }

    /// Answers the presence probe on a script, so startup polling is driven without a real process.
    private sealed class FakePresence : IRunnerPresence
    {
        private readonly bool _running;
        private readonly int _runningAfterCalls;
        private int _calls;

        public FakePresence(bool running = false, int runningAfterCalls = -1)
        {
            _running = running;
            _runningAfterCalls = runningAfterCalls;
        }

        public bool IsRunning(string ownerMutexName) =>
            _running || (_runningAfterCalls >= 0 && _calls++ >= _runningAfterCalls);
    }

    private static InstalledWorker Worker(string version, int supportedSchema) =>
        new InstalledWorker(Version.Parse(version), @"C:\Motif\worker.exe", supportedSchema);

    private static InstalledWorker Register(InstalledWorkerCatalog catalog, InstalledWorker worker)
    {
        var directory = System.IO.Path.GetDirectoryName(worker.ExecutablePath)!;
        Directory.CreateDirectory(directory);
        var metadata = new RunnerBuildMetadata(worker.ProductVersion.ToString(), worker.SupportedSchema);
        File.WriteAllText(Path.Combine(directory, InstalledWorkerCatalog.RunnerMetadataFileName),
            metadata.ToCanonicalJson());
        return catalog.Register(worker);
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
