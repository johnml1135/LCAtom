using SIL.Motif.Contract.Projects;
using SIL.Motif.Host.Store;
using SIL.Motif.Worker.Jobs;
using SIL.Motif.Worker.Store;
using Xunit;

namespace SIL.Motif.Tests.Worker;

public sealed class WorkerRecoveryCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "motif-recovery-coordinator-" + Guid.NewGuid().ToString("N"));

    public WorkerRecoveryCoordinatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void StartupCleanupCompletesBeforeRecoveryAndTerminalCleanupIsExact()
    {
        var projectPath = Path.Combine(_root, "project.fwdata");
        var project = new ProjectLocator(projectPath, "project");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "project.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var clock = new TestClock("2026-08-23T12:00:00Z");
        var jobs = new JobRepository(database, clock);
        var running = jobs.Transition(jobs.Create(new SIL.Motif.Contract.Jobs.JobRecord(
            "coordinator", "project", "dry-run", SIL.Motif.Contract.Jobs.JobStatus.Queued, 1,
            "{}", null, clock.UtcNow.ToString("O"), clock.UtcNow.ToString("O"))),
            SIL.Motif.Contract.Jobs.JobStatus.Running);
        var jobPath = Path.Combine(_root, "project", "work", running.JobId);
        var siblingPath = Path.Combine(_root, "project", "work", "other");
        Directory.CreateDirectory(jobPath);
        Directory.CreateDirectory(siblingPath);
        File.WriteAllText(Path.Combine(jobPath, "candidate.tsv"), "candidate");

        var coordinator = new WorkerRecoveryCoordinator(new WorkerRecovery(jobs, clock),
            new WorkspaceCleaner(new WorkspaceOwnership(_root)));
        var startup = coordinator.RecoverStartup("project", clock.UtcNow);

        Assert.True(startup.Cleanup.Succeeded);
        Assert.False(Directory.Exists(jobPath));
        Assert.False(Directory.Exists(siblingPath));
        Assert.Contains(running.JobId, startup.Recovery.InterruptedJobIds);
        var retry = jobs.ListAttempts(running.LogicalJobId).SingleOrDefault(x => x.Attempt == 2);
        Assert.NotNull(retry);

        var terminalPath = Path.Combine(_root, "project", "work", retry!.JobId);
        Directory.CreateDirectory(terminalPath);
        File.WriteAllText(Path.Combine(terminalPath, "terminal.txt"), "terminal");
        var terminal = jobs.Transition(retry!.JobId, SIL.Motif.Contract.Jobs.JobStatus.Running, retry.Version);
        jobs.Transition(terminal.JobId, SIL.Motif.Contract.Jobs.JobStatus.Completed, terminal.Version);
        var cleanup = coordinator.CleanupTerminal("project", terminal.JobId);
        Assert.True(cleanup.Succeeded);
        Assert.False(Directory.Exists(terminalPath));
    }

    [Fact]
    public void CleanupFailureIsBoundedAndDoesNotSuppressRecovery()
    {
        var project = new ProjectLocator(Path.Combine(_root, "failure.fwdata"), "failure");
        using var database = MotifDatabase.OpenOwned(Path.Combine(_root, "failure.motif.db"), project,
            MotifSchema.CurrentSchema, new Version(1, 0));
        var clock = new TestClock("2026-08-23T12:00:00Z");
        var jobs = new JobRepository(database, clock);
        var running = jobs.Transition(jobs.Create(new SIL.Motif.Contract.Jobs.JobRecord(
            "failure-job", "failure", "dry-run", SIL.Motif.Contract.Jobs.JobStatus.Queued, 1,
            "{}", null, clock.UtcNow.ToString("O"), clock.UtcNow.ToString("O"))),
            SIL.Motif.Contract.Jobs.JobStatus.Running);
        var work = Path.Combine(_root, "failure", "work");
        Directory.CreateDirectory(Path.Combine(work, running.JobId));
        var result = new WorkerRecoveryCoordinator(new WorkerRecovery(jobs, clock),
            new WorkspaceCleaner(new WorkspaceOwnership(_root), new FailingFileSystem()))
            .RecoverStartup("failure", clock.UtcNow);

        Assert.NotEmpty(result.Cleanup.Failures);
        Assert.Contains(running.JobId, result.Recovery.InterruptedJobIds);
        Assert.Single(result.Recovery.RetryJobs);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class TestClock(string value) : SIL.Motif.Contract.Jobs.IJobClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse(value);
    }

    private sealed class FailingFileSystem : IWorkspaceFileSystem
    {
        public bool Exists(string path) => true;
        public FileAttributes GetAttributes(string path) => FileAttributes.Directory;
        public IReadOnlyList<string> EnumerateFileSystemEntries(string path) => throw new IOException("enumeration failure");
        public void DeleteFile(string path) => throw new IOException("delete failure");
        public void DeleteDirectory(string path) => throw new IOException("delete failure");
    }
}
